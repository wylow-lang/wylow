using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    /// <summary>
    /// The type arguments of a generic candidate, inferred from a call's arguments (spec generics.arguments.inference). The
    /// arguments that aren't lambdas, and the parameter types lambdas declare, are used at once. A
    /// type parameter only a lambda's return type can give waits for <see cref="FinishFromLambdas"/>,
    /// which binds the lambda's body and is run only for the one candidate left, so each lambda is
    /// still bound once.
    /// </summary>
    sealed class Inference
    {
        readonly Binder binder;
        readonly FunctionSymbol function;
        readonly WylType? owner;
        readonly TypeMap ownerMap;
        readonly Dictionary<TypeParameterSymbol, List<(BoundExpression? Expression, WylType Type)>> bindings;

        /// <summary>Type arguments a lambda's parameters were bound with, which later bindings can't change.</summary>
        readonly Dictionary<TypeParameterSymbol, WylType> fixedTypes = [];

        public Inference(Binder binder, Candidate candidate, IReadOnlyList<Argument?> arguments)
        {
            this.binder = binder;
            (function, owner) = (candidate.Symbol, candidate.Owner);
            ownerMap = owner is null ? TypeMap.Empty : TypeMap.For(owner);
            bindings = function.TypeParameters.ToDictionary(p => p, _ => new List<(BoundExpression?, WylType)>());
            for (int i = 0; i < arguments.Count; i++)
            {
                // A parameter left to its default says nothing about the type arguments.
                if (arguments[i] is not { } argument)
                    continue;
                var parameter = ParameterType(i);
                foreach (var option in Options(argument.Expression))
                {
                    if (option is not BoundPendingLambda lambda)
                        Unify(parameter, option, option.Type);
                    else if (lambda.DeclaredParameters is { } declared && parameter is FunctionType functionParameter && functionParameter.Parameters.Count == declared.Count)
                    {
                        foreach (var (target, type) in functionParameter.Parameters.Zip(declared))
                        {
                            if (type.Type != WylType.Error)
                                Unify(target.Type, null, type.Type);
                        }
                    }
                }
            }
            InferFromBounds();
        }

        /// <summary>
        /// Gives the type parameters that a bound mentions their types from the argument of the
        /// bounded parameter: with <c>&lt;I extends Iterator&lt;T&gt;, T&gt;</c> and <c>I</c> found to
        /// be a type that implements <c>Iterator&lt;int&gt;</c>, <c>T</c> is <c>int</c>. A parameter
        /// whose arguments disagree, or whose argument has the bound's interface with two lists of type
        /// arguments, gives nothing. Repeats while a bound gives a parameter its first type.
        /// </summary>
        void InferFromBounds()
        {
            var done = new HashSet<TypeParameterSymbol>();
            bool progress = true;
            while (progress)
            {
                progress = false;
                foreach (var parameter in function.TypeParameters)
                {
                    if (done.Contains(parameter) || bindings[parameter].Select(b => b.Type).Distinct().ToList() is not [var argument] || argument == WylType.Null)
                        continue;
                    done.Add(parameter);
                    foreach (var bound in parameter.BoundsIn(function).Select(ownerMap.Apply))
                    {
                        if (bound.TypeArguments.Count == 0 || !bound.ContainedTypeParameters.Any(p => bindings.TryGetValue(p, out var b) && b.Count == 0))
                            continue;
                        var matches = ((IEnumerable<WylType>)[argument, .. Conversions.Supertypes(binder.stdlib.MemberHost(argument), binder.currentFunction)])
                            .Where(t => t.Symbol == bound.Symbol).Distinct().ToList();
                        if (matches is [var match])
                        {
                            Unify(bound, null, match);
                            progress = true;
                        }
                    }
                }
            }
        }

        /// <summary>The type parameters nothing has given a type yet.</summary>
        public IReadOnlyList<TypeParameterSymbol> Unresolved => [.. function.TypeParameters.Where(p => bindings[p].Count == 0)];

        WylType ParameterType(int index) => ownerMap.Apply(function.Parameters[index].Type);

        void Unify(WylType parameter, BoundExpression? argument, WylType argumentType)
        {
            // Type arguments are escaping, so a non-escaping argument is an error once it is converted.
            if (parameter is TypeParameterType { Parameter: var p } && bindings.TryGetValue(p, out var found))
            {
                found.Add((argument, EscapingForm(argumentType)));
            }
            // A place or a reference gives `ref? T` the type referred to, exactly.
            else if (parameter is NullableReferenceType reference)
            {
                if (argumentType != WylType.Null)
                    Unify(reference.Referenced, null, argumentType.Referred);
            }
            // A nullable argument gives `T?` its value type; null says nothing about `T`.
            else if (parameter.IsNullableValue)
            {
                if (argumentType != WylType.Null)
                    Unify(parameter.NonNull, argumentType.IsNullable ? null : argument, argumentType.NonNull);
            }
            else if (binder.stdlib.IsSpan(parameter) && parameter.Symbol != argumentType.Symbol
                     && binder.ViewedElement(argumentType, parameter.Symbol == binder.stdlib.ReadOnlySpan) is { } viewed)
            {
                Unify(parameter.TypeArguments[0], null, viewed);
            }
            else if (parameter is PointerType pointer && argumentType is PointerType argumentPointer)
            {
                Unify(pointer.Element, null, argumentPointer.Element);
            }
            else if (parameter is FixedArrayType array && argumentType is FixedArrayType argumentArray && array.Length == argumentArray.Length)
            {
                Unify(array.Element, null, argumentArray.Element);
            }
            else if (parameter.Symbol is not null && parameter.Symbol == argumentType.Symbol)
            {
                // Inside a type argument there are no conversions, so these must match exactly.
                for (int i = 0; i < parameter.TypeArguments.Count; i++)
                    Unify(parameter.TypeArguments[i], null, argumentType.TypeArguments[i]);
            }
            // Function types convert only to themselves, so their parts match exactly too.
            else if (parameter is FunctionType parameterFunction && argumentType is FunctionType argumentFunction
                     && parameterFunction.Parameters.Count == argumentFunction.Parameters.Count)
            {
                foreach (var (to, from) in parameterFunction.Parameters.Zip(argumentFunction.Parameters))
                    Unify(to.Type, null, from.Type);
                Unify(parameterFunction.ReturnType, null, argumentFunction.ReturnType);
            }
        }

        /// <summary>
        /// The candidate with its type arguments. While some are left to lambda bodies, those stand for
        /// themselves, and <see cref="Unresolved"/> lists them. Null, with an error, if inference fails.
        /// </summary>
        public MethodReference? Instantiate(IReadOnlyList<Argument?> arguments, out Func<Location, Diagnostic>? error)
        {
            var result = new List<WylType>();
            foreach (var parameter in function.TypeParameters)
            {
                var found = bindings[parameter];
                if (found.Count == 0)
                {
                    if (!LambdaCouldGive(parameter, arguments))
                    {
                        error = at => Catalog.CannotInferTypeArgument(at, parameter.Name, function.Name);
                        return null;
                    }
                    result.Add(parameter.Type);
                    continue;
                }
                // A non-escaping function can't convert to its escaping form, and is reported as that once the call is checked.
                // An unsuffixed literal's type comes last, so the literal takes another argument's type when it fits.
                // A call that returns nothing is a statement, not a value, so it can never be a type argument.
                if (found.Any(b => b.Type == WylType.Void))
                {
                    error = at => Catalog.InferFromVoid(at, parameter.Name, function.Name);
                    return null;
                }
                var chosen = found.OrderBy(b => b.Expression is BoundLiteral { Integer: not null } literal && literal.Type == WylType.Int)
                    .Select(b => b.Type).Distinct().FirstOrDefault(candidate =>
                    candidate != WylType.Null && found.All(b => b.Expression is not null ? binder.CanConvert(b.Expression, candidate) || IsEscapingMismatch(b.Expression.Type, candidate) : b.Type == candidate));
                if (found.All(b => b.Type == WylType.Null))
                {
                    error = at => Catalog.InferFromNull(at, parameter.Name, function.Name);
                    return null;
                }
                if (chosen is null || (fixedTypes.TryGetValue(parameter, out var fixedType) && chosen != fixedType))
                {
                    var types = found.Select(b => b.Type.ToString()).Distinct().ToList();
                    error = at => Catalog.ConflictingInference(at, parameter.Name, function.Name, types);
                    return null;
                }
                result.Add(chosen);
            }
            error = null;
            return new MethodReference(function, owner, result);
        }

        /// <summary>Whether a lambda argument's return type is all that could give <paramref name="parameter"/> a type.</summary>
        bool LambdaCouldGive(TypeParameterSymbol parameter, IReadOnlyList<Argument?> arguments) =>
            Enumerable.Range(0, arguments.Count).Any(i =>
                ParameterType(i) is FunctionType type
                && type.ReturnType.ContainedTypeParameters.Contains(parameter)
                && arguments[i] is { } argument && Options(argument.Expression).Any(o => o is BoundPendingLambda));

        /// <summary>
        /// Whether the arguments could be passed once the rest is inferred: those whose parameter types
        /// are known must convert, and lambdas must take as many parameters as their targets.
        /// </summary>
        public bool CouldApply(MethodReference partial, IReadOnlyList<Argument?> arguments)
        {
            var unresolved = Unresolved;
            return Enumerable.Range(0, arguments.Count).All(i =>
            {
                if (arguments[i] is not { } argument)
                    return true;
                var type = partial.ParameterType(i);
                if (!type.ContainedTypeParameters.Any(unresolved.Contains))
                    return binder.IsApplicable(partial, arguments, i);
                // `null` for a `ref?` fits whatever its type parameters turn out to be.
                if (IsNullForReference(argument, type))
                    return true;
                return partial.ParameterRefKind(i) != RefKind.Ref && !argument.IsRef && Options(argument.Expression).All(o =>
                    o is not BoundPendingLambda lambda || (type is FunctionType target && target.Parameters.Count == lambda.Syntax.Parameters.Count));
            });
        }

        /// <summary>
        /// Binds, in turn, each lambda whose parameter types are known and whose return type has a type
        /// parameter only lambdas can give, and infers from the values it returns as if they were
        /// arguments. Reports its own errors.
        /// </summary>
        public MethodReference? FinishFromLambdas(IReadOnlyList<Argument?> arguments, Location location)
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
                    binder.Error(error!(location));
                    return null;
                }
                var unresolved = Unresolved;
                for (int i = 0; i < arguments.Count && !progress; i++)
                {
                    if (ParameterType(i) is not FunctionType target || !target.ReturnType.ContainedTypeParameters.Any(inferable.Contains))
                        continue;
                    var parameters = ((FunctionType)current.ParameterType(i)).Parameters;
                    if (parameters.Any(p => p.Type.ContainedTypeParameters.Any(unresolved.Contains)))
                        continue;
                    var lambdas = arguments[i] is not { } argument ? [] : Options(argument.Expression).OfType<BoundPendingLambda>().Where(l => l.Inferred is null).ToList();
                    if (lambdas.Count > 0)
                    {
                        foreach (var p in target.Parameters.SelectMany(p => p.Type.ContainedTypeParameters).Where(bindings.ContainsKey))
                            fixedTypes.TryAdd(p, current.Map.Apply(p.Type));
                    }
                    foreach (var lambda in lambdas)
                    {
                        var inferred = lambda.Inferred = binder.BindLambdaInferringReturn(lambda, parameters, target.IsEscaping, target.IsShared);
                        if (inferred.Values.Any(v => v.Type == WylType.Error))
                            return null;
                        if (inferred.Values.Count == 0 && target.ReturnType is TypeParameterType && binder.stdlib.Unit is { } unit)
                        {
                            Unify(target.ReturnType, null, unit.Type);
                            progress = true;
                            continue;
                        }
                        if (inferred.Values.Count == 0)
                        {
                            binder.Error(Catalog.InferFromVoidLambda(lambda.Location, target.ReturnType.ContainedTypeParameters.First(inferable.Contains).Name, function.Name));
                            return null;
                        }
                        foreach (var value in inferred.Values)
                            Unify(target.ReturnType, value, value.Type);
                        progress = true;
                    }
                }
            }

            var finished = Instantiate(arguments, out var finalError);
            if (finished is not null && Unresolved is [var first, ..])
            {
                finished = null;
                finalError = at => Catalog.CannotInferTypeArgument(at, first.Name, function.Name);
            }
            if (finished is null)
                binder.Error(finalError!(location));
            return finished;
        }
    }

    /// <summary>
    /// The functions an argument could be: itself, or each option of a <c>?:</c> or switch expression
    /// still waiting for its function type.
    /// </summary>
    static IEnumerable<BoundExpression> Options(BoundExpression argument) => argument is BoundPendingChoice choice ? choice.Options : [argument];
}
