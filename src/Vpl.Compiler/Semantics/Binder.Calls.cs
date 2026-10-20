using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

public sealed partial class Binder
{
    List<Argument> BindArguments(IEnumerable<Expr> arguments) =>
    [
        .. arguments.Select(a => a is RefArgExpr r
            ? new Argument(BindExpression(r.Operand), true, r.Location)
            : new Argument(BindExpression(a), false, a.Location)),
    ];

    BoundExpression BindCall(CallExpr e, VplType? expected)
    {
        if (e.Callee is SuperExpr)
        {
            BindArguments(e.Arguments);
            return ErrorExpression(e.Callee.Location, currentFunction is { IsConstructor: true, ContainingType.Kind: TypeKind.Class }
                ? "'super(...)' must be the first statement of a constructor"
                : "'super(...)' can only be used in a class constructor");
        }

        var arguments = BindArguments(e.Arguments);
        return e.Callee switch
        {
            NameExpr name => BindNameCall(e, name, arguments, expected),
            MemberAccessExpr member => BindMemberCall(e, member, arguments, expected),
            ConditionalAccessExpr access => BindConditionalCall(e, access, arguments),
            ThisExpr => ErrorExpression(e.Callee.Location, "'this(...)' constructor chaining is not supported yet"),
            _ => ErrorExpression(e.Callee.Location, "this expression cannot be called"),
        };
    }

    BoundExpression BindNameCall(CallExpr e, NameExpr name, List<Argument> arguments, VplType? expected)
    {
        if (IsValueName(name.Name))
            return ErrorExpression(name.Location, $"'{name.Name}' is a variable, not a function");
        if (LookupTypeParameter(name.Name) is not null)
            return ErrorExpression(name.Location, $"'{name.Name}' is a type parameter and cannot be constructed");

        var typeArguments = BindTypeArguments(name.TypeArguments);
        if (arguments.Any(a => a.Expression.Type == VplType.Error) || typeArguments?.Contains(VplType.Error) == true)
            return new BoundErrorExpression(e.Location);

        if (currentType is { } s && LookupMethods(s.Type, name.Name) is { Count: > 0 } methods)
        {
            var method = Resolve(e.Location, $"overload of '{name.Name}'", methods, typeArguments, arguments);
            if (method is null)
                return new BoundErrorExpression(e.Location);
            CheckAccessible(name.Location, method.Symbol.ContainingType!, method.Symbol.IsPrivate, $"method '{method.Symbol.ContainingType!.Name}.{name.Name}'");
            if (method.Symbol.IsStatic)
                return MakeCall(e.Location, method, null, arguments);
            if (!hasThis)
                return ErrorExpression(name.Location, $"'{name.Name}' is an instance method; {NoInstanceReason}");
            return MakeCall(e.Location, method, new BoundThis(name.Location, s.Type), arguments);
        }

        if (types.TryGetValue(name.Name, out var type))
        {
            if (type.Kind != TypeKind.Struct)
            {
                return ErrorExpression(name.Location, type.Kind switch
                {
                    TypeKind.Class => $"classes are created with 'new', as in 'new {name.Name}(...)'",
                    TypeKind.Enum => $"enums are created from their variants, as in '{name.Name}.{type.Variants.FirstOrDefault()?.Name}'",
                    _ => $"cannot create an instance of interface '{name.Name}'",
                });
            }
            var constructed = Construct(type, typeArguments ?? [], name.Location);
            return constructed == VplType.Error
                ? new BoundErrorExpression(e.Location)
                : BindConstruction(e.Location, constructed, arguments, []);
        }

        if (!functions.TryGetValue(name.Name, out var overloads))
        {
            if (typeArguments is null && ExpectedVariant(name.Name, expected) is { } variant)
                return BindVariant(e.Location, variant.Owner, expected, variant, arguments);
            return ErrorExpression(name.Location, $"function '{name.Name}' is not defined{VariantHint(name.Name)}");
        }
        var function = Resolve(e.Location, $"overload of '{name.Name}'", [.. overloads.Select(f => new Candidate(f, null))], typeArguments, arguments);
        return function is null ? new BoundErrorExpression(e.Location) : MakeCall(e.Location, function, null, arguments);
    }

    BoundExpression BindMemberCall(CallExpr e, MemberAccessExpr member, List<Argument> arguments, VplType? expected)
    {
        var typeArguments = BindTypeArguments(member.TypeArguments);
        if (typeArguments?.Contains(VplType.Error) == true)
            return new BoundErrorExpression(e.Location);

        if (member.Target is SuperExpr super)
            return BindSuperMethodCall(e, super, member, typeArguments, arguments);

        if (AsEnumName(member.Target, expected) is var (enumSymbol, enumType) && enumSymbol.FindVariant(member.Name) is { } variant)
        {
            if (typeArguments is not null)
                return ErrorExpression(member.NameLocation, $"type arguments belong to the enum, as in '{enumSymbol.Name}<...>.{member.Name}(...)'");
            return BindVariant(e.Location, enumSymbol, enumType, variant, arguments);
        }

        if (AsTypeName(member.Target) is { } type)
        {
            if (type == VplType.Error || arguments.Any(a => a.Expression.Type == VplType.Error))
                return new BoundErrorExpression(e.Location);
            var s = type.Symbol!;
            var staticCandidates = LookupMethods(type, member.Name);
            if (staticCandidates.Count == 0)
                return ErrorExpression(member.NameLocation, $"'{s.Name}' has no method '{member.Name}'");
            var staticMethod = Resolve(e.Location, $"overload of '{s.Name}.{member.Name}'", staticCandidates, typeArguments, arguments);
            if (staticMethod is null)
                return new BoundErrorExpression(e.Location);
            if (!staticMethod.Symbol.IsStatic)
            {
                return ErrorExpression(member.NameLocation,
                    $"'{s.Name}.{member.Name}' is an instance method; call it on a value of type '{type}'");
            }
            var declaring = staticMethod.Symbol.ContainingType!;
            CheckAccessible(member.NameLocation, declaring, staticMethod.Symbol.IsPrivate, $"method '{declaring.Name}.{member.Name}'");
            return MakeCall(e.Location, staticMethod, null, arguments);
        }

        var receiver = BindExpression(member.Target);
        if (receiver.Type == VplType.Error || arguments.Any(a => a.Expression.Type == VplType.Error))
            return new BoundErrorExpression(e.Location);
        receiver = RequireNonNull(receiver);
        if (receiver.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);
        return BindMethodCall(e.Location, receiver, member.Name, member.NameLocation, typeArguments, arguments);
    }

    BoundExpression BindMethodCall(
        Location location,
        BoundExpression receiver,
        string name,
        Location nameLocation,
        IReadOnlyList<VplType>? typeArguments,
        List<Argument> arguments)
    {
        var receiverType = receiver.Type;
        var candidates = receiverType.IsStructOrEnum || receiverType.IsReference || receiverType.IsTypeParameter
            ? LookupMethods(receiverType, name)
            : [];
        if (candidates.Count == 0)
        {
            return ErrorExpression(nameLocation, LookupField(receiverType, name) is not null
                ? $"'{DisplayName(receiverType)}.{name}' is a field, not a method"
                : $"'{DisplayName(receiverType)}' has no method '{name}'");
        }

        var method = Resolve(location, $"overload of '{DisplayName(receiverType)}.{name}'", candidates, typeArguments, arguments);
        if (method is null)
            return new BoundErrorExpression(location);
        var declaring = method.Symbol.ContainingType!;
        if (method.Symbol.IsStatic)
        {
            return ErrorExpression(nameLocation,
                $"static method '{name}' must be called through the type: '{declaring.Name}.{name}(...)'");
        }
        CheckAccessible(nameLocation, declaring, method.Symbol.IsPrivate, $"method '{declaring.Name}.{name}'");
        return MakeCall(location, method, receiver, arguments);
    }

    BoundExpression BindSuperMethodCall(CallExpr e, SuperExpr super, MemberAccessExpr member, IReadOnlyList<VplType>? typeArguments, List<Argument> arguments)
    {
        if (!hasThis || currentType?.Type.BaseType is not { } baseType)
        {
            return ErrorExpression(super.Location, hasThis
                ? $"'{currentType!.Name}' has no base class"
                : "'super' can only be used in instance methods and constructors of a class");
        }
        if (arguments.Any(a => a.Expression.Type == VplType.Error))
            return new BoundErrorExpression(e.Location);
        var candidates = LookupMethods(baseType, member.Name);
        if (candidates.Count == 0)
            return ErrorExpression(member.NameLocation, $"'{baseType.Symbol!.Name}' has no method '{member.Name}'");
        var method = Resolve(e.Location, $"overload of '{baseType.Symbol!.Name}.{member.Name}'", candidates, typeArguments, arguments);
        if (method is null)
            return new BoundErrorExpression(e.Location);
        var declaring = method.Symbol.ContainingType!;
        if (method.Symbol.IsStatic)
            return ErrorExpression(member.NameLocation, $"static method '{member.Name}' must be called through the type: '{declaring.Name}.{member.Name}(...)'");
        if (method.Symbol.IsAbstract)
            return ErrorExpression(member.NameLocation, $"cannot call abstract method '{declaring.Name}.{member.Name}' through 'super'");
        CheckAccessible(member.NameLocation, declaring, method.Symbol.IsPrivate, $"method '{declaring.Name}.{member.Name}'");
        return MakeCall(e.Location, method, new BoundThis(super.Location, currentType.Type), arguments, isSuperCall: true);
    }

    /// <summary>Binds the receiver of <c>a?.member</c> and a temporary local holding its non-null value.</summary>
    (BoundExpression Receiver, LocalSymbol Value)? BindConditionalReceiver(ConditionalAccessExpr e)
    {
        var receiver = BindExpression(e.Target);
        if (receiver.Type == VplType.Error)
            return null;
        if (!receiver.Type.IsNullable)
        {
            Error(e.Target.Location, $"'?.' is only needed on nullable values, and '{receiver.Type}' is not nullable; use '.'");
            return null;
        }
        return (receiver, new LocalSymbol("value", receiver.Type.NonNull, isParameter: false) { IsTemporary = true });
    }

    BoundExpression BindConditionalFieldAccess(ConditionalAccessExpr e)
    {
        if (BindConditionalReceiver(e) is not var (receiver, value))
            return new BoundErrorExpression(e.Location);
        var access = BindFieldAccess(e.Location, new BoundLocalAccess(e.Target.Location, value), e.Name, e.NameLocation);
        return MakeConditionalAccess(e.Location, receiver, value, access);
    }

    BoundExpression BindConditionalCall(CallExpr e, ConditionalAccessExpr access, List<Argument> arguments)
    {
        if (BindConditionalReceiver(access) is not var (receiver, value) || arguments.Any(a => a.Expression.Type == VplType.Error))
            return new BoundErrorExpression(e.Location);
        var call = BindMethodCall(e.Location, new BoundLocalAccess(access.Target.Location, value), access.Name, access.NameLocation, null, arguments);
        return MakeConditionalAccess(e.Location, receiver, value, call);
    }

    BoundExpression MakeConditionalAccess(Location location, BoundExpression receiver, LocalSymbol value, BoundExpression access)
    {
        if (access.Type == VplType.Error)
            return access;
        if (access.Type == VplType.Void || access.Type.IsReference)
            return new BoundConditionalAccess(location, receiver, value, access, access.Type == VplType.Void ? access.Type : access.Type.Nullable);
        return ErrorExpression(location, $"'?.' cannot produce a nullable '{access.Type}': only class and interface types can be nullable for now");
    }

    BoundExpression MakeCall(Location location, MethodReference method, BoundExpression? receiver, List<Argument> arguments, bool isSuperCall = false)
    {
        // Calling a method on an object never modifies the place holding the reference.
        if (receiver is not null && !receiver.Type.IsReference && !method.Symbol.IsReadOnly)
        {
            var (kind, reason) = ClassifyPlace(receiver);
            if (kind == PlaceKind.ReadOnly)
            {
                var what = receiver is BoundThis ? "'this'" : $"'{TextOf(receiver.Location)}'";
                return ErrorExpression(location,
                    $"cannot call '{method.Symbol.Name}' on {what} because {reason}; only 'readonly' methods can be called on it");
            }
        }
        QueueBoundChecks(method.Symbol.TypeParameters, method.TypeArguments, location);
        return new BoundCall(location, method, receiver, ConvertArguments(method, arguments), isSuperCall);
    }

    List<BoundExpression> ConvertArguments(MethodReference method, List<Argument> arguments)
    {
        var result = new List<BoundExpression>();
        for (int i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (method.ParameterRefKind(i) != RefKind.Ref)
            {
                result.Add(Convert(argument.Expression, method.ParameterType(i)));
                continue;
            }

            var (kind, reason) = ClassifyPlace(argument.Expression);
            if (kind == PlaceKind.Temporary)
                Error(argument.Location, "a 'ref' argument must be a variable or field");
            else if (kind == PlaceKind.ReadOnly)
                Error(argument.Location, $"cannot pass '{TextOf(argument.Expression.Location)}' by 'ref' because {reason}");
            else if (argument.Expression is BoundFieldAccess { Field.IsWeak: true })
                Error(argument.Location, "a 'weak' field cannot be passed by 'ref'");
            result.Add(argument.Expression);
        }
        return result;
    }

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
            else if (parameter.Symbol is not null && parameter.Symbol == argumentType.Symbol)
            {
                // Inside a type argument there are no conversions, so these must match exactly.
                for (int i = 0; i < parameter.TypeArguments.Count; i++)
                    Unify(parameter.TypeArguments[i], null, argumentType.TypeArguments[i]);
            }
        }

        for (int i = 0; i < arguments.Count; i++)
            Unify(ownerMap.Apply(function.Parameters[i].Type), arguments[i].Expression, arguments[i].Expression.Type);

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

    static bool IsApplicable(MethodReference method, List<Argument> arguments) =>
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

    BoundExpression BindNew(NewExpr e)
    {
        var arguments = BindArguments(e.Arguments);
        var type = BindType(e.Type, allowVoid: false);
        if (type == VplType.Error || arguments.Any(a => a.Expression.Type == VplType.Error))
            return new BoundErrorExpression(e.Location);
        if (type.IsStruct)
            return ErrorExpression(e.Type.Location, $"structs are created without 'new', as in '{e.Type.Name}(...)'");
        if (type.IsEnum)
            return ErrorExpression(e.Type.Location, $"enums are created from their variants without 'new', as in '{e.Type.Name}.{type.Symbol!.Variants.FirstOrDefault()?.Name}'");
        if (type.IsInterface)
            return ErrorExpression(e.Type.Location, $"cannot create an instance of interface '{type}'");
        if (!type.IsClass)
            return ErrorExpression(e.Type.Location, $"'{type}' cannot be created with 'new'");
        if (type.Symbol!.IsAbstract)
            return ErrorExpression(e.Type.Location, $"cannot create an instance of abstract class '{type}'");

        var constructor = Resolve(e.Location, $"constructor of '{type}'", ConstructorsOf(type), null, arguments);
        if (constructor is null)
            return new BoundErrorExpression(e.Location);
        CheckAccessible(e.Location, type.Symbol, constructor.Symbol.IsPrivate, $"constructor '{constructor}'");
        return new BoundNew(e.Location, type, constructor, ConvertArguments(constructor, arguments));
    }

    BoundExpression BindInitializer(InitializerExpr e)
    {
        var (typeName, arguments) = e.Target switch
        {
            NameExpr n => (n, new List<Argument>()),
            CallExpr { Callee: NameExpr n } call => (n, BindArguments(call.Arguments)),
            _ => throw new InvalidOperationException("the parser only produces initializers on names and calls"),
        };
        if (AsTypeName(typeName) is not { } type || (type != VplType.Error && !type.IsStruct))
            return ErrorExpression(typeName.Location, $"'{typeName.Name}' is not a struct; '{{ ... }}' initializers can only follow a struct name");
        if (type == VplType.Error)
            return new BoundErrorExpression(e.Location);
        return BindConstruction(e.Location, type, arguments, e.Fields);
    }

    BoundExpression BindConstruction(Location location, VplType type, List<Argument> arguments, IReadOnlyList<FieldInit> fieldInits)
    {
        if (arguments.Any(a => a.Expression.Type == VplType.Error))
            return new BoundErrorExpression(location);

        var s = type.Symbol!;
        var constructor = Resolve(location, $"constructor of '{type}'", ConstructorsOf(type), null, arguments);
        if (constructor is null)
            return new BoundErrorExpression(location);
        CheckAccessible(location, s, constructor.Symbol.IsPrivate, $"constructor '{constructor}'");

        var map = TypeMap.For(type);
        var initializers = new List<BoundFieldInitializer>();
        foreach (var init in fieldInits)
        {
            var initField = s.FindField(init.Name);
            var value = BindExpression(init.Value, initField is null ? null : map.Apply(initField.Type));
            if (initField is not { } field)
            {
                Error(init.Location, $"'{s.Name}' has no field '{init.Name}'");
                continue;
            }
            if (initializers.Any(i => i.Field == field))
            {
                Error(init.Location, $"field '{init.Name}' is initialized more than once");
                continue;
            }
            CheckAccessible(init.Location, s, field.IsPrivate, $"field '{s.Name}.{init.Name}'");
            initializers.Add(new BoundFieldInitializer(field, Convert(value, map.Apply(field.Type))));
        }

        if (constructor.Symbol.IsImplicit)
        {
            var missing = s.Fields.FirstOrDefault(f =>
                f.Declaration.Initializer is null
                && initializers.All(i => i.Field != f)
                && !Conversions.IsDefaultable(map.Apply(f.Type)));
            if (missing is not null)
            {
                return ErrorExpression(location,
                    $"'{type}' has no default value because field '{missing.Name}' has type '{map.Apply(missing.Type)}'; initialize it explicitly, as in '{s.Name}{{ {missing.Name} = ... }}'");
            }
        }

        return new BoundConstruction(location, type, constructor, ConvertArguments(constructor, arguments), initializers);
    }
}
