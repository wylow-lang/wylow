using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    /// <summary>
    /// Binds a call's arguments. An argument expects its parameter's type when every candidate taking
    /// this many arguments agrees on it, so <c>f(Result.Ok(1))</c> and <c>f(East)</c> know their enum.
    /// A lambda without an agreed function type, or a choice between lambdas, is bound once overload
    /// resolution chooses one. The candidates' first <paramref name="receivers"/> parameters take
    /// arguments that aren't written in the parentheses: an extension's receiver.
    /// </summary>
    List<Argument> BindArguments(IReadOnlyList<Expr> arguments, IReadOnlyList<Candidate>? candidates = null, int receivers = 0)
    {
        var names = ArgumentNames(arguments);
        List<string?> arranged = [.. Enumerable.Repeat<string?>(null, receivers), .. names];
        var arrangements = (candidates is not null && arguments.Count > 0 ? VisibleFrom(candidates, arguments[0].Location.Source) : candidates)?
            .Select(c => (Candidate: c, Arrangement: Arrange(c, arranged, out _)))
            .Where(c => c.Arrangement is not null)
            .ToList();
        return
        [
            .. arguments.Select((syntax, i) =>
            {
                var (a, name, nameLocation) = syntax is NamedArgExpr n ? (n.Value, n.Name, n.NameLocation) : (syntax, null, default(Location));
                if (a is RefArgExpr r)
                    return new Argument(BindExpression(r.Operand), true, syntax.Location, name, nameLocation);
                var expected = arrangements is null ? null : ExpectedArgumentType(arrangements!, i + receivers);
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
    static WylType? ExpectedArgumentType(IReadOnlyList<(Candidate Candidate, int[]? Arrangement)> candidates, int index)
    {
        WylType? agreed = null;
        foreach (var (candidate, arrangement) in candidates)
        {
            var parameter = candidate.Symbol.Parameters[Array.IndexOf(arrangement!, index)];
            var type = (candidate.Owner is null ? TypeMap.Empty : TypeMap.For(candidate.Owner)).Apply(parameter.Type);
            if (parameter.RefKind == RefKind.Ref || type == WylType.Error || (agreed is not null && agreed != type)
                || type.ContainedTypeParameters.Any(p => candidate.Symbol.TypeParameters.Contains(p)))
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

        public List<Argument> Bind(IReadOnlyList<Candidate>? candidates, int receivers = 0) => bound ??= binder.BindArguments(syntax, candidates, receivers);
    }

    static bool AnyError(List<Argument> arguments) => arguments.Any(a => a.Expression.Type == WylType.Error);

    BoundExpression BindCall(CallExpr e, WylType? expected)
    {
        if (e.Callee is SuperExpr)
        {
            BindArguments(e.Arguments);
            return ErrorExpression(currentFunction is { IsConstructor: true, ContainingType.Kind: TypeKind.Class } ? Catalog.SuperCallNotFirst(e.Callee.Location) : Catalog.SuperCallOutsideConstructor(e.Callee.Location));
        }

        var arguments = new PendingArguments(this, e.Arguments);
        var call = e.Callee switch
        {
            NameExpr name => BindNameCall(e, name, arguments, expected),
            PathExpr path => BindPathCall(e, path, arguments),
            MemberAccessExpr member => BindMemberCall(e, member, arguments, expected),
            ConditionalAccessExpr access => BindConditionalCall(e, access, arguments),
            ThisExpr => ErrorExpression(currentFunction is { IsConstructor: true } ? Catalog.ThisCallNotFirst(e.Callee.Location) : Catalog.ThisCallOutsideConstructor(e.Callee.Location)),
            _ => BindCalleeValue(e, BindExpression(e.Callee), arguments),
        };
        arguments.Bind(null);
        return call;
    }

    /// <summary>A call of something other than a named function or method, which must be a function value.</summary>
    BoundExpression BindCalleeValue(CallExpr e, BoundExpression callee, PendingArguments pending) =>
        callee.Type.NonNull.IsFunction || callee.Type == WylType.Error
            ? BindInvocation(e, callee, pending)
            : ErrorExpression(Catalog.NotCallable(e.Callee.Location, TextOf(e.Callee.Location), callee.Type.ToString()));

    BoundExpression BindNameCall(CallExpr e, NameExpr name, PendingArguments pending, WylType? expected)
    {
        if (IsValueName(name.Name))
            return BindCalleeValue(e, BindName(name, null), pending);
        if (LookupTypeParameter(name.Name) is not null)
            return ErrorExpression(Catalog.ConstructTypeParameter(name.Location, name.Name));

        var typeArguments = BindTypeArguments(name.TypeArguments);
        if (typeArguments?.Contains(WylType.Error) == true)
            return new BoundErrorExpression(e.Location);

        if (currentType is { } s && LookupMethods(s.Type, name.Name) is { Count: > 0 } methods)
        {
            var arguments = pending.Bind(methods);
            if (AnyError(arguments))
                return new BoundErrorExpression(e.Location);
            var method = Resolve(e.Location, $"overload of '{name.Name}'", methods, typeArguments, arguments);
            if (method is null)
                return new BoundErrorExpression(e.Location);
            CheckVisible(name.Location, method.Symbol.Reach, $"method '{method.Symbol.ContainingType!.Name}.{name.Name}'");
            if (method.Symbol.IsStatic)
                return MakeCall(e.Location, method, null, arguments);
            if (!hasThis)
                return ErrorExpression(Catalog.InstanceMemberUnavailable(name.Location, "method", name.Name, noThisContext));
            return MakeCall(e.Location, method, new BoundThis(name.Location, stdlib.ThisType(s)), arguments);
        }

        if (ResolveName(name.Name, name.Location) is { HasValue: true } entry)
        {
            var call = BindTopLevelCall(e, name, entry, typeArguments, pending);
            if (call is not BoundErrorExpression)
                CheckQualifiedCall(name, entry);
            return call;
        }
        if (typeArguments is null && ExpectedVariant(name.Name, expected) is { } variant)
            return BindVariant(e.Location, variant.Owner, expected, variant, pending);
        if (HiddenElsewhere(name.Name, name.Location) is { } hidden)
            return ErrorExpression(hidden);
        return ErrorExpression(UndefinedName(name.Location, name.Name, "function"));
    }

    /// <summary><c>gen::seed()</c> or <c>world::Chunk(...)</c>: a call of a name in a module (spec programs.paths).</summary>
    BoundExpression BindPathCall(CallExpr e, PathExpr path, PendingArguments pending)
    {
        var typeArguments = BindTypeArguments(path.Name.TypeArguments);
        if (typeArguments?.Contains(WylType.Error) == true || ResolvePath(path.Modules, path.Name.Name, path.Name.Location) is not { } entry)
            return new BoundErrorExpression(e.Location);
        return BindTopLevelCall(e, path.Name, entry, typeArguments, pending);
    }

    /// <summary>A call of a top-level type, which constructs a struct, or of a function's overloads, found by name or by a path.</summary>
    BoundExpression BindTopLevelCall(CallExpr e, NameExpr name, Entry entry, IReadOnlyList<WylType>? typeArguments, PendingArguments pending)
    {
        if (entry.Type is { } type)
        {
            if (type.Kind != TypeKind.Struct)
            {
                return ErrorExpression(type.Kind switch { TypeKind.Class => Catalog.ClassWithoutNew(name.Location, name.Name), TypeKind.Enum => Catalog.EnumConstructed(name.Location, name.Name, type.Variants.FirstOrDefault()?.Name), _ => Catalog.InterfaceInstance(name.Location, name.Name) });
            }
            var constructed = Construct(type, typeArguments ?? [], name.Location);
            return constructed == WylType.Error
                ? new BoundErrorExpression(e.Location)
                : BindConstruction(e.Location, constructed, pending.Bind(ConstructorsOf(constructed)));
        }

        if (entry.Functions is [{ IsExternConstant: true } constant])
            return ErrorExpression(Catalog.NotCallable(e.Callee.Location, TextOf(e.Callee.Location), constant.ReturnType.ToString()));
        List<Candidate> candidates = [.. entry.Functions!.Select(f => new Candidate(f, null))];
        var functionArguments = pending.Bind(candidates);
        if (AnyError(functionArguments))
            return new BoundErrorExpression(e.Location);
        var function = Resolve(e.Location, $"overload of '{name.Name}'", candidates, typeArguments, functionArguments);
        if (function is null)
            return new BoundErrorExpression(e.Location);
        CheckVisible(name.Location, function.Symbol.Reach, $"function '{name.Name}'");
        return MakeCall(e.Location, function, null, functionArguments);
    }

    BoundExpression BindMemberCall(CallExpr e, MemberAccessExpr member, PendingArguments pending, WylType? expected)
    {
        var typeArguments = BindTypeArguments(member.TypeArguments);
        if (typeArguments?.Contains(WylType.Error) == true)
            return new BoundErrorExpression(e.Location);

        if (member.Target is SuperExpr super)
            return BindSuperMethodCall(e, super, member, typeArguments, pending);

        if (AsEnumName(member.Target, expected) is var (enumSymbol, enumType) && enumSymbol.FindVariant(member.Name) is { } variant)
        {
            if (typeArguments is not null)
                return ErrorExpression(Catalog.VariantTypeArguments(member.NameLocation, enumSymbol.Name, member.Name));
            return BindVariant(e.Location, enumSymbol, enumType, variant, pending);
        }

        if (AsTypeName(member.Target) is { } type)
        {
            if (type == WylType.Error)
                return new BoundErrorExpression(e.Location);
            var s = stdlib.MemberHost(type).Symbol!;
            var staticCandidates = LookupMethods(type, member.Name);
            if (staticCandidates.Count == 0)
                return ErrorExpression(Catalog.NoMethod(member.NameLocation, s.Name, member.Name));
            var arguments = pending.Bind(staticCandidates);
            if (AnyError(arguments))
                return new BoundErrorExpression(e.Location);
            var staticMethod = Resolve(e.Location, $"overload of '{s.Name}.{member.Name}'", staticCandidates, typeArguments, arguments);
            if (staticMethod is null)
                return new BoundErrorExpression(e.Location);
            if (!staticMethod.Symbol.IsStatic)
            {
                return ErrorExpression(Catalog.InstanceMethodThroughType(member.NameLocation, $"{s.Name}.{member.Name}", type.ToString()));
            }
            var declaring = staticMethod.Symbol.ContainingType!;
            CheckVisible(member.NameLocation, staticMethod.Symbol.Reach, $"method '{declaring.Name}.{member.Name}'");
            return MakeCall(e.Location, staticMethod, null, arguments);
        }

        var receiver = BindExpression(member.Target);
        if (receiver.Type == WylType.Error)
            return new BoundErrorExpression(e.Location);
        // A field of function type is called like a method: `button.onClick(5)`.
        if (MethodCandidates(receiver.Type.Narrowed, member.Name).Count == 0
            && LookupField(receiver.Type, member.Name) is var (field, owner) && TypeMap.For(owner).Apply(field.Type).NonNull.IsFunction)
        {
            return BindInvocation(e, BindFieldAccess(member.Location, receiver, member.Name, member.NameLocation), pending);
        }
        // A member of the receiver's type that the call can see, a field included, wins over an
        // extension (spec functions.extensions).
        var use = member.NameLocation.Source;
        bool seesMember = MethodCandidates(receiver.Type.Narrowed, member.Name).Any(c => c.Symbol.Reach.Includes(use))
            || (LookupField(receiver.Type, member.Name) is { } memberField && memberField.Field.Reach.Includes(use));
        if (!seesMember && BindExtensionCall(e.Location, receiver, member.Name, member.NameLocation, typeArguments, pending) is { } extensionCall)
            return extensionCall;
        var receiverArguments = pending.Bind(MethodCandidates(receiver.Type.Narrowed, member.Name));
        if (AnyError(receiverArguments))
            return new BoundErrorExpression(e.Location);
        receiver = RequireNonNull(receiver);
        if (receiver.Type == WylType.Error)
            return new BoundErrorExpression(e.Location);
        return BindMethodCall(e.Location, receiver, member.Name, member.NameLocation, typeArguments, receiverArguments);
    }

    IReadOnlyList<Candidate> MethodCandidates(WylType receiverType, string name) =>
        HasMembers(receiverType) ? LookupMethods(receiverType, name) : [];

    BoundExpression BindMethodCall(
        Location location,
        BoundExpression receiver,
        string name,
        Location nameLocation,
        IReadOnlyList<WylType>? typeArguments,
        List<Argument> arguments)
    {
        var receiverType = receiver.Type;
        var candidates = MethodCandidates(receiverType, name);
        if (candidates.Count == 0)
        {
            return ErrorExpression(LookupField(receiverType, name) is not null ? Catalog.FieldNotMethod(nameLocation, $"{DisplayName(receiverType)}.{name}") : Catalog.NoMethod(nameLocation, DisplayName(receiverType), name, NearestExtension(name, nameLocation)));
        }

        var description = name == SyntaxFacts.IndexerName ? $"indexer of '{receiverType}'" : $"overload of '{DisplayName(receiverType)}.{name}'";
        var method = Resolve(location, description, candidates, typeArguments, arguments);
        if (method is null)
            return new BoundErrorExpression(location);
        var declaring = method.Symbol.ContainingType!;
        if (method.Symbol.IsStatic)
        {
            return ErrorExpression(Catalog.StaticMethodThroughValue(nameLocation, name, declaring.Name));
        }
        CheckVisible(nameLocation, method.Symbol.Reach, $"method '{declaring.Name}.{name}'");
        return MakeCall(location, method, receiver, arguments);
    }

    BoundExpression BindSuperMethodCall(CallExpr e, SuperExpr super, MemberAccessExpr member, IReadOnlyList<WylType>? typeArguments, PendingArguments pending)
    {
        if (!hasThis || currentType?.Type.BaseType is not { } baseType)
        {
            return ErrorExpression(hasThis ? Catalog.SuperWithoutBase(super.Location, currentType!.Name) : Catalog.SuperOutsideInstance(super.Location));
        }
        var candidates = LookupMethods(baseType, member.Name);
        if (candidates.Count == 0)
            return ErrorExpression(Catalog.NoMethod(member.NameLocation, baseType.Symbol!.Name, member.Name));
        var arguments = pending.Bind(candidates);
        if (AnyError(arguments))
            return new BoundErrorExpression(e.Location);
        var method = Resolve(e.Location, $"overload of '{baseType.Symbol!.Name}.{member.Name}'", candidates, typeArguments, arguments);
        if (method is null)
            return new BoundErrorExpression(e.Location);
        var declaring = method.Symbol.ContainingType!;
        if (method.Symbol.IsStatic)
            return ErrorExpression(Catalog.StaticMethodThroughValue(member.NameLocation, member.Name, declaring.Name));
        if (method.Symbol.IsAbstract)
            return ErrorExpression(Catalog.AbstractSuperCall(member.NameLocation, $"{declaring.Name}.{member.Name}"));
        CheckVisible(member.NameLocation, method.Symbol.Reach, $"method '{declaring.Name}.{member.Name}'");
        return MakeCall(e.Location, method, new BoundThis(super.Location, currentType.Type), arguments, isSuperCall: true);
    }

    /// <summary>Binds the receiver of <c>a?.member</c> and a temporary local holding its non-null value.</summary>
    (BoundExpression Receiver, LocalSymbol Value)? BindConditionalReceiver(ConditionalAccessExpr e)
    {
        var receiver = BindExpression(e.Target);
        if (receiver.Type == WylType.Error)
            return null;
        // Through a reference, the member is reached in the place it refers to, not in a copy.
        if (receiver.Type is NullableReferenceType reference)
        {
            // A temporary's name is never written, so it takes the one the other branch gives it
            // rather than the target's text, which can be any expression.
            return (receiver, new LocalSymbol("value", reference, isParameter: false, reference.Kind)
            {
                IsTemporary = true,
                IsReferenceBinding = true,
            });
        }
        if (!receiver.Type.IsNullable)
        {
            Error(Catalog.ConditionalOnNonNullable(e.Target.Location, receiver.Type.ToString()));
            return null;
        }
        return (receiver, new LocalSymbol("value", receiver.Type.NonNull, isParameter: false) { IsTemporary = true });
    }

    BoundExpression BindConditionalFieldAccess(ConditionalAccessExpr e)
    {
        if (BindConditionalReceiver(e) is not var (receiver, value))
            return new BoundErrorExpression(e.Location);
        var access = BindFieldAccess(e.Location, new BoundLocalAccess(e.Target.Location, value, AssumesNonNull: value.IsReferenceBinding), e.Name, e.NameLocation);
        return MakeConditionalAccess(e.Location, receiver, value, access);
    }

    BoundExpression BindConditionalCall(CallExpr e, ConditionalAccessExpr access, PendingArguments pending)
    {
        if (BindConditionalReceiver(access) is not var (receiver, value))
            return new BoundErrorExpression(e.Location);
        var target = new BoundLocalAccess(access.Target.Location, value, AssumesNonNull: value.IsReferenceBinding);
        if (MethodCandidates(value.Type.Narrowed, access.Name).Count == 0 && LookupField(value.Type, access.Name) is null
            && BindExtensionCall(e.Location, target, access.Name, access.NameLocation, null, pending) is { } extensionCall)
        {
            return MakeConditionalAccess(e.Location, receiver, value, extensionCall);
        }
        var arguments = pending.Bind(MethodCandidates(value.Type.Narrowed, access.Name));
        if (AnyError(arguments))
            return new BoundErrorExpression(e.Location);
        var call = BindMethodCall(e.Location, target, access.Name, access.NameLocation, null, arguments);
        return MakeConditionalAccess(e.Location, receiver, value, call);
    }

    BoundExpression MakeConditionalAccess(Location location, BoundExpression receiver, LocalSymbol value, BoundExpression access)
    {
        if (access.Type == WylType.Error)
            return access;
        // The member of a nullable value is reached through a copy, which a method would change in vain.
        if (receiver.Type.NonNull.IsStruct && access is BoundCall { Receiver: BoundLocalAccess { Local: var local }, Method.Symbol: { IsReadOnly: false } method } && local == value)
        {
            return ErrorExpression(Catalog.ConditionalMutCall(location, method.Name, TextOf(receiver.Location)));
        }
        if (receiver.Type.NonNull.IsStruct && access is BoundCall { Receiver: null, Method.Symbol: { IsExtension: true } extension, Arguments: [BoundLocalAccess { Local: var copy }, ..] }
            && copy == value && extension.Parameters[0].RefKind == RefKind.Ref)
        {
            return ErrorExpression(Catalog.ConditionalMutCall(location, extension.Name, TextOf(receiver.Location)));
        }
        if (access.Type == WylType.Void)
            return new BoundConditionalAccess(location, receiver, value, access, access.Type);
        if (access.Type.CanBeNullable)
            return new BoundConditionalAccess(location, receiver, value, access, access.Type.Nullable);
        return ErrorExpression(Catalog.ConditionalNotNullable(location, access.Type.ToString()));
    }

    /// <summary>
    /// Checks a member's <c>where</c> clause against the type arguments its type is used with at
    /// this call (spec <c>generics.bounds</c>).
    /// </summary>
    void CheckMemberBounds(MethodReference method, Location location)
    {
        var symbol = method.Symbol;
        if (!symbol.HasMemberBounds || method.Owner is not { } owner)
            return;
        var member = symbol.IsConstructor ? symbol.Signature : $"{symbol.ContainingType?.Name}.{symbol.Signature}";
        foreach (var parameter in symbol.CopyableParameters)
        {
            var argument = method.Map.Apply(parameter.Type);
            if (argument != WylType.Error && !argument.IsCopyableIn(currentFunction))
                Error(Catalog.MemberBoundNotSatisfied(location, argument.ToString(), "copyable", member, property: true));
        }
        foreach (var parameter in symbol.SendableParameters)
        {
            var argument = method.Map.Apply(parameter.Type);
            if (argument != WylType.Error && !argument.IsSendableIn(currentFunction))
                Error(Catalog.MemberBoundNotSatisfied(location, argument.ToString(), "sendable", member, property: true));
        }
        foreach (var bound in symbol.MemberInterfaceBounds)
        {
            var argument = method.Map.Apply(bound.Parameter.Type);
            var required = method.Map.Apply(bound.Bound);
            if (argument != WylType.Error && !stdlib.SatisfiesBound(argument, required, currentFunction))
                Error(Catalog.MemberBoundNotSatisfied(location, argument.ToString(), required.ToString(), member, property: false));
        }
    }

    BoundExpression MakeCall(Location location, MethodReference method, BoundExpression? receiver, List<Argument> arguments, bool isSuperCall = false)
    {
        if (receiver is not null && !CheckMutReceiver(location, method, receiver))
            return new BoundErrorExpression(location);
        if (!CheckUnsafeCall(location, method) || !CheckTypeArguments(method.TypeArguments, location))
            return new BoundErrorExpression(location);
        if (IsBorrowHook(method.Symbol) && !InUnsafe)
            return ErrorExpression(Catalog.BorrowHookCall(location, method.Symbol.Name));
        QueueBoundChecks(method.Symbol.TypeParameters, method.TypeArguments, location);
        CheckMemberBounds(method, location);
        return new BoundCall(location, method, receiver, ConvertArguments(method, arguments), isSuperCall);
    }

    /// <summary>
    /// A <c>mut</c> method changes its receiver, which must be a variable it may change: a temporary
    /// is a value, not a variable, so its changes would be lost (spec structs.methods.mut).
    /// <paramref name="onTemporary"/> says so in the words of what the call does, if it isn't a call in the source.
    /// </summary>
    bool CheckMutReceiver(Location location, MethodReference method, BoundExpression receiver, Diagnostic? onTemporary = null)
    {
        // Calling a method on an object never modifies the place holding the reference.
        if (receiver.Type.IsReference || method.Symbol.IsReadOnly)
            return true;
        var (kind, reason) = ClassifyPlace(receiver);
        if (kind == PlaceKind.Mutable)
            return true;
        if (kind == PlaceKind.Temporary)
            Error(onTemporary ?? Catalog.MutOnTemporary(location, method.Symbol.Name));
        else
            Error(Catalog.MutOnReadOnly(location, method.Symbol.Name, receiver is BoundThis ? "this" : TextOf(receiver.Location), reason));
        return false;
    }

    /// <summary>A call could unbalance the borrow count, so only the compiler and unsafe code call the borrow methods.</summary>
    bool IsBorrowHook(FunctionSymbol method) =>
        stdlib.Borrowable is { } borrowable
        && method is { Name: "beginBorrow" or "endBorrow", Parameters.Count: 0, ContainingType: { } owner }
        && Conversions.IsSubtype(owner.Type, borrowable.Type, function: null);

    bool CheckUnsafeCall(Location location, MethodReference method)
    {
        if (!method.Symbol.IsUnsafe || InUnsafe)
            return true;
        var what = method.Symbol.IsConstructor ? $"constructor '{method}'" : $"'{method.Symbol.Name}'";
        Error(Catalog.UnsafeCall(location, what));
        return false;
    }

    List<BoundExpression> ConvertArguments(MethodReference method, List<Argument> arguments)
    {
        var result = new List<BoundExpression>();
        for (int i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            var parameterType = method.ParameterType(i);
            if (method.ParameterRefKind(i) != RefKind.Ref)
            {
                result.Add(Convert(argument.Expression, parameterType));
                continue;
            }
            if (parameterType is NullableReferenceType reference)
            {
                result.Add(ConvertToNullableReference(argument.Expression, reference, "'ref?' argument") ?? new BoundErrorExpression(argument.Location));
                continue;
            }

            var place = NarrowReference(argument.Expression);
            var (kind, reason) = ClassifyPlace(place);
            // A `this ref` receiver needs a mutable place, as the receiver of a `mut` method does.
            if (argument.IsReceiver && kind == PlaceKind.Temporary)
                Error(Catalog.RefReceiverOnTemporary(argument.Location, method.Symbol.Name));
            else if (argument.IsReceiver && kind == PlaceKind.ReadOnly)
                Error(Catalog.RefReceiverOnReadOnly(argument.Location, method.Symbol.Name, TextOf(place.Location), reason));
            else if (kind == PlaceKind.Temporary)
                Error(Catalog.RefArgumentNotPlace(argument.Location, Elementless(place)));
            else if (kind == PlaceKind.ReadOnly)
                Error(Catalog.RefArgumentReadOnly(argument.Location, TextOf(place.Location), reason));
            result.Add(place);
        }
        return result;
    }

    BoundExpression BindNew(NewExpr e)
    {
        var type = BindType(e.Type, allowVoid: false);
        var arguments = BindArguments(e.Arguments, type.IsClass ? ConstructorsOf(type) : null);
        if (type == WylType.Error || AnyError(arguments))
            return new BoundErrorExpression(e.Location);
        if (type.IsStruct)
            return ErrorExpression(Catalog.StructWithNew(e.Type.Location, e.Type.Name));
        if (type.IsEnum)
            return ErrorExpression(Catalog.EnumWithNew(e.Type.Location, e.Type.Name, type.Symbol!.Variants.FirstOrDefault()?.Name));
        if (type.IsInterface)
            return ErrorExpression(Catalog.InterfaceInstance(e.Type.Location, type.ToString()));
        if (!type.IsClass)
            return ErrorExpression(Catalog.NotNewable(e.Type.Location, type.ToString()));
        if (type.Symbol!.IsAbstract)
            return ErrorExpression(Catalog.AbstractInstance(e.Type.Location, type.ToString()));
        return BindNewObject(e.Location, e.Type.Location, type, arguments);
    }

    BoundExpression BindNewObject(Location location, Location typeLocation, WylType type, List<Argument> arguments)
    {
        var constructor = Resolve(location, $"constructor of '{type}'", ConstructorsOf(type), null, arguments);
        if (constructor is null)
            return new BoundErrorExpression(location);
        CheckVisible(location, constructor.Symbol.Reach, $"constructor '{constructor}'");
        CheckMemberBounds(constructor, location);
        if (!CheckArrayElementsDefaultable(typeLocation, type, constructor) || !CheckUnsafeCall(location, constructor))
            return new BoundErrorExpression(location);
        return new BoundNew(location, type, constructor, ConvertArguments(constructor, arguments));
    }

    BoundExpression BindConstruction(Location location, WylType type, List<Argument> arguments)
    {
        if (arguments.Any(a => a.Expression.Type == WylType.Error))
            return new BoundErrorExpression(location);

        var s = type.Symbol!;
        var constructor = Resolve(location, $"constructor of '{type}'", ConstructorsOf(type), null, arguments);
        if (constructor is null)
            return new BoundErrorExpression(location);
        CheckVisible(location, constructor.Symbol.Reach, $"constructor '{constructor}'");
        CheckMemberBounds(constructor, location);
        if (!CheckUnsafeCall(location, constructor))
            return new BoundErrorExpression(location);

        if (!CheckDefaultValue(location, type, constructor))
            return new BoundErrorExpression(location);

        return new BoundConstruction(location, type, constructor, ConvertArguments(constructor, arguments));
    }
}
