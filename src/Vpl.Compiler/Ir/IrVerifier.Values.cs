using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Ir;

public sealed partial class IrVerifier
{
    /// <summary>What a reference, view, pointer, or borrowed local refers to, which its validity depends on.</summary>
    readonly Dictionary<IrLocal, Dependency> dependencies = [];
    readonly HashSet<IrLocal> borrowingHolds = [];
    readonly Dictionary<IrLocal, IrRvalue> slotObjects = [];
    readonly List<IrLocal> pendingMoves = [];

    /// <summary>What may be true at a point, on some path to it; <see cref="Assigned"/> is what is true on every path.</summary>
    sealed class State
    {
        public bool Reachable { get; private init; } = true;
        public HashSet<IrLocal> Assigned { get; private init; } = [];
        public HashSet<IrLocal> MaybeAssigned { get; private init; } = [];
        public HashSet<IrLocal> Moved { get; private init; } = [];
        public HashSet<IrLocal> Ended { get; private init; } = [];

        /// <summary>Dependents that may no longer be used, and why.</summary>
        public Dictionary<IrLocal, string> Invalid { get; private init; } = [];

        public static State Unreachable => new() { Reachable = false };

        public State Clone() => new()
        {
            Reachable = Reachable,
            Assigned = [.. Assigned],
            MaybeAssigned = [.. MaybeAssigned],
            Moved = [.. Moved],
            Ended = [.. Ended],
            Invalid = new(Invalid),
        };

        public static State Merge(State a, State b)
        {
            if (!a.Reachable)
                return b;
            if (!b.Reachable)
                return a;
            var invalid = new Dictionary<IrLocal, string>(a.Invalid);
            foreach (var (local, reason) in b.Invalid)
                invalid.TryAdd(local, reason);
            return new()
            {
                Assigned = [.. a.Assigned.Intersect(b.Assigned)],
                MaybeAssigned = [.. a.MaybeAssigned.Union(b.MaybeAssigned)],
                Moved = [.. a.Moved.Union(b.Moved)],
                Ended = [.. a.Ended.Union(b.Ended)],
                Invalid = invalid,
            };
        }

        public bool SameAs(State other) =>
            Reachable == other.Reachable && Assigned.SetEquals(other.Assigned) && MaybeAssigned.SetEquals(other.MaybeAssigned)
            && Moved.SetEquals(other.Moved) && Ended.SetEquals(other.Ended) && Invalid.Keys.ToHashSet().SetEquals(other.Invalid.Keys);

        /// <summary>A local declared again, as in a later iteration, starts over.</summary>
        public void Forget(IrLocal local)
        {
            Assigned.Remove(local);
            MaybeAssigned.Remove(local);
            Moved.Remove(local);
            Ended.Remove(local);
            Invalid.Remove(local);
        }
    }

    enum StorageKind
    {
        /// <summary>A local's own storage.</summary>
        Frame,
        /// <summary>A field of an object, which any object of the class may share.</summary>
        Object,
        /// <summary>Storage the function can't see: a pointer's target, a caller's place, or a container's elements.</summary>
        Unknown,
    }

    /// <summary>
    /// Where a place lies, for comparing places that may be the same storage: a frame local, or the
    /// field of some object of the class (a field of no class when the whole object), and the
    /// projections inside it. <see cref="Within"/> for somewhere in it.
    /// </summary>
    sealed record Resolved(StorageKind Kind, IrLocal? Local, FieldSymbol? Field, IReadOnlyList<IrProjection> Path, bool Within)
    {
        public static readonly Resolved Unknown = new(StorageKind.Unknown, null, null, [], Within: true);

        /// <summary>The index of the first projection whose storage a write before it can destroy: a nullable value's or a variant's.</summary>
        public int Destructible => Path.ToList().FindIndex(p => p is IrAssumed or IrVariantField);
    }

    /// <summary>
    /// What a dependent needs: its roots, the locals whose storage, or with <c>Through</c>, whose
    /// object or target, it lies in; and what it refers to. An alias (a borrowed local) needs nothing
    /// to write that; a reference needs nothing to destroy it. One taken from a <c>Borrowable</c>
    /// that isn't borrowed needs no code to run.
    /// </summary>
    sealed record Dependency(IReadOnlyList<(IrLocal Local, bool Through)> Roots, Resolved? Target, bool Alias, bool NeedsBorrow)
    {
        public Dependency Union(Dependency other) => new(
            [.. Roots.Union(other.Roots)],
            Target == other.Target ? Target : Resolved.Unknown,
            Alias || other.Alias,
            NeedsBorrow || other.NeedsBorrow);

        /// <summary>The dependency of a reference, view, pointer, or alias an rvalue makes, if it makes one.</summary>
        public static Dependency? Of(IrRvalue value, IrVerifier verifier) => value switch
        {
            IrRef reference => verifier.Into(reference.Place, alias: false),
            IrAddr address => verifier.Into(address.Place, alias: false),
            IrData data => verifier.Into(data.Place, alias: false),
            IrUse use => Of(use.Operand, verifier),
            IrConvert conversion => Of(conversion.Operand, verifier),
            IrCall call when call.Method.Symbol.ReturnRefKind != RefKind.None || call.Type.IsRefStruct => verifier.Returned(call),
            IrInvoke invoke when invoke.Signature.Symbol.ReturnRefKind != RefKind.None || invoke.Type.IsRefStruct => new([], Resolved.Unknown, false, false),
            IrConstruct { Type.IsRefStruct: true } construction => Made(construction.Arguments.OfType<IrRead>(), verifier),
            _ => null,
        };

        public static Dependency? Of(IrOperand operand, IrVerifier verifier) => operand switch
        {
            IrRead { Ownership: IrOwnership.Borrow } read => verifier.Into(read.Place, alias: true),
            IrRead { Place: { IsDeref: false, Projections.Count: 0, Local: var local } } when verifier.dependencies.TryGetValue(local, out var copied) =>
                copied with { Roots = [(local, true)], Alias = false },
            _ => null,
        };

        /// <summary>A <c>ref struct</c> made from views and pointers views what they do.</summary>
        static Dependency Made(IEnumerable<IrRead> arguments, IrVerifier verifier)
        {
            var parts = arguments.Where(a => !a.Place.IsDeref && a.Place.Projections.Count == 0 && verifier.dependencies.ContainsKey(a.Place.Local))
                .Select(a => a.Place.Local).ToList();
            var target = parts.Count == 1 ? verifier.dependencies[parts[0]].Target! with { Within = true } : Resolved.Unknown;
            return new([.. parts.Select(p => (p, true))], target, false, false);
        }
    }

    Dependency Into(IrPlace place, bool alias) => new([(place.Local, place.IsDeref)], Resolve(place, within: false), alias, NeedsBorrow: false);

    /// <summary>A returned reference or view points into the receiver's storage: a struct place's, or an object's, which may be a <c>Borrowable</c>.</summary>
    Dependency Returned(IrCall call) => call.Receiver switch
    {
        IrRefArgument { Place: { Type.IsRefStruct: true } span } => new([(span.Local, true)], Resolved.Unknown, false, false),
        IrRefArgument reference => Into(reference.Place, alias: false) with { Target = Resolve(reference.Place, within: true) },
        IrRead { Place: var holder } => new([(holder.Local, true)], Resolved.Unknown, false,
            NeedsBorrow: MayBeBorrowable(holder.Type) && !borrowingHolds.Contains(holder.Local)),
        _ => new([], Resolved.Unknown, false, false),
    };

    bool MayBeBorrowable(VplType type) => type.TypeParameter is { } parameter
        ? parameter.Bounds.Any(MayBeBorrowable)
        : borrowableClasses.Any(c => c == type.NonNull.Symbol || Conversions.Supertypes(c.Type).Any(t => t.Symbol == type.NonNull.Symbol));

    Resolved Resolve(IrPlace place, bool within)
    {
        var local = place.Local;
        if (!place.IsDeref)
        {
            // A struct method's `this` is its caller's storage.
            return local.IsThis && !local.Type.IsReference ? Resolved.Unknown : new(StorageKind.Frame, local, null, place.Projections, within);
        }
        bool isObject = local.Kind == IrLocalKind.Hold || (local.Kind is IrLocalKind.Value or IrLocalKind.Borrowed && local.Type.NonNull.IsReference);
        if (!isObject && dependencies.TryGetValue(local, out var dependency) && dependency.Target is { Kind: not StorageKind.Unknown } target)
            return target with { Path = [.. target.Path, .. place.Projections], Within = target.Within || within };
        if (!isObject)
            return Resolved.Unknown;
        var first = place.Projections.FirstOrDefault() as IrField;
        return new(StorageKind.Object, local, first?.Field, [.. place.Projections.Skip(first is null ? 0 : 1)], within);
    }

    static bool SameStorage(Resolved a, Resolved b) => (a.Kind, b.Kind) switch
    {
        (StorageKind.Frame, StorageKind.Frame) => a.Local == b.Local,
        (StorageKind.Frame, _) or (_, StorageKind.Frame) => false,
        (StorageKind.Object, StorageKind.Object) => a.Field is null || b.Field is null || a.Field == b.Field,
        _ => true,
    };

    /// <summary>Whether one path leads into the other: elements may be the same whatever their indices.</summary>
    static bool PathsOverlap(IReadOnlyList<IrProjection> a, IReadOnlyList<IrProjection> b)
    {
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            bool same = (a[i], b[i]) switch
            {
                (IrField x, IrField y) => x.Field == y.Field,
                (IrVariantField x, IrVariantField y) => x.Field == y.Field,
                (IrElement, IrElement) or (IrAssumed, IrAssumed) => true,
                _ => false,
            };
            if (!same)
                return false;
        }
        return true;
    }

    static bool Overlap(Resolved write, Resolved place) =>
        SameStorage(write, place) && (write.Kind == StorageKind.Unknown || place.Kind == StorageKind.Unknown || PathsOverlap(write.Path, place.Path));

    /// <summary>Whether a write may replace the nullable value or variant that the place lies in, destroying the place.</summary>
    static bool Destroys(Resolved write, Resolved place)
    {
        int destructible = place.Destructible;
        if (destructible < 0 || !SameStorage(write, place))
            return false;
        return write.Kind == StorageKind.Unknown || (write.Path.Count <= destructible && PathsOverlap(write.Path, place.Path));
    }

    /// <summary>
    /// Whether code the function calls may write the place: a field that is <c>mut</c>, storage the
    /// function can't see, or a local a lambda assigns. Only a class's own constructors assign a field
    /// that isn't <c>mut</c>, and only of the object they make.
    /// </summary>
    static bool CodeWrites(Resolved place) => place.Kind switch
    {
        StorageKind.Frame => place.Local!.IsAssignedByLambda,
        StorageKind.Object => place.Field is null || place.Field.IsMutable,
        _ => true,
    };

    static bool CodeDestroys(Resolved place) => place.Destructible >= 0 && CodeWrites(place);

    /// <summary>Why the effects would make a dependent unusable, or null.</summary>
    static string? Breaks(Dependency dependency, IrEffects effects, IReadOnlyList<Resolved> writes)
    {
        if (dependency.NeedsBorrow && effects.RunsCode)
            return "it points into a Borrowable that isn't borrowed, and code that may change its length runs";
        if (dependency.Target is not { } target)
            return null;
        if (dependency.Alias && (writes.Any(w => Overlap(w, target)) || (effects.RunsCode && CodeWrites(target))))
            return "the place it borrows may be written";
        if (!dependency.Alias && (writes.Any(w => Destroys(w, target)) || (effects.RunsCode && CodeDestroys(target))))
            return "the nullable value or variant it points into may be replaced";
        return null;
    }

    bool Visible(IrLocal local) => scopes.Any(s => s.Contains(local));

    void Apply(State state, IrEffects effects, string what)
    {
        var writes = effects.Writes.Select(w => Resolve(w.Place, w.Within)).ToList();
        foreach (var (local, dependency) in dependencies.ToList())
        {
            var checkedDependency = local.IsUnwrittenElement ? dependency with { Target = null } : dependency;
            if (Visible(local) && !state.Invalid.ContainsKey(local) && Breaks(checkedDependency, effects, writes) is { } reason)
                Invalidate(state, local, $"{reason}, by {what}");
        }
        foreach (var write in effects.Writes.Where(w => !w.Place.IsDeref && w.Place.Projections.Count == 0))
            DependentsOf(write.Place.Local, state, $"'{N(write.Place.Local)}' may be reassigned by {what}", throughOnly: true);
        if (effects.RunsCode)
        {
            foreach (var local in scopes.SelectMany(s => s).Where(l => l.IsAssignedByLambda).ToList())
                DependentsOf(local, state, $"a lambda may reassign '{N(local)}' during {what}", throughOnly: true);
        }
    }

    void Invalidate(State state, IrLocal local, string reason)
    {
        if (!state.Invalid.TryAdd(local, reason))
            return;
        DependentsOf(local, state, $"'{N(local)}' was invalidated: {reason}");
    }

    void DependentsOf(IrLocal root, State state, string reason, bool throughOnly = false)
    {
        foreach (var (local, dependency) in dependencies.ToList())
        {
            if (local != root && Visible(local) && dependency.Roots.Any(r => r.Local == root && (r.Through || !throughOnly)))
                Invalidate(state, local, reason);
        }
    }

    void Define(IrLocal local, IrRvalue value, State state)
    {
        state.Assigned.Add(local);
        state.MaybeAssigned.Add(local);
        if (Dependency.Of(value, this) is { } dependency)
            dependencies[local] = dependency;
        else
            dependencies.Remove(local);
        if (value is IrHold { Borrows: true })
            borrowingHolds.Add(local);
        else
            borrowingHolds.Remove(local);
    }

    void Use(IrLocal local, State state, bool reads)
    {
        if (!Visible(local))
        {
            Error(declarations.ContainsKey(local) ? $"'{N(local)}' is used outside its scope" : $"'{N(local)}' isn't declared");
            return;
        }
        if (reads && !state.Assigned.Contains(local))
            Error($"'{N(local)}' may be read before it is stored");
        if (state.Moved.Contains(local))
            Error($"'{N(local)}' is used after it is moved");
        if (state.Ended.Contains(local))
            Error($"'{N(local)}' is used after it ends");
        if (state.Invalid.TryGetValue(local, out var reason))
            Error($"'{N(local)}' is used where it may no longer be valid: {reason}");
    }

    void Place(IrPlace place, State state, bool reads)
    {
        var local = place.Local;
        Use(local, state, reads || place.IsDeref);
        if (place.IsDeref)
        {
            bool derefable = local.Kind is IrLocalKind.Reference or IrLocalKind.Hold
                || (local.Kind is IrLocalKind.Value or IrLocalKind.Borrowed && (local.Type.NonNull.IsReference || local.Type.IsPointer));
            if (!derefable)
                Error($"'{N(local)}' can't be dereferenced");
        }
        else if (local.Kind == IrLocalKind.Reference && place.Projections.Count > 0)
            Error($"'{N(local)}' is a reference: the place it refers to is '*{N(local)}'");

        var holder = place.BaseType;
        for (int i = 0; i < place.Projections.Count; i++)
        {
            var projection = place.Projections[i];
            switch (projection)
            {
                case IrField when holder.NonNull.IsReference && !(place.IsDeref && i == 0):
                    Error("a place dereferences only at its base: an object reached through a field must be read into a local first");
                    break;
                case IrElement element:
                    if (element.Index is not (IrConstant { Type.IsInteger: true } or IrRead { Type.IsInteger: true, Place: { IsDeref: false, Projections.Count: 0 } }))
                        Error("an element's index is an integer constant or local");
                    Operand(element.Index, state);
                    break;
            }
            if (projection.Type == VplType.Error)
                Error($"a projection doesn't apply to a '{holder}'");
            holder = projection.Type;
        }
    }

    void Operand(IrOperand operand, State state)
    {
        if (operand is not IrRead read)
            return;
        Place(read.Place, state, reads: true);
        if (read.Ownership != IrOwnership.Move)
            return;
        var local = read.Place.Local;
        if (!local.IsTemporary || read.Place.IsDeref || local.Kind != IrLocalKind.Value)
            Error($"only a temporary's value can be moved, not '{N(local)}''s");
        else if (pendingMoves.Contains(local))
            Error($"'{N(local)}' is moved twice");
        else
            pendingMoves.Add(local);
    }

    void Argument(IrArgument argument, State state)
    {
        switch (argument)
        {
            case IrOperand operand:
                Operand(operand, state);
                break;
            case IrRefArgument reference:
                Place(reference.Place, state, reads: true);
                break;
            case IrAddrArgument address:
                Place(address.Place, state, reads: true);
                break;
        }
    }

    /// <summary>Checks an rvalue's operands and types, applies its effects, and moves what it moves.</summary>
    void Rvalue(IrRvalue value, State state, IrLocal? defined)
    {
        pendingMoves.Clear();
        switch (value)
        {
            case IrUse use:
                Operand(use.Operand, state);
                if (use.Operand is IrRead { Ownership: IrOwnership.Borrow } && defined?.Kind != IrLocalKind.Borrowed)
                    Error("a borrowed value is only kept in a borrowed local");
                break;
            case IrUnary unary:
                Operand(unary.Operand, state);
                CheckLoc(unary.Loc);
                if ((unary.Operator == BoundUnaryOperator.Negate && unary.Operand.Type.IsInteger) != (unary.Loc is not null))
                    Error("an integer negation, and only one, carries a location");
                break;
            case IrBinary binary:
                Operand(binary.Left, state);
                Operand(binary.Right, state);
                CheckLoc(binary.Loc);
                if (IrBinary.IsChecked(binary.Operator, binary.Left.Type) != (binary.Loc is not null))
                    Error("checked integer arithmetic, and only that, carries a location");
                break;
            case IrCall call:
                Call(call, state);
                break;
            case IrInvoke invoke:
                Operand(invoke.Callee, state);
                if (!invoke.Callee.Type.NonNull.IsFunction)
                    Error($"invokes a '{invoke.Callee.Type}', which isn't a function");
                Arguments(invoke.Signature, invoke.Arguments, state);
                break;
            case IrNew creation:
                Arguments(creation.Constructor, creation.Arguments, state);
                CheckLoc(creation.Loc);
                if (!creation.Type.IsClass)
                    Error($"'new' makes an object, not a '{creation.Type}'");
                Slot(creation.Slot, value, state);
                break;
            case IrConstruct construction:
                Arguments(construction.Constructor, construction.Arguments, state);
                CheckLoc(construction.Loc);
                if (construction.Arguments.Any(a => a is IrFieldDefault) && !construction.Constructor.Symbol.IsMemberwise)
                    Error("only a memberwise constructor's call leaves fields out");
                break;
            case IrVariant variant:
                foreach (var field in variant.Fields)
                    Operand(field, state);
                var map = TypeMap.For(variant.Type);
                if (variant.Type.Symbol != variant.Variant.Owner || variant.Fields.Count != variant.Variant.Fields.Count
                    || variant.Fields.Where((f, i) => f.Type != map.Apply(variant.Variant.Fields[i].Type)).Any())
                    Error($"the fields don't fit variant '{variant.Variant.Name}' of '{variant.Type}'");
                break;
            case IrArray array:
                foreach (var element in array.Elements)
                    Operand(element, state);
                if (!array.Type.IsFixedArray || array.Elements.Count != array.Type.FixedLength || array.Elements.Any(e => e.Type != array.Type.ElementType))
                    Error($"the elements don't fit a '{array.Type}'");
                break;
            case IrDefault:
                break;
            case IrClosure closure:
                foreach (var capture in closure.Captures)
                    Operand(capture, state);
                if (!closure.Function.IsEscaping)
                    Error("a closure is made from an escaping lambda");
                if (closure.Captures.Count != closure.Function.Captures.Count
                    || closure.Captures.Where((c, i) => c.Type != closure.Function.Captures[i].Type).Any())
                    Error("the captures don't fit the lambda's");
                Slot(closure.Slot, value, state);
                Closure(closure.Function);
                break;
            case IrLambda lambda:
                if (lambda.Function.IsEscaping)
                    Error("an escaping lambda is made by a closure");
                Lambda(lambda.Function, state);
                break;
            case IrChoose choice:
                Operand(choice.Index, state);
                foreach (var option in choice.Options)
                    Operand(option, state);
                if (!choice.Index.Type.IsInteger || choice.Options.Any(o => !o.Type.NonNull.IsFunction))
                    Error("a choice selects a function with an integer");
                break;
            case IrRef reference:
                Place(reference.Place, state, reads: true);
                break;
            case IrAddr address:
                Place(address.Place, state, reads: true);
                break;
            case IrData data:
                Place(data.Place, state, reads: true);
                if (!data.Place.Type.IsFixedArray)
                    Error($"'data' points into a fixed array, not a '{data.Place.Type}'");
                break;
            case IrTest test:
                Operand(test.Operand, state);
                var type = test.Operand.Type;
                bool fits = test.Kind switch
                {
                    IrTestKind.NotNull => type.IsNullableReference || (type.IsNullable && type.NonNull.IsReference) || type.IsPointer,
                    IrTestKind.HasValue => type.IsNullableValue,
                    IrTestKind.Tag => test.Variant is { } v && type.Symbol == v.Owner,
                    IrTestKind.Class => test.Target is not null && type.NonNull.IsReference,
                    _ => false,
                };
                if (!fits)
                    Error($"a '{test.Kind}' test doesn't apply to a '{type}'");
                break;
            case IrConvert conversion:
                Operand(conversion.Operand, state);
                CheckLoc(conversion.Loc);
                break;
            case IrHold hold:
                Place(hold.Place, state, reads: true);
                if (!IsHolder(hold.Place.Type))
                    Error($"a hold holds an object or a string, not a '{hold.Place.Type}'");
                break;
            default:
                throw new InvalidOperationException($"unexpected IR rvalue {value.GetType().Name}");
        }

        var effects = IrEffects.Of(value);
        if (value is IrCall or IrInvoke or IrNew or IrConstruct)
            CheckInUse(value, effects, state);
        Apply(state, effects, Describe(value));
        CommitMoves(state);
    }

    /// <summary>A temporary moved by an instruction is gone once the instruction is done.</summary>
    void CommitMoves(State state)
    {
        foreach (var moved in pendingMoves)
        {
            state.Moved.Add(moved);
            DependentsOf(moved, state, $"'{N(moved)}' was moved");
        }
        pendingMoves.Clear();
    }

    static string Describe(IrRvalue value) => value switch
    {
        IrCall call => $"the call of '{call.Method.Symbol.Name}'",
        IrInvoke => "the call of a function value",
        IrNew => "a 'new'",
        IrConstruct => "a construction",
        IrHold => "a borrow",
        _ => "an instruction",
    };

    void Call(IrCall call, State state)
    {
        var method = call.Method;
        if (method.Symbol.IsInstanceMethod != call.Receiver is not null)
            Error(call.Receiver is null ? $"calls method '{method.Symbol.Name}' without a receiver" : $"calls '{method.Symbol.Name}' with a receiver");
        if (call.Receiver is { } receiver)
        {
            Argument(receiver, state);
            // What it returns points into what the receiver holds, which must outlive the call.
            bool returnsInto = method.Symbol.ReturnRefKind != RefKind.None || call.Type.IsRefStruct;
            if (returnsInto && receiver is IrRead read && !(IsHolder(read.Type) && read.Place is { IsDeref: false, Projections.Count: 0 }))
                Error("a reference or view a method returns points into its receiver: an object or string in a local, or a struct passed as a place");
        }
        else if (method.Symbol.ReturnRefKind != RefKind.None)
            Error("only a method returns a reference, into its receiver");
        Arguments(method, call.Arguments, state);
        CheckLoc(call.Loc);
        if (method.Symbol.PassesLocation != call.Loc is not null)
            Error(call.Loc is null ? $"'{method.Symbol.Name}' is given its caller's location, which the call doesn't pass" : $"passes a location to '{method.Symbol.Name}', which takes none");
    }

    /// <summary>A value that holds what a reference or view into it points to: an object, or a string, whose copies share its bytes.</summary>
    static bool IsHolder(VplType type) => type.NonNull.IsReference || type.IsTypeParameter || type == VplType.String;

    void Arguments(MethodReference method, IReadOnlyList<IrArgument> arguments, State state)
    {
        var parameters = method.Symbol.Parameters;
        if (arguments.Count != parameters.Count)
        {
            Error($"passes {arguments.Count} arguments to '{method.Symbol.Name}', which takes {parameters.Count}");
            return;
        }
        for (int i = 0; i < arguments.Count; i++)
        {
            Argument(arguments[i], state);
            var type = method.ParameterType(i);
            var kind = method.ParameterRefKind(i);
            bool fits = arguments[i] switch
            {
                IrAddrArgument address => type.IsNullableReference && address.Place.Type == type.ReferencedType && (address.Kind == type.ReferenceKind || type.ReferenceKind == RefKind.In),
                IrRefArgument reference => !type.IsNullableReference && reference.Place.Type == type && (reference.Kind == kind || kind == RefKind.In),
                IrOperand operand => operand.Type == type || (type.IsNullableReference && operand.Type.ReferencedType == type.ReferencedType),
                IrFieldDefault field => parameters[i].Field == field.Field,
                _ => false,
            };
            if (fits && arguments[i] is IrOperand && kind != RefKind.None && !type.IsNullableReference)
                fits = false;
            if (!fits)
                Error($"argument {i + 1} doesn't fit parameter '{FunctionSymbol.ParameterText(kind, type)} {parameters[i].Name}'");
        }
    }

    /// <summary>
    /// What a call is given in place, a reference or a borrowed value, is used while it runs, so the
    /// call's own effects must not invalidate it: a receiver that the callee could reassign meanwhile
    /// must be held first.
    /// </summary>
    void CheckInUse(IrRvalue value, IrEffects effects, State state)
    {
        if (!effects.RunsCode && effects.Writes.Count == 0)
            return;
        IEnumerable<IrArgument> passed = value switch
        {
            IrCall call => call.Receiver is null ? call.Arguments : [call.Receiver, .. call.Arguments],
            IrInvoke invoke => [invoke.Callee, .. invoke.Arguments],
            IrNew creation => creation.Arguments,
            IrConstruct construction => construction.Arguments,
            _ => [],
        };
        foreach (var argument in passed)
        {
            var (place, alias) = argument switch
            {
                IrRefArgument reference => (reference.Place, false),
                IrAddrArgument address => (address.Place, false),
                IrRead { Ownership: IrOwnership.Borrow } read => (read.Place, true),
                _ => (null, false),
            };
            if (place is not null && WouldBreak(place, alias, effects, state) is { } reason)
                Error($"'{IrPrinter.Print(root, place)}' is used during {Describe(value)}, but {reason}");
        }
    }

    string? WouldBreak(IrPlace place, bool alias, IrEffects effects, State state)
    {
        var writes = effects.Writes.Select(w => Resolve(w.Place, w.Within)).ToList();
        if (Breaks(Into(place, alias), effects, writes) is { } reason)
            return reason;
        var local = place.Local;
        if (place.IsDeref && effects.Writes.Any(w => !w.Place.IsDeref && w.Place.Projections.Count == 0 && w.Place.Local == local))
            return $"it may reassign '{N(local)}'";
        if (place.IsDeref && effects.RunsCode && local.IsAssignedByLambda)
            return $"a lambda may reassign '{N(local)}'";
        var trial = state.Clone();
        Apply(trial, effects, "it");
        return !state.Invalid.ContainsKey(local) && trial.Invalid.TryGetValue(local, out var invalid) ? invalid : null;
    }

    void Slot(IrLocal? slot, IrRvalue creation, State state)
    {
        if (slot is null)
            return;
        if (slot.Kind != IrLocalKind.Slot)
            Error($"'{N(slot)}' isn't a slot");
        Use(slot, state, reads: false);
        if (declarations.TryGetValue(slot, out var declaration) && declaration.LoopDepth != loopDepth)
            Error($"the object in slot '{N(slot)}' is made inside a loop or lambda nested in the slot's block, so it may be made more than once");
        if (slotObjects.TryGetValue(slot, out var other) && other != creation)
            Error($"slot '{N(slot)}' is given more than one object");
        slotObjects[slot] = creation;
    }

    /// <summary>A non-escaping lambda's body runs where it is called, during the code that made it, so it sees that code's locals.</summary>
    void Lambda(IrFunction lambda, State state)
    {
        if (!verifiedLambdas.Add(lambda))
            Error($"lambda '{lambda.Name}' is made twice");
        var (savedFunction, savedLoops, savedCurrent, savedMoves) = (function, loops, current, pendingMoves.ToList());
        function = lambda;
        loops = [];
        // It may run any number of times, as a loop's body does.
        loopDepth++;
        scopes.Add([]);
        var inner = state.Clone();
        foreach (var parameter in Entry(lambda))
            DeclareParameter(parameter, inner);
        inner = Block(lambda.Body, inner, newScope: false);
        if (inner.Reachable && lambda.Symbol.ReturnType != VplType.Void)
            Report(lambda, null, "the end of the lambda is reachable, but it returns a value");
        scopes.RemoveAt(scopes.Count - 1);
        loopDepth--;
        (function, loops, current) = (savedFunction, savedLoops, savedCurrent);
        pendingMoves.Clear();
        pendingMoves.AddRange(savedMoves);
    }

    /// <summary>An escaping lambda's body sees only its parameters and the copies its closure holds.</summary>
    void Closure(IrFunction lambda)
    {
        if (!verifiedLambdas.Add(lambda))
            Error($"lambda '{lambda.Name}' is made twice");
        var saved = (function, loops, scopes, loopDepth, current, moves: pendingMoves.ToList());
        VerifyFunction(lambda, root);
        (function, loops, scopes, loopDepth, current, _) = saved;
        pendingMoves.Clear();
        pendingMoves.AddRange(saved.moves);
    }
}
