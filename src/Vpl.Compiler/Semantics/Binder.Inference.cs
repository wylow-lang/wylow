using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

public sealed partial class Binder
{
    /// <summary>
    /// The type arguments of a generic candidate, inferred from a call's arguments (§6.7). The
    /// arguments that aren't lambdas, and the parameter types lambdas declare, are used at once. A type parameter only a lambda's return type can give waits for
    /// <see cref="FinishFromLambdas"/>, which binds the lambda's body and is run only for the one
    /// candidate left, so each lambda is still bound once.
    /// </summary>
    sealed class Inference
    {
        readonly Binder binder;
        readonly FunctionSymbol function;
        readonly VplType? owner;
        readonly TypeMap ownerMap;
        readonly Dictionary<TypeParameterSymbol, List<(BoundExpression? Expression, VplType Type)>> bindings;

        /// <summary>Type arguments a lambda's parameters were bound with, which later bindings can't change.</summary>
        readonly Dictionary<TypeParameterSymbol, VplType> fixedTypes = [];

        public Inference(Binder binder, Candidate candidate, List<Argument> arguments)
        {
            this.binder = binder;
            (function, owner) = (candidate.Symbol, candidate.Owner);
            ownerMap = owner is null ? TypeMap.Empty : TypeMap.For(owner);
            bindings = function.TypeParameters.ToDictionary(p => p, _ => new List<(BoundExpression?, VplType)>());
            for (int i = 0; i < arguments.Count; i++)
            {
                var parameter = ParameterType(i);
                foreach (var option in Options(arguments[i].Expression))
                {
                    if (option is not BoundPendingLambda lambda)
                        Unify(parameter, option, option.Type);
                    else if (lambda.DeclaredParameters is { } declared && parameter.IsFunction && parameter.FunctionParameters.Count == declared.Count)
                    {
                        foreach (var (target, type) in parameter.FunctionParameters.Zip(declared))
                        {
                            if (type.Type != VplType.Error)
                                Unify(target.Type, null, type.Type);
                        }
                    }
                }
            }
        }

        /// <summary>The type parameters nothing has given a type yet.</summary>
        public IReadOnlyList<TypeParameterSymbol> Unresolved => [.. function.TypeParameters.Where(p => bindings[p].Count == 0)];

        VplType ParameterType(int index) => ownerMap.Apply(function.Parameters[index].Type);

        void Unify(VplType parameter, BoundExpression? argument, VplType argumentType)
        {
            // Type arguments are escaping, so a non-escaping argument is an error once it is converted.
            if (parameter.TypeParameter is { } p && bindings.TryGetValue(p, out var found))
            {
                found.Add((argument, EscapingForm(argumentType)));
            }
            // A nullable argument gives `T?` its value type; null says nothing about `T`.
            else if (parameter.IsNullableValue)
            {
                if (argumentType != VplType.Null)
                    Unify(parameter.NonNull, argumentType.IsNullable ? null : argument, argumentType.NonNull);
            }
            else if (binder.stdlib.IsSpan(parameter) && parameter.Symbol != argumentType.Symbol
                     && binder.ViewedElement(argumentType, parameter.Symbol == binder.stdlib.ReadOnlySpan) is { } viewed)
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
            // Function types convert only to themselves, so their parts match exactly too.
            else if (parameter.IsFunction && argumentType.IsFunction && parameter.FunctionParameters.Count == argumentType.FunctionParameters.Count)
            {
                foreach (var (to, from) in parameter.FunctionParameters.Zip(argumentType.FunctionParameters))
                    Unify(to.Type, null, from.Type);
                Unify(parameter.FunctionReturnType!, null, argumentType.FunctionReturnType!);
            }
        }

        /// <summary>
        /// The candidate with its type arguments. While some are left to lambda bodies, those stand for
        /// themselves, and <see cref="Unresolved"/> lists them. Null, with an error, if inference fails.
        /// </summary>
        public MethodReference? Instantiate(List<Argument> arguments, out string? error)
        {
            var result = new List<VplType>();
            foreach (var parameter in function.TypeParameters)
            {
                var found = bindings[parameter];
                if (found.Count == 0)
                {
                    if (!LambdaCouldGive(parameter, arguments))
                    {
                        error = $"cannot infer type argument '{parameter.Name}' of '{function.Name}'; specify it explicitly, as in '{function.Name}<...>(...)'";
                        return null;
                    }
                    result.Add(parameter.Type);
                    continue;
                }
                // A non-escaping function can't convert to its escaping form, and is reported as that once the call is checked.
                var chosen = found.Select(b => b.Type).Distinct().FirstOrDefault(candidate =>
                    candidate != VplType.Null && found.All(b => b.Expression is not null ? binder.CanConvert(b.Expression, candidate) || IsEscapingMismatch(b.Expression.Type, candidate) : b.Type == candidate));
                if (found.All(b => b.Type == VplType.Null))
                {
                    error = $"cannot infer type argument '{parameter.Name}' of '{function.Name}' from 'null'; specify it explicitly, as in '{function.Name}<...>(...)'";
                    return null;
                }
                if (chosen is null || (fixedTypes.TryGetValue(parameter, out var fixedType) && chosen != fixedType))
                {
                    error = $"cannot infer type argument '{parameter.Name}' of '{function.Name}': it could be {string.Join(" or ", found.Select(b => $"'{b.Type}'").Distinct())}";
                    return null;
                }
                result.Add(chosen);
            }
            error = null;
            return new MethodReference(function, owner, result);
        }

        /// <summary>Whether a lambda argument's return type is all that could give <paramref name="parameter"/> a type.</summary>
        bool LambdaCouldGive(TypeParameterSymbol parameter, List<Argument> arguments) =>
            Enumerable.Range(0, arguments.Count).Any(i =>
                ParameterType(i) is { IsFunction: true } type
                && TypeParametersIn(type.FunctionReturnType!).Contains(parameter)
                && Options(arguments[i].Expression).Any(o => o is BoundPendingLambda));

        /// <summary>
        /// Whether the arguments could be passed once the rest is inferred: those whose parameter types
        /// are known must convert, and lambdas must take as many parameters as their targets.
        /// </summary>
        public bool CouldApply(MethodReference partial, List<Argument> arguments)
        {
            var unresolved = Unresolved;
            return Enumerable.Range(0, arguments.Count).All(i =>
            {
                var type = partial.ParameterType(i);
                if (!TypeParametersIn(type).Any(unresolved.Contains))
                    return binder.IsApplicable(partial, arguments, i);
                return partial.ParameterRefKind(i) != RefKind.Ref && !arguments[i].IsRef && Options(arguments[i].Expression).All(o =>
                    o is not BoundPendingLambda lambda || (type.IsFunction && type.FunctionParameters.Count == lambda.Syntax.Parameters.Count));
            });
        }

        /// <summary>
        /// Binds, in turn, each lambda whose parameter types are known and whose return type has a type
        /// parameter only lambdas can give, and infers from the values it returns as if they were arguments. Reports its
        /// own errors.
        /// </summary>
        public MethodReference? FinishFromLambdas(List<Argument> arguments, Location location)
        {
            // Every lambda that can give these is bound, so that what they return agrees.
            var inferable = Unresolved;
            bool progress = true;
            while (progress)
            {
                progress = false;
                var current = Instantiate(arguments, out var error);
                if (current is null)
                {
                    binder.Error(location, error!);
                    return null;
                }
                var unresolved = Unresolved;
                for (int i = 0; i < arguments.Count && !progress; i++)
                {
                    var target = ParameterType(i);
                    if (!target.IsFunction || !TypeParametersIn(target.FunctionReturnType!).Any(inferable.Contains))
                        continue;
                    var parameters = current.ParameterType(i).FunctionParameters;
                    if (parameters.Any(p => TypeParametersIn(p.Type).Any(unresolved.Contains)))
                        continue;
                    foreach (var lambda in Options(arguments[i].Expression).OfType<BoundPendingLambda>().Where(l => l.Inferred is null).ToList())
                    {
                        foreach (var p in target.FunctionParameters.SelectMany(p => TypeParametersIn(p.Type)).Where(bindings.ContainsKey))
                            fixedTypes.TryAdd(p, current.Map.Apply(p.Type));
                        var inferred = lambda.Inferred = binder.BindLambdaInferringReturn(lambda, parameters, target.IsEscaping);
                        if (inferred.Values.Any(v => v.Type == VplType.Error))
                            return null;
                        if (inferred.Values.Count == 0)
                        {
                            binder.Error(lambda.Location, $"this lambda returns no value, so it cannot give '{function.Name}' its type argument "
                                + $"'{TypeParametersIn(target.FunctionReturnType!).First(inferable.Contains).Name}'");
                            return null;
                        }
                        foreach (var value in inferred.Values)
                            Unify(target.FunctionReturnType!, value, value.Type);
                        progress = true;
                    }
                }
            }

            var finished = Instantiate(arguments, out var finalError);
            if (finished is not null && Unresolved is [var first, ..])
            {
                finished = null;
                finalError = $"cannot infer type argument '{first.Name}' of '{function.Name}'; specify it explicitly, as in '{function.Name}<...>(...)'";
            }
            if (finished is null)
                binder.Error(location, finalError!);
            return finished;
        }
    }

    /// <summary>
    /// The functions an argument could be: itself, or each option of a <c>?:</c> or switch expression
    /// still waiting for its function type.
    /// </summary>
    static IEnumerable<BoundExpression> Options(BoundExpression argument) => argument is BoundPendingChoice choice ? choice.Options : [argument];
}
