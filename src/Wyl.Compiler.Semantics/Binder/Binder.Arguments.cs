using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    /// <summary>Why a candidate can't take a call's arguments, and the argument at fault, if one is.</summary>
    sealed record ArrangeFailure(Func<Location, Diagnostic> Report, int? Argument);

    /// <summary>
    /// Which argument each of the candidate's parameters takes, or -1 for one left out to its
    /// default. Positional arguments come first; named ones follow in parameter order, so the
    /// arguments are evaluated in the order the parameters are.
    /// </summary>
    int[]? Arrange(Candidate candidate, IReadOnlyList<string?> names, out ArrangeFailure? failure)
    {
        var parameters = candidate.Symbol.Parameters;
        var arrangement = Enumerable.Repeat(-1, parameters.Count).ToArray();
        int next = 0;
        failure = null;
        for (int a = 0; a < names.Count; a++)
        {
            int p;
            if (names[a] is not { } name)
            {
                p = next;
                if (p >= parameters.Count)
                {
                    failure = new(at => Catalog.ArgumentCount(at, candidate.Symbol.Name, parameters.Count, names.Count), null);
                    return null;
                }
            }
            else
            {
                p = Enumerable.Range(0, parameters.Count).FirstOrDefault(i => parameters[i].Name == name, -1);
                if (p < 0)
                {
                    failure = new(at => Catalog.NoParameterNamed(at, candidate.Symbol.Name, name), a);
                    return null;
                }
                if (arrangement[p] >= 0)
                {
                    failure = new(at => Catalog.ArgumentGivenTwice(at, name), a);
                    return null;
                }
                if (p < next)
                {
                    var previous = parameters[next - 1].Name;
                    failure = new(at => Catalog.NamedArgumentOrder(at, name, previous), a);
                    return null;
                }
            }
            arrangement[p] = a;
            next = p + 1;
        }
        for (int p = 0; p < parameters.Count; p++)
        {
            if (arrangement[p] < 0 && !HasDefault(candidate, p))
            {
                var (type, parameter, fieldType) = (candidate.Owner is null ? null : DisplayName(candidate.Owner), parameters[p], ParameterType(candidate, p).ToString());
                failure = new(at => parameter.Field is { } field
                    ? Catalog.MissingFieldValue(at, type!, field.Name, fieldType)
                    : Catalog.MissingArgument(at, candidate.Symbol.Name, parameter.Name), null);
                return null;
            }
        }
        return arrangement;
    }

    static WylType ParameterType(Candidate candidate, int index) =>
        (candidate.Owner is null ? TypeMap.Empty : TypeMap.For(candidate.Owner)).Apply(candidate.Symbol.Parameters[index].Type);

    /// <summary>
    /// Whether a call may leave the parameter out: it has a default value, or it is a memberwise
    /// constructor's field with an initializer or a type that has a default value.
    /// </summary>
    static bool HasDefault(Candidate candidate, int index)
    {
        var parameter = candidate.Symbol.Parameters[index];
        if (parameter.Field is { } field)
            return field.HasDefault(candidate.Owner is null ? TypeMap.Empty : TypeMap.For(candidate.Owner));
        return parameter.DefaultDeclaration is not null;
    }

    /// <summary>What a call passes for a parameter it leaves out, as a member of the method's owner.</summary>
    BoundExpression DefaultArgument(MethodReference method, int index, Location location)
    {
        var parameter = method.Symbol.Parameters[index];
        var type = method.ParameterType(index);
        if (parameter.Field is { } field)
            return new BoundFieldDefault(location, field, type);
        var value = DefaultOf(parameter.DefaultDeclaration!);
        if (value.Type == WylType.Error)
            return new BoundErrorExpression(location);
        // Only null can be the default of a type that involves type parameters, and it takes the type this call gives.
        return value.Type == type ? value with { Location = location } : Convert(new BoundNullLiteral(location), type);
    }

    /// <summary>
    /// Binds the default values the functions declare, and gives the methods that override or
    /// implement others the defaults of those, which they may not declare themselves.
    /// </summary>
    void BindDefaults(IReadOnlyList<FunctionSymbol> functions)
    {
        foreach (var function in functions)
        {
            foreach (var parameter in function.Parameters)
                parameter.Owner = function;
        }
        foreach (var function in functions)
        {
            if (function is { IsConstructor: true, ContainingType.Kind: TypeKind.Struct, Declaration: { } declaration, Parameters.Count: > 0 }
                && function.Parameters.All(p => p.DefaultSyntax is not null))
            {
                Error(Catalog.EveryParameterDefaulted(declaration.NameLocation, function.ContainingType!.Name));
            }
            if (function.Overridden.Count > 0 && function.Parameters.FirstOrDefault(p => p.DefaultSyntax is not null) is { } declared)
            {
                Error(Catalog.DefaultOnOverride(declared.DefaultSyntax!.Location, $"{function.ContainingType!.Name}.{function.Name}"));
            }
            foreach (var parameter in function.Parameters.Where(p => p.DefaultSyntax is not null))
                DefaultOf(parameter);
        }
        foreach (var function in functions)
            InheritDefaults(function, []);
    }

    void InheritDefaults(FunctionSymbol function, HashSet<FunctionSymbol> visiting)
    {
        if (function.Overridden.Count == 0 || !visiting.Add(function))
            return;
        foreach (var overridden in function.Overridden)
            InheritDefaults(overridden, visiting);
        for (int i = 0; i < function.Parameters.Count; i++)
        {
            var inherited = function.Overridden
                .Select(o => i < o.Parameters.Count ? o.Parameters[i].DefaultDeclaration : null)
                .OfType<LocalSymbol>()
                .Distinct()
                .ToList();
            if (inherited.Count > 1)
            {
                Error(Catalog.ConflictingInheritedDefaults(function.Declaration!.NameLocation, $"{function.ContainingType!.Name}.{function.Name}", function.Parameters[i].Name, inherited.Select(p => (p.DefaultSyntax!.Location, $"{p.Owner!.ContainingType!.Name}.{p.Owner.Name}"))));
            }
            function.Parameters[i].DefaultDeclaration = inherited.FirstOrDefault();
        }
    }

    readonly HashSet<LocalSymbol> bindingDefaults = [];

    /// <summary>
    /// A parameter's default value, bound in the scope of the function declaring it when first
    /// needed, since it may construct a struct whose own defaults are not bound yet.
    /// </summary>
    BoundExpression DefaultOf(LocalSymbol parameter)
    {
        if (parameter.Default is { } bound)
            return bound;
        var syntax = parameter.DefaultSyntax!;
        if (!bindingDefaults.Add(parameter))
        {
            Error(Catalog.DefaultDependsOnItself(syntax.Location, parameter.Name));
            return parameter.Default = new BoundErrorExpression(syntax.Location);
        }

        var function = parameter.Owner!;
        var saved = (currentFunction, currentType, typeParameters, hasThis, noThisContext, scope);
        currentFunction = function;
        currentType = function.ContainingType;
        typeParameters = [.. currentType?.TypeParameters ?? [], .. function.TypeParameters];
        hasThis = false;
        noThisContext = NoThisContext.DefaultValue;
        scope = new Scope(null);
        foreach (var p in function.Parameters)
            scope.Add(p);
        var value = Convert(BindExpression(syntax, parameter.Type), parameter.Type);
        (currentFunction, currentType, typeParameters, hasThis, noThisContext, scope) = saved;

        if (value.Type != WylType.Error && !IsConstantDefault(value))
        {
            Error(Catalog.NonConstantDefault(syntax.Location));
            value = new BoundErrorExpression(syntax.Location);
        }
        else if (value.Type != WylType.Error && parameter.Type.ContainsTypeParameters && !IsNull(value))
        {
            Error(Catalog.GenericDefault(syntax.Location, parameter.Name, parameter.Type.ToString()));
            value = new BoundErrorExpression(syntax.Location);
        }
        bindingDefaults.Remove(parameter);
        return parameter.Default = value;
    }

    /// <summary>A literal, an operator on constants, an enum variant, or a struct constructor given constants.</summary>
    static bool IsConstantDefault(BoundExpression value) => value switch
    {
        // An error is reported already, as a default that depends on itself is in the call that needs it.
        BoundLiteral or BoundNullLiteral or BoundFieldDefault or BoundErrorExpression => true,
        BoundConversion conversion => IsConstantDefault(conversion.Operand),
        BoundUnary unary => IsConstantDefault(unary.Operand),
        BoundBinary binary => IsConstantDefault(binary.Left) && IsConstantDefault(binary.Right),
        BoundVariant variant => variant.Arguments.All(IsConstantDefault),
        BoundConstruction construction => construction.Arguments.All(IsConstantDefault),
        _ => false,
    };

    static bool IsNull(BoundExpression value) => value is BoundNullLiteral || (value is BoundConversion conversion && IsNull(conversion.Operand));

    /// <summary>The names arguments are passed by, after checking that no positional argument follows a named one.</summary>
    List<string?> ArgumentNames(IReadOnlyList<Expr> arguments)
    {
        bool named = false;
        foreach (var argument in arguments)
        {
            if (argument is NamedArgExpr)
                named = true;
            else if (named)
                Error(Catalog.PositionalAfterNamed(argument.Location));
        }
        return [.. arguments.Select(a => a is NamedArgExpr n ? n.Name : null)];
    }
}
