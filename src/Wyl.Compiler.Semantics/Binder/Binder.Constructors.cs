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
            Error(call.Callee.Location, $"'{currentType.Name}' has no base class, so it cannot call 'super(...)'");
            return null;
        }
        if (AnyError(arguments))
            return null;
        var constructor = Resolve(call.Location, $"constructor of '{called}'", candidates!, null, arguments);
        if (constructor is null)
            return null;
        CheckVisible(call.Location, constructor.Symbol.Reach, $"constructor '{constructor}'");
        if (!CheckUnsafeCall(call.Location, constructor) || !CheckDefaultValue(call.Location, called, constructor))
            return null;
        var converted = ConvertArguments(constructor, arguments);
        // A class's constructor call is the C++ mem-initializer, where nothing can be held.
        for (int i = 0; i < converted.Count && currentType.Kind == TypeKind.Class; i++)
        {
            var parameter = constructor.ParameterType(i);
            if ((constructor.ParameterRefKind(i) != RefKind.None || parameter.IsNullableReference || parameter.IsRefStruct) && !IsHeldByCaller(converted[i]))
                Error(converted[i].Location, $"an argument to '{(chains ? "this" : "super")}(...)' for a reference or view parameter must be a parameter, a field of one, or null, for now");
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
            Error(location, $"'{baseType}' has no parameterless constructor, so this constructor must start with 'super(...)'");
            return null;
        }
        var reference = new MethodReference(constructor.Symbol, baseType, []);
        CheckVisible(location, reference.Symbol.Reach, $"constructor '{reference}'");
        if (!CheckUnsafeCall(location, reference))
            return null;
        return new BoundConstructorCall(location, reference, [], Chains: false);
    }

    /// <summary>
    /// Java's "recursive constructor invocation": a constructor whose <c>this(...)</c> chain comes
    /// back to it would never finish. Each constructor on the cycle is reported.
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
            var next = call.Constructor.Symbol;
            var seen = new HashSet<FunctionSymbol>();
            while (next != constructor && seen.Add(next) && chains.TryGetValue(next, out var further))
                next = further.Constructor.Symbol;
            if (next == constructor)
                Error(call.Location, "recursive constructor invocation: this 'this(...)' chain calls the constructor it starts from");
        }
    }

    static List<Candidate> ConstructorsOf(WylType type)
    {
        var s = type.Symbol!;
        if (s.Kind == TypeKind.Struct)
            return [.. s.Constructors.Append(s.DefaultConstructor).Select(c => new Candidate(c, type))];
        return [.. (s.Constructors.Count > 0 ? s.Constructors : [s.DefaultConstructor]).Select(c => new Candidate(c, type))];
    }

    /// <summary>A struct's <c>T()</c> exists only if every field has a default (06-syntax.md, Struct constructors).</summary>
    bool CheckDefaultValue(Location location, WylType type, MethodReference constructor)
    {
        if (!type.IsStruct || !constructor.Symbol.IsImplicit || type.FindNonDefaultableField() is not { } missing)
            return true;
        Error(location,
            $"'{type}' has no default value because field '{missing.Name}' has type '{TypeMap.For(type).Apply(missing.Type)}'; pass it by name, as in '{type}({missing.Name}: ...)'");
        return false;
    }

    BoundFunction BindImplicitConstructor(TypeSymbol s)
    {
        var location = s.Declaration.NameLocation;
        foreach (var field in s.Fields.Where(f => f.RequiresAssignment))
        {
            Error(field.Declaration.NameLocation,
                $"field '{field.Name}' of type '{field.Type}' has no default value; give it an initializer or assign it in a constructor");
        }
        currentType = s;
        var super = ImplicitSuperCall(s, location);
        currentType = null;
        return new BoundFunction(s.DefaultConstructor, new BoundBlock(location, super is null ? [] : [super]));
    }
}
