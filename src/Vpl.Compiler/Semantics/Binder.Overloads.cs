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
        var waiting = new List<(Inference Inference, MethodReference Partial)>();
        string? inferenceError = null;
        foreach (var candidate in sameArity)
        {
            if (!candidate.Symbol.IsGeneric || typeArguments is not null)
            {
                instantiated.Add(new MethodReference(candidate.Symbol, candidate.Owner, typeArguments ?? []));
                continue;
            }
            var inference = new Inference(this, candidate, arguments);
            if (inference.Instantiate(arguments, out inferenceError) is not { } method)
                continue;
            if (inference.Unresolved.Count == 0)
                instantiated.Add(method);
            else
                waiting.Add((inference, method));
        }
        if (instantiated.Count == 0 && waiting.Count == 0)
        {
            Error(location, inferenceError!);
            return null;
        }

        if (sameArity.Count == 1 && ExplainRefMismatch(instantiated.FirstOrDefault() ?? waiting[0].Partial, arguments) is { } mismatch)
        {
            Error(mismatch.Location, mismatch.Message);
            return null;
        }

        var applicable = instantiated.Where(m => IsApplicable(m, arguments)).ToList();
        waiting = [.. waiting.Where(w => w.Inference.CouldApply(w.Partial, arguments))];
        if (waiting.Count > 0)
        {
            // A lambda's body is bound once, with the parameter types of the one candidate left.
            if (waiting.Count + applicable.Count > 1)
            {
                var (inference, partial) = waiting[0];
                var name = partial.Symbol.Name;
                Error(location, $"cannot infer type argument '{inference.Unresolved[0].Name}' of '{name}' from a lambda, since other overloads also take these arguments; "
                    + $"specify it explicitly, as in '{name}<...>(...)'");
                return null;
            }
            if (waiting[0].Inference.FinishFromLambdas(arguments, location) is not { } finished)
                return null;
            applicable = IsApplicable(finished, arguments) ? [finished] : [];
        }
        if (applicable.Count == 0)
        {
            if (NonEscapingArgument(instantiated, arguments) is { } nonEscaping)
            {
                ReportNotEscaping(nonEscaping.Argument.Expression, nonEscaping.Parameter);
                return null;
            }
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

    bool IsApplicable(MethodReference method, List<Argument> arguments) =>
        Enumerable.Range(0, arguments.Count).All(i => IsApplicable(method, arguments, i));

    bool IsApplicable(MethodReference method, List<Argument> arguments, int i) =>
        method.ParameterRefKind(i) == RefKind.Ref
            ? arguments[i].IsRef && arguments[i].Expression.Type == method.ParameterType(i)
            : !arguments[i].IsRef && CanConvert(arguments[i].Expression, method.ParameterType(i));

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
