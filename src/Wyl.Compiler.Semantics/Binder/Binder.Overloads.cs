using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    /// <summary>
    /// Picks the overload to call. Type arguments are inferred from the arguments unless given explicitly.
    /// Among equally good candidates one that leaves fewer parameters to their defaults wins, then a
    /// non-generic one. On success, <paramref name="arguments"/> is rearranged to one per parameter,
    /// with the defaults of those left out filled in.
    /// </summary>
    MethodReference? Resolve(
        Location location,
        string description,
        IReadOnlyList<Candidate> candidates,
        IReadOnlyList<WylType>? typeArguments,
        List<Argument> arguments)
    {
        var names = arguments.Select(a => a.Name).ToList();
        var fitting = candidates.Where(c => typeArguments is null || c.Symbol.TypeParameters.Count == typeArguments.Count).ToList();
        var arranged = new Dictionary<FunctionSymbol, (int[] ArgumentOf, List<Argument?> Arguments)>();
        var failures = new List<(Candidate Candidate, ArrangeFailure Failure)>();
        foreach (var candidate in fitting)
        {
            if (Arrange(candidate, names, out var failure) is { } arrangement)
                arranged[candidate.Symbol] = (arrangement, [.. arrangement.Select(a => a < 0 ? null : arguments[a])]);
            else
                failures.Add((candidate, failure!));
        }
        var arrangeable = fitting.Where(c => arranged.ContainsKey(c.Symbol)).ToList();
        if (arrangeable.Count == 0)
        {
            // The one candidate that has every name given is the one meant, as is a lone candidate with defaults.
            var meant = failures.Count == 1 ? failures
                : failures.Where(f => names.All(n => n is null || f.Candidate.Symbol.Parameters.Any(p => p.Name == n))).ToList();
            if (meant.Count != 1 || (names.All(n => n is null) && !Enumerable.Range(0, meant[0].Candidate.Symbol.Parameters.Count).Any(i => HasDefault(meant[0].Candidate, i))))
                meant = [];
            if (typeArguments is not null && candidates.Any(c => Arrange(c, names, out _) is not null))
                Error(location, $"no {description} takes {typeArguments.Count} type argument{(typeArguments.Count == 1 ? "" : "s")}");
            else if (meant is [var (_, failure)])
                Error(failure.Argument is { } a ? arguments[a].NameLocation : location, failure.Message);
            else if (names.Any(n => n is not null))
                Error(location, $"no {description} takes these named arguments");
            else
                Error(location, $"no {description} takes {arguments.Count} argument{(arguments.Count == 1 ? "" : "s")}");
            return null;
        }

        List<Argument?> ArgumentsOf(MethodReference m) => arranged[m.Symbol].Arguments;
        int Defaults(MethodReference m) => arranged[m.Symbol].ArgumentOf.Count(a => a < 0);

        var instantiated = new List<MethodReference>();
        var waiting = new List<(Inference Inference, MethodReference Partial)>();
        string? inferenceError = null;
        foreach (var candidate in arrangeable)
        {
            if (!candidate.Symbol.IsGeneric || typeArguments is not null)
            {
                instantiated.Add(new MethodReference(candidate.Symbol, candidate.Owner, typeArguments ?? []));
                continue;
            }
            var candidateArguments = arranged[candidate.Symbol].Arguments;
            var inference = new Inference(this, candidate, candidateArguments);
            if (inference.Instantiate(candidateArguments, out inferenceError) is not { } method)
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

        var first = instantiated.FirstOrDefault() ?? waiting[0].Partial;
        if (arrangeable.Count == 1 && ExplainRefMismatch(first, ArgumentsOf(first)) is { } mismatch)
        {
            Error(mismatch.Location, mismatch.Message);
            return null;
        }

        var applicable = instantiated.Where(m => IsApplicable(m, ArgumentsOf(m))).ToList();
        waiting = [.. waiting.Where(w => w.Inference.CouldApply(w.Partial, ArgumentsOf(w.Partial)))];
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
            var (lastInference, lastPartial) = waiting[0];
            if (lastInference.FinishFromLambdas(ArgumentsOf(lastPartial), location) is not { } finished)
                return null;
            applicable = IsApplicable(finished, ArgumentsOf(finished)) ? [finished] : [];
        }
        if (applicable.Count == 0)
        {
            if (NonEscapingArgument([.. instantiated.Select(m => (m, (IReadOnlyList<Argument?>)ArgumentsOf(m)))]) is { } nonEscaping)
            {
                ReportNotEscaping(nonEscaping.Expression);
                return null;
            }
            // With one candidate, converting the lambda says what is wrong with it.
            if (arrangeable.Count == 1 && instantiated is [var only]
                && ArgumentsOf(only) is var onlyArguments
                && Enumerable.Range(0, onlyArguments.Count).FirstOrDefault(i => !IsApplicable(only, onlyArguments, i)) is var failing
                && onlyArguments[failing]?.Expression is BoundPendingLambda or BoundPendingChoice)
            {
                Convert(onlyArguments[failing]!.Expression, only.ParameterType(failing));
                return null;
            }
            var types = string.Join(", ", arguments.Select(a => (a.Name is null ? "" : a.Name + ": ") + (a.IsRef ? "ref " : "") + DescribeArgument(a.Expression)));
            Error(location, $"no {description} accepts ({types})");
            return null;
        }

        MethodReference? Choose(List<MethodReference> best)
        {
            var fewest = best.Min(Defaults);
            best = [.. best.Where(m => Defaults(m) == fewest)];
            // Overloads differ in their parameters, so only defaults can leave two non-generic ones equally good.
            if (best.Count(m => !m.Symbol.IsGeneric) > 1)
            {
                Error(location, $"call is ambiguous between {string.Join(" and ", best.Where(m => !m.Symbol.IsGeneric).Select(m => $"'{m}'"))}");
                return null;
            }
            var method = best.FirstOrDefault(m => !m.Symbol.IsGeneric) ?? best[0];
            var filled = ArgumentsOf(method).Select((a, i) => a ?? new Argument(DefaultArgument(method, i, location), false, location)).ToList();
            arguments.Clear();
            arguments.AddRange(filled);
            return method;
        }

        var exact = applicable
            .Where(m => ArgumentsOf(m).Select((a, i) => a is null || m.ParameterType(i) == a.Expression.Type).All(e => e))
            .ToList();
        if (exact.Count > 0)
            return Choose(exact);

        // Java's rule: pick the overload whose parameters all convert to every other candidate's parameters.
        bool IsMoreSpecific(MethodReference m, MethodReference n) =>
            Enumerable.Range(0, arguments.Count).All(a =>
                Conversions.IsImplicit(m.ParameterType(Array.IndexOf(arranged[m.Symbol].ArgumentOf, a)), n.ParameterType(Array.IndexOf(arranged[n.Symbol].ArgumentOf, a))));
        var mostSpecific = applicable.Where(m => applicable.All(n => n == m || IsMoreSpecific(m, n))).ToList();
        if (mostSpecific.Count > 1)
        {
            var fewest = mostSpecific.Min(Defaults);
            mostSpecific = [.. mostSpecific.Where(m => Defaults(m) == fewest)];
        }
        if (mostSpecific.Count > 1 && mostSpecific.Count(m => !m.Symbol.IsGeneric) == 1)
            mostSpecific = [.. mostSpecific.Where(m => !m.Symbol.IsGeneric)];
        if (mostSpecific.Count == 1)
            return Choose(mostSpecific);

        Error(location, $"call is ambiguous between {string.Join(" and ", applicable.Select(m => $"'{m}'"))}");
        return null;
    }

    /// <summary>Whether every argument converts to its parameter. A parameter left out to its default takes none.</summary>
    bool IsApplicable(MethodReference method, IReadOnlyList<Argument?> arguments) =>
        Enumerable.Range(0, arguments.Count).All(i => IsApplicable(method, arguments, i));

    bool IsApplicable(MethodReference method, IReadOnlyList<Argument?> arguments, int i) =>
        arguments[i] is not { } argument
        || (method.ParameterRefKind(i) == RefKind.Ref
            ? argument.IsRef ? RefArgumentFits(argument.Expression, method.ParameterType(i)) : IsNullForReference(argument, method.ParameterType(i))
            : !argument.IsRef && CanConvert(argument.Expression, method.ParameterType(i)));

    /// <summary>
    /// Whether a <c>ref</c> argument has exactly the type its parameter refers to. A <c>ref?</c>
    /// local passed to a <c>ref</c> parameter is narrowed, and a <c>ref?</c> parameter also takes
    /// another reference that may refer to nothing.
    /// </summary>
    static bool RefArgumentFits(BoundExpression argument, WylType parameter) =>
        parameter is NullableReferenceType reference
            ? argument.Type.Referred == reference.Referenced
            : argument.Type == parameter || (argument is BoundLocalAccess && argument.Type is NullableReferenceType { Referenced: var referenced } && referenced == parameter);

    /// <summary>A <c>ref?</c> parameter takes <c>null</c>, which needs no <c>ref</c>.</summary>
    static bool IsNullForReference(Argument argument, WylType parameter) =>
        !argument.IsRef && argument.Expression.Type == WylType.Null && parameter.IsNullableReference;

    (Location Location, string Message)? ExplainRefMismatch(MethodReference method, IReadOnlyList<Argument?> arguments)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is not { } argument)
                continue;
            var what = argument.Name is { } name ? $"argument '{name}'" : $"argument {i + 1}";
            var parameter = method.ParameterType(i);
            bool isRefParameter = method.ParameterRefKind(i) == RefKind.Ref;
            if (isRefParameter && !argument.IsRef && !IsNullForReference(argument, parameter))
                return (argument.Location, $"{what} must be passed with 'ref'");
            if (!isRefParameter && argument.IsRef)
                return (argument.Location, $"{what} must not be passed with 'ref'");
            if (isRefParameter && argument.IsRef && !RefArgumentFits(argument.Expression, parameter))
            {
                return (argument.Location, argument.Expression.Type is NullableReferenceType { Referenced: var referenced } && referenced == parameter
                    ? MayReferToNothingMessage(argument.Expression)
                    : $"a 'ref' argument must have exactly type '{parameter.Referred}', not '{argument.Expression.Type.Referred}'");
            }
        }
        return null;
    }
}
