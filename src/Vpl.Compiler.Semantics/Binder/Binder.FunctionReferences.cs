using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Semantics;

/// <summary>
/// Function references (§6.7): the name of a free function or static method, used as a function
/// value, is the lambda that calls it with its arguments. So the parameters of the function type it
/// is converted to pick the overload, as a call's arguments would, and everything a lambda does
/// (waiting for overload resolution, generic inference, escaping) works the same way.
/// </summary>
public sealed partial class Binder
{
    /// <summary>
    /// The functions that <paramref name="expression"/>, a name or <c>Type.name</c>, refers to, if it
    /// names functions rather than a value; found as a call of it would find them.
    /// </summary>
    IReadOnlyList<FunctionSymbol>? ReferencedFunctions(Expr expression) => expression switch
    {
        NameExpr name when IsValueName(name.Name) => null,
        NameExpr name when currentType is not null && LookupMethods(currentType.Type, name.Name) is { Count: > 0 } methods =>
            Functions(methods),
        NameExpr name when types.ContainsKey(name.Name) => null,
        NameExpr name => functions.GetValueOrDefault(name.Name),
        MemberAccessExpr { Target: NameExpr target } member when !IsValueName(target.Name) && types.TryGetValue(target.Name, out var type) =>
            Functions(LookupMethods(type.Type, member.Name)),
        _ => null,
    };

    /// <summary>The methods found by a lookup, unless there are none.</summary>
    static IReadOnlyList<FunctionSymbol>? Functions(List<Candidate> methods) =>
        methods.Count == 0 ? null : [.. methods.Select(m => m.Symbol)];

    /// <summary>For a function used as a value where no function type is expected, how it could be used as one.</summary>
    string ReferenceHint(Expr expression) =>
        ReferencedFunctions(expression) is { } referenced && !referenced.Any(f => f.IsInstanceMethod)
            ? ", or use it where a function type is expected"
            : "";

    /// <summary>
    /// A function reference converted to the function type <paramref name="type"/>, or, without one,
    /// waiting for a type as an untyped lambda does.
    /// </summary>
    BoundExpression BindFunctionReference(Expr reference, IReadOnlyList<FunctionSymbol> overloads, FunctionType? type)
    {
        var name = TextOf(reference.Location);
        if (overloads.Any(f => f.IsInstanceMethod))
        {
            return ErrorExpression(reference.Location,
                $"'{name}' is an instance method, and only functions and static methods can be used as values; use a lambda that calls it");
        }
        if (type is not null)
            return BindLambda(ReferenceLambda(reference, type.Parameters), type, null);

        // Overload resolution tells lambdas apart by how many parameters they take.
        var counts = overloads.Select(f => f.Parameters.Count).Distinct().ToList();
        if (counts.Count > 1)
        {
            return ErrorExpression(reference.Location,
                $"'{name}' has overloads taking different numbers of arguments, so it can only be used where a function type is expected; use a lambda that calls it");
        }
        var parameters = Enumerable.Repeat(new FunctionParameterType(RefKind.None, VplType.Error), counts[0]).ToList();
        return new BoundPendingLambda(reference.Location, ReferenceLambda(reference, parameters), null)
        {
            Reference = p => ReferenceLambda(reference, p),
        };
    }

    /// <summary><c>(a, b) -> f(a, b)</c> for a reference to <c>f</c>, whose parameters can't be named by VPL code.</summary>
    static LambdaExpr ReferenceLambda(Expr reference, IReadOnlyList<FunctionParameterType> parameters)
    {
        var location = reference.Location;
        var names = parameters.Select((_, i) => $"#{i}").ToList();
        var arguments = parameters.Select((p, i) =>
        {
            Expr argument = new NameExpr(location, names[i], []);
            return p.RefKind == RefKind.Ref ? new RefArgExpr(location, argument) : argument;
        }).ToList();
        return new LambdaExpr(location, [.. names.Select(n => new LambdaParameterSyntax(location, RefKind.None, null, n, location))],
            new CallExpr(location, reference, arguments), null)
        {
            IsFunctionReference = true,
        };
    }
}
