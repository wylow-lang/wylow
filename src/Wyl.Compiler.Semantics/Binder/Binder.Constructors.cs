using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    /// <summary>
    /// Binds a constructor. A class constructor's first statement is always its constructor call:
    /// <c>this(...)</c>, <c>super(...)</c>, or the base's parameterless constructor if it has a base.
    /// A struct constructor's is <c>this(...)</c> if it chains.
    /// </summary>
    BoundBlock BindConstructorBody(FunctionSymbol constructor)
    {
        var block = constructor.Body!;
        var statements = new List<BoundStatement>();
        var rest = block.Statements;
        var isClass = currentType!.Kind == TypeKind.Class;
        var first = block.Statements.FirstOrDefault() is ExprStmt { Expression: CallExpr { Callee: ThisExpr or SuperExpr } written } ? written : null;
        if (first is { Callee: ThisExpr } || (isClass && first is { Callee: SuperExpr }))
        {
            if (BindConstructorCall(first!) is { } bound)
                statements.Add(bound);
            rest = [.. block.Statements.Skip(1)];
        }
        else if (isClass && ImplicitSuperCall(currentType, constructor.Declaration!.NameLocation) is { } super)
        {
            statements.Add(super);
        }

        scope = new Scope(scope);
        statements.AddRange(rest.Select(BindStatement));
        scope = scope.Parent;
        return new BoundBlock(block.Location, statements);
    }

    /// <summary>
    /// <c>super(...)</c>, a call of the base class's constructor, or <c>this(...)</c>, of another
    /// constructor of the type's own. Neither's arguments can use <c>this</c>, which isn't made yet.
    /// </summary>
    BoundConstructorCall? BindConstructorCall(CallExpr call)
    {
        bool chains = call.Callee is ThisExpr;
        var called = chains ? currentType!.Type : currentType!.Type.BaseType;
        var candidates = called is null ? null : ConstructorsOf(called);
        hasThis = false;
        noThisContext = chains ? NoThisContext.ChainArguments : NoThisContext.SuperArguments;
        var arguments = BindArguments(call.Arguments, candidates);
        hasThis = true;
        noThisContext = NoThisContext.Static;

        if (called is null)
        {
            Error(Catalog.SuperCallWithoutBase(call.Callee.Location, currentType.Name));
            return null;
        }
        if (AnyError(arguments))
            return null;
        var constructor = Resolve(call.Location, $"constructor of '{called}'", candidates!, null, arguments);
        if (constructor is null)
            return null;
        CheckVisible(call.Location, constructor.Symbol.Reach, $"constructor '{constructor}'");
        CheckMemberBounds(constructor, call.Location);
        if (!CheckUnsafeCall(call.Location, constructor) || !CheckDefaultValue(call.Location, called, constructor))
            return null;
        var converted = ConvertArguments(constructor, arguments);
        // A class's constructor call is the C++ mem-initializer, where nothing can be held.
        for (int i = 0; i < converted.Count && currentType.Kind == TypeKind.Class; i++)
        {
            var parameter = constructor.ParameterType(i);
            if ((constructor.ParameterRefKind(i) != RefKind.None || parameter.IsNullableReference || parameter.IsRefStruct) && !IsHeldByCaller(converted[i]))
                Error(Catalog.ConstructorCallArgumentNotHeld(converted[i].Location, chains ? "this" : "super"));
        }
        return new BoundConstructorCall(call.Location, constructor, converted, chains);
    }

    /// <summary>
    /// Whether a reference or view argument points into storage the constructor's caller keeps
    /// alive, so that passing it needs nothing held and nothing computed before the constructor call.
    /// </summary>
    static bool IsHeldByCaller(BoundExpression argument)
    {
        if (argument is BoundConversion { Operand.Type: var from } && from == WylType.Null)
            return true;
        var path = PlacePath.Of(argument);
        return path is { Base: PlaceBase.Local, Root: BoundLocalAccess { Local.IsParameter: true, AssumesNonNull: false } }
            && path.Steps.All(s => s is BoundFieldAccess or BoundConversion);
    }

    /// <summary>The base constructor call of a constructor without <c>super(...)</c>: the base's parameterless constructor.</summary>
    BoundConstructorCall? ImplicitSuperCall(TypeSymbol s, Location location)
    {
        if (s.Type.BaseType is not { } baseType)
            return null;
        var constructor = ConstructorsOf(baseType).FirstOrDefault(c => c.Symbol.Parameters.Count == 0);
        if (constructor is null)
        {
            Error(Catalog.NoParameterlessBaseConstructor(location, baseType.ToString()));
            return null;
        }
        var reference = new MethodReference(constructor.Symbol, baseType, []);
        CheckVisible(location, reference.Symbol.Reach, $"constructor '{reference}'");
        if (!CheckUnsafeCall(location, reference))
            return null;
        return new BoundConstructorCall(location, reference, [], Chains: false);
    }

    /// <summary>
    /// A constructor whose <c>this(...)</c> chain comes back to it would never finish. Each
    /// constructor on the cycle is reported, with the steps that lead back to it.
    /// </summary>
    void CheckConstructorCycles(IEnumerable<BoundFunction> bodies)
    {
        var chains = bodies
            .Where(b => b.Symbol.IsConstructor)
            .Select(b => (b.Symbol, Call: b.Body.Statements.FirstOrDefault() as BoundConstructorCall))
            .Where(b => b.Call is { Chains: true })
            .ToDictionary(b => b.Symbol, b => b.Call!);
        foreach (var (constructor, call) in chains)
        {
            var steps = new List<(string From, string To, Location At)>();
            var seen = new HashSet<FunctionSymbol>();
            var (current, step) = (constructor, call);
            while (true)
            {
                var next = step.Constructor.Symbol;
                steps.Add((current.Signature, next.Signature, step.Location));
                if (next == constructor)
                {
                    Error(Catalog.ConstructorCycle(call.Location, constructor.Signature, steps));
                    break;
                }
                if (!seen.Add(next) || !chains.TryGetValue(next, out var further))
                    break;
                (current, step) = (next, further);
            }
        }
    }

    static List<Candidate> ConstructorsOf(WylType type)
    {
        var s = type.Symbol!;
        if (s.Kind == TypeKind.Struct)
            return [.. s.Constructors.Append(s.DefaultConstructor).Select(c => new Candidate(c, type))];
        return [.. (s.Constructors.Count > 0 ? s.Constructors : [s.DefaultConstructor]).Select(c => new Candidate(c, type))];
    }

    /// <summary>A struct's <c>T()</c> exists only if every field has a default (spec structs.constructors).</summary>
    bool CheckDefaultValue(Location location, WylType type, MethodReference constructor)
    {
        if (!type.IsStruct || !constructor.Symbol.IsImplicit || type.FindNonDefaultableField() is not { } missing)
            return true;
        Error(Catalog.NoDefaultValue(location, type.ToString(), missing.Name, TypeMap.For(type).Apply(missing.Type).ToString()));
        return false;
    }

    BoundFunction BindImplicitConstructor(TypeSymbol s)
    {
        var location = s.Declaration.NameLocation;
        foreach (var field in s.Fields.Where(f => f.RequiresAssignment))
        {
            Error(Catalog.FieldWithoutDefault(field.Declaration.NameLocation, field.Name, field.Type.ToString()));
        }
        currentType = s;
        var super = ImplicitSuperCall(s, location);
        currentType = null;
        return new BoundFunction(s.DefaultConstructor, new BoundBlock(location, super is null ? [] : [super]));
    }
}
