using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

public sealed partial class Binder
{
    /// <summary>
    /// Binds a call's arguments. An argument expects its parameter's type when every candidate taking
    /// this many arguments agrees on it, so <c>f(Result.Ok(1))</c> and <c>f(East)</c> know their enum.
    /// A lambda without an agreed function type, or a choice between lambdas, is bound once overload
    /// resolution chooses one.
    /// </summary>
    List<Argument> BindArguments(IReadOnlyList<Expr> arguments, IReadOnlyList<Candidate>? candidates = null)
    {
        var names = ArgumentNames(arguments);
        var arrangements = candidates?
            .Select(c => (Candidate: c, Arrangement: Arrange(c, names, out _)))
            .Where(c => c.Arrangement is not null)
            .ToList();
        return
        [
            .. arguments.Select((syntax, i) =>
            {
                var (a, name, nameLocation) = syntax is NamedArgExpr n ? (n.Value, n.Name, n.NameLocation) : (syntax, null, default(Location));
                if (a is RefArgExpr r)
                    return new Argument(BindExpression(r.Operand), true, syntax.Location, name, nameLocation);
                var expected = arrangements is null ? null : ExpectedArgumentType(arrangements!, i);
                var value = a is LambdaExpr or ConditionalExpr or SwitchExpr || ReferencedFunctions(a) is not null ? BindOption(a, expected) : BindExpression(a, expected);
                // A positional argument after a named one has no parameter, which is already reported.
                if (name is null && names.Take(i).Any(n => n is not null))
                    value = new BoundErrorExpression(syntax.Location);
                return new Argument(value, false, syntax.Location, name, nameLocation);
            }),
        ];
    }

    /// <summary>
    /// The type every candidate gives the argument at <paramref name="index"/>, or null if they differ.
    /// A parameter typed by the method's own type parameters gives none, since those are inferred from the arguments.
    /// </summary>
    static VplType? ExpectedArgumentType(IReadOnlyList<(Candidate Candidate, int[]? Arrangement)> candidates, int index)
    {
        VplType? agreed = null;
        foreach (var (candidate, arrangement) in candidates)
        {
            var parameter = candidate.Symbol.Parameters[Array.IndexOf(arrangement!, index)];
            var type = (candidate.Owner is null ? TypeMap.Empty : TypeMap.For(candidate.Owner)).Apply(parameter.Type);
            if (parameter.RefKind == RefKind.Ref || type == VplType.Error || (agreed is not null && agreed != type)
                || TypeParametersIn(type).Any(p => candidate.Symbol.TypeParameters.Contains(p)))
                return null;
            agreed = type;
        }
        return agreed;
    }

    /// <summary>
    /// Arguments are bound once the callee's candidates are known. A callee that fails first leaves
    /// them unbound, and they are then bound without expected types to report their own errors.
    /// </summary>
    sealed class PendingArguments(Binder binder, IReadOnlyList<Expr> syntax)
    {
        List<Argument>? bound;

        public List<Argument> Bind(IReadOnlyList<Candidate>? candidates) => bound ??= binder.BindArguments(syntax, candidates);
    }

    static bool AnyError(List<Argument> arguments) => arguments.Any(a => a.Expression.Type == VplType.Error);

    BoundExpression BindCall(CallExpr e, VplType? expected)
    {
        if (e.Callee is SuperExpr)
        {
            BindArguments(e.Arguments);
            return ErrorExpression(e.Callee.Location, currentFunction is { IsConstructor: true, ContainingType.Kind: TypeKind.Class }
                ? "'super(...)' must be the first statement of a constructor"
                : "'super(...)' can only be used in a class constructor");
        }

        var arguments = new PendingArguments(this, e.Arguments);
        var call = e.Callee switch
        {
            NameExpr name => BindNameCall(e, name, arguments, expected),
            MemberAccessExpr member => BindMemberCall(e, member, arguments, expected),
            ConditionalAccessExpr access => BindConditionalCall(e, access, arguments),
            ThisExpr => ErrorExpression(e.Callee.Location, "'this(...)' constructor chaining is not supported yet"),
            _ => BindCalleeValue(e, BindExpression(e.Callee), arguments),
        };
        arguments.Bind(null);
        return call;
    }

    /// <summary>A call of something other than a named function or method, which must be a function value.</summary>
    BoundExpression BindCalleeValue(CallExpr e, BoundExpression callee, PendingArguments pending) =>
        callee.Type.NonNull.IsFunction || callee.Type == VplType.Error
            ? BindInvocation(e, callee, pending)
            : ErrorExpression(e.Callee.Location, callee is BoundLocalAccess or BoundFieldAccess
                ? $"'{TextOf(e.Callee.Location)}' is a variable of type '{callee.Type}', not a function"
                : $"a value of type '{callee.Type}' cannot be called");

    BoundExpression BindNameCall(CallExpr e, NameExpr name, PendingArguments pending, VplType? expected)
    {
        if (IsValueName(name.Name))
            return BindCalleeValue(e, BindName(name, null), pending);
        if (LookupTypeParameter(name.Name) is not null)
            return ErrorExpression(name.Location, $"'{name.Name}' is a type parameter and cannot be constructed");

        var typeArguments = BindTypeArguments(name.TypeArguments);
        if (typeArguments?.Contains(VplType.Error) == true)
            return new BoundErrorExpression(e.Location);

        if (currentType is { } s && LookupMethods(s.Type, name.Name) is { Count: > 0 } methods)
        {
            var arguments = pending.Bind(methods);
            if (AnyError(arguments))
                return new BoundErrorExpression(e.Location);
            var method = Resolve(e.Location, $"overload of '{name.Name}'", methods, typeArguments, arguments);
            if (method is null)
                return new BoundErrorExpression(e.Location);
            if (method.Symbol.IsProperty)
                return ErrorExpression(name.Location, $"'{name.Name}' is a property; use it without '(...)'");
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
                : BindConstruction(e.Location, constructed, pending.Bind(ConstructorsOf(constructed)));
        }

        if (!functions.TryGetValue(name.Name, out var overloads))
        {
            if (typeArguments is null && ExpectedVariant(name.Name, expected) is { } variant)
                return BindVariant(e.Location, variant.Owner, expected, variant, pending);
            return ErrorExpression(name.Location, $"function '{name.Name}' is not defined{VariantHint(name.Name)}");
        }
        List<Candidate> candidates = [.. overloads.Select(f => new Candidate(f, null))];
        var functionArguments = pending.Bind(candidates);
        if (AnyError(functionArguments))
            return new BoundErrorExpression(e.Location);
        var function = Resolve(e.Location, $"overload of '{name.Name}'", candidates, typeArguments, functionArguments);
        return function is null ? new BoundErrorExpression(e.Location) : MakeCall(e.Location, function, null, functionArguments);
    }

    BoundExpression BindMemberCall(CallExpr e, MemberAccessExpr member, PendingArguments pending, VplType? expected)
    {
        var typeArguments = BindTypeArguments(member.TypeArguments);
        if (typeArguments?.Contains(VplType.Error) == true)
            return new BoundErrorExpression(e.Location);

        if (member.Target is SuperExpr super)
            return BindSuperMethodCall(e, super, member, typeArguments, pending);

        if (AsEnumName(member.Target, expected) is var (enumSymbol, enumType) && enumSymbol.FindVariant(member.Name) is { } variant)
        {
            if (typeArguments is not null)
                return ErrorExpression(member.NameLocation, $"type arguments belong to the enum, as in '{enumSymbol.Name}<...>.{member.Name}(...)'");
            return BindVariant(e.Location, enumSymbol, enumType, variant, pending);
        }

        if (AsTypeName(member.Target) is { } type)
        {
            if (type == VplType.Error)
                return new BoundErrorExpression(e.Location);
            var s = type.Symbol!;
            var staticCandidates = LookupMethods(type, member.Name);
            if (staticCandidates.Count == 0)
                return ErrorExpression(member.NameLocation, $"'{s.Name}' has no method '{member.Name}'");
            var arguments = pending.Bind(staticCandidates);
            if (AnyError(arguments))
                return new BoundErrorExpression(e.Location);
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
        if (receiver.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);
        // A field of function type is called like a method: `button.onClick(5)`.
        if (MethodCandidates(receiver.Type.NonNull, member.Name).Count == 0
            && LookupField(receiver.Type, member.Name) is var (field, owner) && TypeMap.For(owner).Apply(field.Type).NonNull.IsFunction)
        {
            return BindInvocation(e, BindFieldAccess(member.Location, receiver, member.Name, member.NameLocation), pending);
        }
        var receiverArguments = pending.Bind(MethodCandidates(receiver.Type.NonNull, member.Name));
        if (AnyError(receiverArguments))
            return new BoundErrorExpression(e.Location);
        receiver = RequireNonNull(receiver);
        if (receiver.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);
        return BindMethodCall(e.Location, receiver, member.Name, member.NameLocation, typeArguments, receiverArguments);
    }

    IReadOnlyList<Candidate> MethodCandidates(VplType receiverType, string name) =>
        HasMembers(receiverType) ? LookupMethods(receiverType, name) : [];

    BoundExpression BindMethodCall(
        Location location,
        BoundExpression receiver,
        string name,
        Location nameLocation,
        IReadOnlyList<VplType>? typeArguments,
        List<Argument> arguments,
        bool asProperty = false)
    {
        var receiverType = receiver.Type;
        if (name == "toString" && Builtins.FormattedTypes.Contains(receiverType))
            return BindFormattedToString(location, receiver, typeArguments, arguments);
        if (name == "compareTo" && (receiverType.IsInteger || receiverType == VplType.Char))
            return BindBuiltinCompareTo(location, receiver, typeArguments, arguments);
        var candidates = MethodCandidates(receiverType, name);
        if (candidates.Count == 0)
        {
            return ErrorExpression(nameLocation, LookupField(receiverType, name) is not null
                ? $"'{DisplayName(receiverType)}.{name}' is a field, not a method"
                : $"'{DisplayName(receiverType)}' has no method '{name}'");
        }

        var description = name == SyntaxFacts.IndexerName ? $"indexer of '{receiverType}'" : $"overload of '{DisplayName(receiverType)}.{name}'";
        var method = Resolve(location, description, candidates, typeArguments, arguments);
        if (method is null)
            return new BoundErrorExpression(location);
        var declaring = method.Symbol.ContainingType!;
        if (method.Symbol.IsStatic)
        {
            return ErrorExpression(nameLocation,
                $"static method '{name}' must be called through the type: '{declaring.Name}.{name}(...)'");
        }
        if (method.Symbol.IsProperty && !asProperty)
            return ErrorExpression(nameLocation, $"'{DisplayName(receiverType)}.{name}' is a property; use it without '(...)'");
        CheckAccessible(nameLocation, declaring, method.Symbol.IsPrivate, $"method '{declaring.Name}.{name}'");
        return MakeCall(location, method, receiver, arguments);
    }

    /// <summary>Primitives and <c>String</c> are printable without implementing <c>Printable</c>, so their <c>toString()</c> is the runtime's.</summary>
    BoundExpression BindFormattedToString(Location location, BoundExpression receiver, IReadOnlyList<VplType>? typeArguments, List<Argument> arguments)
    {
        if (typeArguments is not null)
            return ErrorExpression(location, $"'{receiver.Type}.toString' takes no type arguments");
        if (arguments.Count != 0)
            return ErrorExpression(location, $"no overload of '{receiver.Type}.toString' takes {arguments.Count} argument{(arguments.Count == 1 ? "" : "s")}");
        return new BoundCall(location, new MethodReference(Builtins.ToString(receiver.Type), null, []), null, [receiver]);
    }

    /// <summary>
    /// The integers and <c>char</c> are comparable without implementing <c>Comparable</c>, so their
    /// <c>compareTo(other)</c> is the runtime's. <c>String</c>'s is declared in the standard library.
    /// </summary>
    BoundExpression BindBuiltinCompareTo(Location location, BoundExpression receiver, IReadOnlyList<VplType>? typeArguments, List<Argument> arguments)
    {
        var type = receiver.Type;
        if (typeArguments is not null)
            return ErrorExpression(location, $"'{type}.compareTo' takes no type arguments");
        if (arguments.Count != 1)
            return ErrorExpression(location, $"no overload of '{type}.compareTo' takes {arguments.Count} argument{(arguments.Count == 1 ? "" : "s")}");
        var other = arguments[0];
        if (other.Name is { } argumentName && argumentName != "other")
            return ErrorExpression(other.NameLocation, $"'compareTo' has no parameter named '{argumentName}'");
        if (other.IsRef)
            return ErrorExpression(other.Location, "argument 1 must not be passed with 'ref'");
        return new BoundCall(location, new MethodReference(Builtins.CompareTo(type), null, []), null, [receiver, Convert(other.Expression, type)]);
    }

    BoundExpression BindSuperMethodCall(CallExpr e, SuperExpr super, MemberAccessExpr member, IReadOnlyList<VplType>? typeArguments, PendingArguments pending)
    {
        if (!hasThis || currentType?.Type.BaseType is not { } baseType)
        {
            return ErrorExpression(super.Location, hasThis
                ? $"'{currentType!.Name}' has no base class"
                : "'super' can only be used in instance methods and constructors of a class");
        }
        var candidates = LookupMethods(baseType, member.Name);
        if (candidates.Count == 0)
            return ErrorExpression(member.NameLocation, $"'{baseType.Symbol!.Name}' has no method '{member.Name}'");
        var arguments = pending.Bind(candidates);
        if (AnyError(arguments))
            return new BoundErrorExpression(e.Location);
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

    BoundExpression BindConditionalCall(CallExpr e, ConditionalAccessExpr access, PendingArguments pending)
    {
        if (BindConditionalReceiver(access) is not var (receiver, value))
            return new BoundErrorExpression(e.Location);
        var arguments = pending.Bind(MethodCandidates(value.Type, access.Name));
        if (AnyError(arguments))
            return new BoundErrorExpression(e.Location);
        var call = BindMethodCall(e.Location, new BoundLocalAccess(access.Target.Location, value), access.Name, access.NameLocation, null, arguments);
        return MakeConditionalAccess(e.Location, receiver, value, call);
    }

    BoundExpression MakeConditionalAccess(Location location, BoundExpression receiver, LocalSymbol value, BoundExpression access)
    {
        if (access.Type == VplType.Error)
            return access;
        // The member of a nullable value is reached through a copy, which a method would change in vain.
        if (receiver.Type.NonNull.IsStruct && access is BoundCall { Receiver: BoundLocalAccess { Local: var local }, Method.Symbol: { IsReadOnly: false, IsProperty: false } method } && local == value)
        {
            return ErrorExpression(location,
                $"'?.' would call '{method.Name}' on a copy of '{TextOf(receiver.Location)}', so its changes would be lost; check it for null and call '{method.Name}' on a variable");
        }
        if (access.Type == VplType.Void)
            return new BoundConditionalAccess(location, receiver, value, access, access.Type);
        if (access.Type.CanBeNullable)
            return new BoundConditionalAccess(location, receiver, value, access, access.Type.Nullable);
        return ErrorExpression(location, $"'?.' cannot produce a nullable '{access.Type}'");
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
        if (!CheckUnsafeCall(location, method) || !CheckTypeArguments(method.TypeArguments, location))
            return new BoundErrorExpression(location);
        if (IsBorrowHook(method.Symbol))
            return ErrorExpression(location, $"'{method.Symbol.Name}' is called by the compiler around every borrow, and cannot be called directly");
        QueueBoundChecks(method.Symbol.TypeParameters, method.TypeArguments, location);
        return new BoundCall(location, method, receiver, ConvertArguments(method, arguments), isSuperCall);
    }

    /// <summary>A call could unbalance the borrow count, so the borrow methods are left to the compiler.</summary>
    bool IsBorrowHook(FunctionSymbol method) =>
        stdlib.Borrowable is { } borrowable
        && method is { Name: "beginBorrow" or "endBorrow", Parameters.Count: 0, ContainingType: { } owner }
        && Conversions.IsSubtype(owner.Type, borrowable.Type);

    bool CheckUnsafeCall(Location location, MethodReference method)
    {
        if (!method.Symbol.IsUnsafe || InUnsafe)
            return true;
        var what = method.Symbol.IsConstructor ? $"constructor '{method}'" : $"'{method.Symbol.Name}'";
        Error(location, $"{what} is unsafe and can only be called from 'unsafe' code");
        return false;
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
            result.Add(argument.Expression);
        }
        return result;
    }

    BoundExpression BindNew(NewExpr e)
    {
        var type = BindType(e.Type, allowVoid: false);
        var arguments = BindArguments(e.Arguments, type.IsClass ? ConstructorsOf(type) : null);
        if (type == VplType.Error || AnyError(arguments))
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
        return BindNewObject(e.Location, e.Type.Location, type, arguments);
    }

    BoundExpression BindNewObject(Location location, Location typeLocation, VplType type, List<Argument> arguments)
    {
        var constructor = Resolve(location, $"constructor of '{type}'", ConstructorsOf(type), null, arguments);
        if (constructor is null)
            return new BoundErrorExpression(location);
        CheckAccessible(location, type.Symbol!, constructor.Symbol.IsPrivate, $"constructor '{constructor}'");
        if (!CheckArrayElementsDefaultable(typeLocation, type, constructor) || !CheckUnsafeCall(location, constructor))
            return new BoundErrorExpression(location);
        return new BoundNew(location, type, constructor, ConvertArguments(constructor, arguments));
    }

    BoundExpression BindConstruction(Location location, VplType type, List<Argument> arguments)
    {
        if (arguments.Any(a => a.Expression.Type == VplType.Error))
            return new BoundErrorExpression(location);

        var s = type.Symbol!;
        var constructor = Resolve(location, $"constructor of '{type}'", ConstructorsOf(type), null, arguments);
        if (constructor is null)
            return new BoundErrorExpression(location);
        CheckAccessible(location, s, constructor.Symbol.IsPrivate, $"constructor '{constructor}'");
        if (!CheckUnsafeCall(location, constructor))
            return new BoundErrorExpression(location);

        if (constructor.Symbol.IsImplicit)
        {
            var map = TypeMap.For(type);
            var missing = s.Fields.FirstOrDefault(f =>
                f.Declaration.Initializer is null
                && !Conversions.IsDefaultable(map.Apply(f.Type)));
            if (missing is not null)
            {
                return ErrorExpression(location,
                    $"'{type}' has no default value because field '{missing.Name}' has type '{map.Apply(missing.Type)}'; pass it by name, as in '{type}({missing.Name}: ...)'");
            }
        }

        return new BoundConstruction(location, type, constructor, ConvertArguments(constructor, arguments));
    }
}
