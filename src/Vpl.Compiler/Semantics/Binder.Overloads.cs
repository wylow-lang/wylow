using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

public sealed partial class Binder
{
    /// <summary>
    /// Picks the overload to call. Type arguments are inferred from the arguments unless given explicitly.
    /// Among equally good candidates a non-generic one wins.
    /// </summary>
    MethodReference? Resolve(
        Location location,
        string description,
        IReadOnlyList<Candidate> candidates,
        IReadOnlyList<VplType>? typeArguments,
        List<Argument> arguments)
    {
        var sameArity = candidates
            .Where(c => c.Symbol.Parameters.Count == arguments.Count && (typeArguments is null || c.Symbol.TypeParameters.Count == typeArguments.Count))
            .ToList();
        if (sameArity.Count == 0)
        {
            if (typeArguments is not null && candidates.Any(c => c.Symbol.Parameters.Count == arguments.Count))
                Error(location, $"no {description} takes {typeArguments.Count} type argument{(typeArguments.Count == 1 ? "" : "s")}");
            else
                Error(location, $"no {description} takes {arguments.Count} argument{(arguments.Count == 1 ? "" : "s")}");
            return null;
        }

        var instantiated = new List<MethodReference>();
        string? inferenceError = null;
        foreach (var candidate in sameArity)
        {
            var inferred = candidate.Symbol.IsGeneric
                ? typeArguments ?? InferTypeArguments(candidate.Symbol, candidate.Owner, arguments, out inferenceError)
                : [];
            if (inferred is not null)
                instantiated.Add(new MethodReference(candidate.Symbol, candidate.Owner, inferred));
        }
        if (instantiated.Count == 0)
        {
            Error(location, inferenceError!);
            return null;
        }

        if (sameArity.Count == 1 && ExplainRefMismatch(instantiated[0], arguments) is { } mismatch)
        {
            Error(mismatch.Location, mismatch.Message);
            return null;
        }

        var applicable = instantiated.Where(m => IsApplicable(m, arguments)).ToList();
        if (applicable.Count == 0)
        {
            var types = string.Join(", ", arguments.Select(a => (a.IsRef ? "ref " : "") + a.Expression.Type));
            Error(location, $"no {description} accepts ({types})");
            return null;
        }

        var exact = applicable
            .Where(m => Enumerable.Range(0, arguments.Count).All(i => m.ParameterType(i) == arguments[i].Expression.Type))
            .ToList();
        if (exact.Count > 0)
            return exact.FirstOrDefault(m => !m.Symbol.IsGeneric) ?? exact[0];

        // Java's rule: pick the overload whose parameters all convert to every other candidate's parameters.
        var mostSpecific = applicable.Where(m => applicable.All(n => n == m || IsMoreSpecific(m, n))).ToList();
        if (mostSpecific.Count > 1 && mostSpecific.Count(m => !m.Symbol.IsGeneric) == 1)
            mostSpecific = [.. mostSpecific.Where(m => !m.Symbol.IsGeneric)];
        if (mostSpecific.Count == 1)
            return mostSpecific[0];

        Error(location, $"call is ambiguous between {string.Join(" and ", applicable.Select(m => $"'{m}'"))}");
        return null;
    }

    /// <summary>
    /// Infers each type parameter from the arguments in its positions. When positions disagree, the
    /// type that every argument converts to wins, so <c>max(1, 2L)</c> infers <c>long</c>.
    /// </summary>
    IReadOnlyList<VplType>? InferTypeArguments(FunctionSymbol function, VplType? owner, List<Argument> arguments, out string? error)
    {
        var ownerMap = owner is null ? TypeMap.Empty : TypeMap.For(owner);
        var bindings = function.TypeParameters.ToDictionary(p => p, _ => new List<(BoundExpression? Expression, VplType Type)>());

        void Unify(VplType parameter, BoundExpression? argument, VplType argumentType)
        {
            if (parameter.TypeParameter is { } p && bindings.TryGetValue(p, out var found))
            {
                found.Add((argument, argumentType));
            }
            // A nullable argument gives `T?` its value type; null says nothing about `T`.
            else if (parameter.IsNullableValue)
            {
                if (argumentType != VplType.Null)
                    Unify(parameter.NonNull, argumentType.IsNullable ? null : argument, argumentType.NonNull);
            }
            else if (stdlib.IsSpan(parameter) && parameter.Symbol != argumentType.Symbol
                     && ViewedElement(argumentType, parameter.Symbol == stdlib.ReadOnlySpan) is { } viewed)
            {
                Unify(parameter.TypeArguments[0], null, viewed);
            }
            else if ((parameter.IsPointer && argumentType.IsPointer)
                     || (parameter.IsFixedArray && argumentType.IsFixedArray && parameter.FixedLength == argumentType.FixedLength))
            {
                Unify(parameter.ElementType!, null, argumentType.ElementType!);
            }
            else if (parameter.Symbol is not null && parameter.Symbol == argumentType.Symbol)
            {
                // Inside a type argument there are no conversions, so these must match exactly.
                for (int i = 0; i < parameter.TypeArguments.Count; i++)
                    Unify(parameter.TypeArguments[i], null, argumentType.TypeArguments[i]);
            }
        }

        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].Expression is not BoundPendingLambda)
                Unify(ownerMap.Apply(function.Parameters[i].Type), arguments[i].Expression, arguments[i].Expression.Type);
        }

        var result = new List<VplType>();
        foreach (var parameter in function.TypeParameters)
        {
            var found = bindings[parameter];
            if (found.Count == 0)
            {
                error = $"cannot infer type argument '{parameter.Name}' of '{function.Name}'; specify it explicitly, as in '{function.Name}<...>(...)'";
                return null;
            }
            var chosen = found.Select(b => b.Type).Distinct().FirstOrDefault(candidate =>
                candidate != VplType.Null && found.All(b => b.Expression is not null ? CanConvert(b.Expression, candidate) : b.Type == candidate));
            if (chosen is null)
            {
                error = $"cannot infer type argument '{parameter.Name}' of '{function.Name}': it could be {string.Join(" or ", found.Select(b => $"'{b.Type}'").Distinct())}";
                return null;
            }
            result.Add(chosen);
        }
        error = null;
        return result;
    }

    bool IsApplicable(MethodReference method, List<Argument> arguments) =>
        Enumerable.Range(0, arguments.Count).All(i => method.ParameterRefKind(i) == RefKind.Ref
            ? arguments[i].IsRef && arguments[i].Expression.Type == method.ParameterType(i)
            : !arguments[i].IsRef && CanConvert(arguments[i].Expression, method.ParameterType(i)));

    static (Location Location, string Message)? ExplainRefMismatch(MethodReference method, List<Argument> arguments)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            bool isRefParameter = method.ParameterRefKind(i) == RefKind.Ref;
            if (isRefParameter && !argument.IsRef)
                return (argument.Location, $"argument {i + 1} must be passed with 'ref'");
            if (!isRefParameter && argument.IsRef)
                return (argument.Location, $"argument {i + 1} must not be passed with 'ref'");
            if (isRefParameter && argument.Expression.Type != method.ParameterType(i))
                return (argument.Location, $"a 'ref' argument must have exactly type '{method.ParameterType(i)}', not '{argument.Expression.Type}'");
        }
        return null;
    }

    static bool IsMoreSpecific(MethodReference m, MethodReference n) =>
        Enumerable.Range(0, m.Symbol.Parameters.Count).All(i => Conversions.IsImplicit(m.ParameterType(i), n.ParameterType(i)));
}
