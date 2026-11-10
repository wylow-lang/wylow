using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Analysis;

public sealed partial class IrVerifier
{
    /// <summary>What a reference, view, pointer, or borrowed local refers to, which its validity depends on.</summary>
    readonly Dictionary<IrLocal, Dependency> dependencies = [];
    readonly HashSet<IrLocal> borrowingHolds = [];
    readonly Dictionary<IrLocal, IrRvalue> slotObjects = [];

    /// <summary>The place each hold holds, so that a move through one names the source local it came from.</summary>
    readonly Dictionary<IrLocal, IrPlace> holdPlaces = [];

    /// <summary>For an in-use guard, the local whose value it marks.</summary>
    readonly Dictionary<IrLocal, IrLocal> guardTargets = [];
    readonly List<IrLocal> pendingMoves = [];

    /// <summary>What each temporary, or <c>is ref</c> name, holds a copy of the value of, until either is given another.</summary>
    readonly Dictionary<IrLocal, IrLocal> copies = [];

    /// <summary>The reference each <c>test.not_null</c> temporary tested, and the references known to be null where they are.</summary>
    readonly Dictionary<IrLocal, IrLocal> nullTests = [];
    readonly HashSet<IrLocal> knownNull = [];

    void Use(IrLocal local, State state, bool reads)
    {
        if (!Visible(local))
        {
            Error(declarations.ContainsKey(local) ? $"'{N(local)}' is used outside its scope" : $"'{N(local)}' isn't declared");
            return;
        }
        if (reads && !state.Assigned.Contains(local))
        {
            if (SourceOf(local) is { } symbol)
            {
                if (reportedUnassigned.Add(symbol))
                    Violation(l => Catalog.MayBeUnassigned(l, symbol.Name));
            }
            else
                Error($"'{N(local)}' may be read before it is stored");
        }
        if (reads)
        {
            // A move earlier in the same instruction is not in the state yet. A temporary's second
            // move reports itself as a compiler invariant, in <see cref="Operand"/>.
            bool movedEarlier = state.Moved.TryGetValue(local, out var movedAt);
            bool movedInInstruction = pendingMoves.Contains(local) && SourceOf(local) is not null;
            if (movedEarlier || movedInInstruction)
            {
                if (SourceOf(local) is { } moved)
                {
                    if (reportedMoved.Add(moved))
                        Violation(l => Catalog.UseAfterMove(l, moved.Name, movedEarlier ? movedAt : current?.Origin));
                }
                else
                    Error($"'{N(local)}' is used after it is moved");
            }
        }
        if (state.Ended.Contains(local))
            Error($"'{N(local)}' is used after it ends");
        if (state.Invalid.TryGetValue(local, out var reason))
            Error($"'{N(local)}' is used where it may no longer be valid: {reason}");
    }

    /// <param name="initializes">The place is a field a constructor must assign, which a store is assigning.</param>
    void Place(IrPlace place, State state, bool reads, bool initializes = false)
    {
        var local = place.Local;
        Use(local, state, reads || place.IsDeref);
        CheckNullableOrder(place);
        if (local == constructed && !initializes)
            CheckConstructed(place, state);
        if (place.IsDeref)
        {
            if (local.Deref is null)
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
            if (projection.Type == WylType.Error)
                Error($"a projection doesn't apply to a '{holder}'");
            holder = projection.Type;
        }
    }

    void Operand(IrOperand operand, State state)
    {
        if (operand is not IrRead read)
            return;
        Place(read.Place, state, reads: true);
        var local = read.Place.Local;
        // No source copies a value that is not copyable: lowering moves every one of them. A
        // reference local's whole value is the reference itself, whose type is what it refers to.
        bool copiesReference = local.Kind == IrLocalKind.Reference && !read.Place.IsDeref && read.Place.Projections.Count == 0;
        if (read.Ownership == IrOwnership.Copy && !read.Type.IsCopyableIn(function.Symbol) && !copiesReference)
            Error($"a '{read.Type}' is copied, which is not copyable");
        // A lambda may run more than once, so it never moves what it captures (spec functions.lambdas.captures).
        if (read.Ownership == IrOwnership.Move && capturedOutside.Contains(local) && SourceOf(local) is { } captured)
        {
            if (reportedCaptureMoves.Add(captured))
                Violation(l => Catalog.LambdaMovesCapture(l, captured.Name));
            return;
        }
        if (read.Ownership != IrOwnership.Move)
            return;
        if (read.Place.IsDeref || local.Kind != IrLocalKind.Value || local.IsThis || (!local.IsTemporary && read.Place.Projections.Any(p => p is not IrAssumed)))
        {
            if (MovedPart(read.Place) is { } part)
            {
                if (part == "out of 'this'" && function.Symbol.IsDestructor)
                    Violation(l => Catalog.MoveOutOfThisInDestructor(l));
                else
                    Violation(l => Catalog.MoveOutOfPlace(l, part, read.Type.ToString()));
            }
            else if (diagnostics is not null && read.Place.IsDeref)
            {
                var what = read.Place.Local.Deref switch
                {
                    IrDeref.Pointee => "out of what a pointer refers to",
                    IrDeref.Generic => "out of a held value",
                    _ => "out of what a reference refers to",
                };
                Violation(l => Catalog.MoveOutOfPlace(l, what, read.Type.ToString()));
            }
            else
                Error($"only a local's whole value can be moved, not '{N(local)}''s");
        }
        // A pattern cannot move a field out of a value whose type has a destructor: the rest of the
        // value would be one the destructor cannot describe (spec structs.unique.destructors).
        else if (MovedFromDestructorValue(read.Place) is { } holder)
        {
            Violation(l => Catalog.MoveOutOfDestructorValue(l, holder.ToString()));
        }
        else if (pendingMoves.Contains(local))
        {
            if (SourceOf(local) is { } symbol)
            {
                if (reportedMoved.Add(symbol))
                    Violation(l => Catalog.UseAfterMove(l, symbol.Name, current?.Origin));
            }
            else
                Error($"'{N(local)}' is moved twice");
        }
        else
            pendingMoves.Add(local);
    }

    /// <summary>
    /// The type of a value a move takes a field out of, when that type declares a destructor, or
    /// null. Only a temporary's field reaches here: another place is reported where it is found.
    /// </summary>
    static WylType? MovedFromDestructorValue(IrPlace place)
    {
        if (place.IsDeref)
            return null;
        var holder = place.BaseType;
        foreach (var projection in place.Projections)
        {
            if (projection is IrField && holder.NonNull.Symbol is { Destructor: not null })
                return holder.NonNull;
            holder = projection.Type;
        }
        return null;
    }

    /// <summary>
    /// What a move would take from a place that is not a whole local, as a message names it, or null
    /// where the place is one the compiler made and the source doesn't name.
    /// </summary>
    string? MovedPart(IrPlace place)
    {
        if (place.Local.IsThis || HeldThis(place.Local))
            return "out of 'this'";
        if (SourceOf(place) is not { } source)
            return null;
        var name = source.Name;
        // A field an object holds is reached by dereferencing it, and is still a field of its source.
        if (place.IsDeref)
            return place.Projections.Count > 0 && place.Projections[0] is IrField
                ? $"a field out of '{name}'"
                : $"out of what '{name}' refers to";
        return place.Projections.Count > 0 && place.Projections[0] is IrElement
            ? $"an element out of '{name}'"
            : $"a field out of '{name}'";
    }

    /// <summary>The source local a place's local is, or the one the place a hold holds came from.</summary>
    LocalSymbol? SourceOf(IrPlace place)
    {
        var local = place.Local;
        for (int depth = 0; depth < 8; depth++)
        {
            if (SourceOf(local) is { } symbol)
                return symbol;
            if (!holdPlaces.TryGetValue(local, out var held))
                return null;
            local = held.Local;
        }
        return null;
    }

    /// <summary>Whether a hold eventually holds <c>this</c>, which a move out of names as such.</summary>
    bool HeldThis(IrLocal local)
    {
        for (int depth = 0; depth < 8 && holdPlaces.TryGetValue(local, out var held); depth++)
        {
            if (held.Local.IsThis)
                return true;
            local = held.Local;
        }
        return false;
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
                break;
            case IrBinary binary:
                Operand(binary.Left, state);
                Operand(binary.Right, state);
                CheckLoc(binary.Loc);
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
                // A field left out fits only a memberwise constructor's parameter for that field.
                Arguments(construction.Constructor, construction.Arguments, state);
                CheckLoc(construction.Loc);
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
                if (array.Type is not FixedArrayType fixedArray || array.Elements.Count != fixedArray.Length || array.Elements.Any(e => e.Type != fixedArray.Element))
                    Error($"the elements don't fit a '{array.Type}'");
                break;
            case IrDefault:
                break;
            case IrZeroed:
                if (defined != function.Struct || (function.Builds is null && !function.Symbol.IsConstructor))
                    Error("only a struct being built starts as zeroed storage");
                break;
            case IrClosure closure:
            {
                IrLocal? capturedThis = null;
                var making = constructed;
                for (int i = 0; i < closure.Captures.Count && i < closure.Function.Captures.Count; i++)
                {
                    // What the source writes is used where the lambda's body uses it.
                    captureOrigin = FirstUse(closure.Function, closure.Function.Captures[i]);
                    // What a constructor makes is used where the body uses its copy.
                    bool copiesMade = closure.Captures[i] is IrRead { Place: { IsDeref: false, Projections.Count: 0 } place } && place.Local == making && making is not null;
                    if (copiesMade)
                        (capturedThis, constructed) = (closure.Function.Captures[i], null);
                    Operand(closure.Captures[i], state);
                    constructed = making;
                }
                captureOrigin = null;
                if (!closure.Function.IsEscaping)
                    Error("a closure is made from an escaping lambda");
                if (closure.Captures.Count != closure.Function.Captures.Count
                    || closure.Captures.Where((c, i) => c.Type != closure.Function.Captures[i].Type).Any())
                    Error("the captures don't fit the lambda's");
                Slot(closure.Slot, value, state);
                Closure(closure.Function, state, capturedThis);
                break;
            }
            case IrLambda lambda:
                if (lambda.Function.IsEscaping)
                    Error("an escaping lambda is made by a closure");
                Lambda(lambda.Function, state);
                foreach (var assigned in writes.Lambdas.Assigns(lambda.Function))
                    state.Unnarrow(assigned);
                state.Lambdas.Add(lambda.Function);
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
                    IrTestKind.NotNull => type.IsNullableObject || type.IsPointer,
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
                if (!hold.Place.Type.MayShareStorage)
                    Error($"a hold holds an object or a string, not a '{hold.Place.Type}'");
                break;
            case IrInUse inUse:
                Place(inUse.Place, state, reads: true);
                break;
            default:
                throw new InvalidOperationException($"unexpected IR rvalue {value.GetType().Name}");
        }

        var effects = IrEffects.Of(value);
        if (value is IrCall or IrInvoke or IrNew or IrConstruct)
            CheckInUse(value, effects, state);
        CheckMovedReferences(value);
        Apply(state, effects, Describe(value));
        CommitMoves(state);
    }

    /// <summary>
    /// A reference taken by an earlier operand refers into the value at the call, so moving the
    /// value in a later operand would leave it referring to nothing.
    /// </summary>
    void CheckMovedReferences(IrRvalue value)
    {
        if (pendingMoves.Count == 0)
            return;
        foreach (var argument in IrWalk.Operands(value))
        {
            var (place, kind) = argument switch
            {
                IrRefArgument reference => (reference.Place, reference.Kind),
                IrAddrArgument address => (address.Place, address.Kind),
                _ => (null, RefKind.None),
            };
            if (place is not { IsDeref: false, Local: var local } || !pendingMoves.Contains(local) || SourceOf(local) is not { } symbol)
                continue;
            if (reportedMoved.Add(symbol))
                Violation(l => Catalog.MoveWhileUsed(l, symbol.Name, FunctionSymbol.RefPrefix(kind).Trim()));
        }
    }

    /// <summary>A temporary moved by an instruction is gone once the instruction is done.</summary>
    void CommitMoves(State state)
    {
        foreach (var moved in pendingMoves)
        {
            state.Moved[moved] = current?.Origin;
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
        // A by-value receiver is passed as the first argument, so the call has no receiver of its own.
        bool takesReceiver = method.Symbol.IsInstanceMethod && !method.Symbol.HasByValueReceiver;
        if (takesReceiver != call.Receiver is not null)
            Error(call.Receiver is null ? $"calls method '{method.Symbol.Name}' without a receiver" : $"calls '{method.Symbol.Name}' with a receiver");
        if (call.Receiver is { } receiver)
        {
            Argument(receiver, state);
            // What it returns points into what the receiver holds, which must outlive the call.
            bool returnsInto = method.Symbol.ReturnRefKind != RefKind.None || call.Type.IsRefStruct;
            if (returnsInto && receiver is IrRead read && !(read.Type.MayShareStorage && read.Place is { IsDeref: false, Projections.Count: 0 }))
                Error("a reference or view a method returns points into its receiver: an object or string in a local, or a struct passed as a place");
        }
        else if (method.Symbol.ReturnRefKind != RefKind.None)
            Error("only a method returns a reference, into its receiver");
        if (method.Symbol.ReturnsHold != call.Holder is not null)
            Error(call.Holder is null ? $"calls '{method.Symbol.Name}', which returns a hold, without a holder" : $"gives a holder to '{method.Symbol.Name}', which returns no hold");
        if (call.Holder is { } holder)
        {
            if (holder.Kind != IrLocalKind.Holder)
                Error($"'{N(holder)}' is given as a holder, but is a {holder.Kind.ToString().ToLowerInvariant()} local");
            else if (!Visible(holder))
                Error($"'{N(holder)}' is used outside its scope");
            state.Assigned.Add(holder);
        }
        Arguments(method, call.Arguments, state);
        if (function.ChainsWith(call))
            state.AssignedFields.UnionWith(requiredFields);
        CheckLoc(call.Loc);
        if (method.Symbol.PassesLocation != call.Loc is not null)
            Error(call.Loc is null ? $"'{method.Symbol.Name}' is given its caller's location, which the call doesn't pass" : $"passes a location to '{method.Symbol.Name}', which takes none");
    }

    /// <summary>A function value a non-escaping function parameter takes, which the call converts to the parameter's type.</summary>
    static bool PassesFunction(WylType value, WylType parameter) =>
        parameter.NonNull is FunctionType target && !target.IsEscaping && value.NonNull is FunctionType function
        && Conversions.IsFunctionConversion(function, target) && (parameter.IsNullable || !value.IsNullable);

    void Arguments(MethodReference method, IReadOnlyList<IrArgument> arguments, State state)
    {
        var parameters = method.Symbol.Parameters;
        int count = parameters.Count + method.ReceiverCount;
        if (arguments.Count != count)
        {
            Error($"passes {arguments.Count} arguments to '{method.Symbol.Name}', which takes {count}");
            return;
        }
        for (int i = 0; i < arguments.Count; i++)
        {
            Argument(arguments[i], state);
            var type = method.ArgumentType(i);
            var kind = method.ArgumentRefKind(i);
            bool fits = arguments[i] switch
            {
                IrAddrArgument address => type is NullableReferenceType nullable && address.Place.Type == nullable.Referenced && (address.Kind == nullable.Kind || nullable.Kind == RefKind.In),
                IrRefArgument reference => !type.IsNullableReference && reference.Place.Type == type && (reference.Kind == kind || kind == RefKind.In),
                IrOperand operand => operand.Type == type || (type is NullableReferenceType nullable && operand.Type is NullableReferenceType passed && passed.Referenced == nullable.Referenced) || PassesFunction(operand.Type, type),
                IrFieldDefault field => i >= method.ReceiverCount && parameters[i - method.ReceiverCount].Field == field.Field,
                _ => false,
            };
            if (fits && arguments[i] is IrOperand && kind != RefKind.None && !type.IsNullableReference)
                fits = false;
            if (!fits)
                Error(i < method.ReceiverCount
                    ? $"the receiver doesn't fit '{method.ReceiverType}'"
                    : $"argument {i + 1 - method.ReceiverCount} doesn't fit parameter '{FunctionSymbol.ParameterText(kind, type)} {parameters[i - method.ReceiverCount].Name}'");
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
        foreach (var argument in IrWalk.Passed(value)?.All ?? [])
        {
            var (place, alias) = argument switch
            {
                IrRefArgument reference => (reference.Place, false),
                IrAddrArgument address => (address.Place, false),
                IrRead { Ownership: IrOwnership.Borrow } read => (read.Place, true),
                _ => (null, false),
            };
            if (place is not null && WouldBreak(place, alias, effects, state) is { } reason)
            {
                if (!ReportNullableValue(place, reason, effects, state))
                    Error($"'{IrPrinter.Print(root, place)}' is used during {Describe(value)}, but {reason}");
            }
            // A lambda it is given runs during it, using what the lambda refers into.
            if (argument is IrRead { Place: { IsDeref: false, Projections.Count: 0, Local: { Type.NonNull.IsNonEscapingFunction: true } lambda } }
                && dependencies.ContainsKey(lambda) && !state.Invalid.ContainsKey(lambda))
            {
                var trial = state.Clone();
                Apply(trial, effects, "it");
                if (trial.Invalid.TryGetValue(lambda, out var broken))
                    Error($"'{N(lambda)}' runs during {Describe(value)}, but {broken}");
            }
        }
    }

    string? WouldBreak(IrPlace place, bool alias, IrEffects effects, State state)
    {
        var writes = effects.Writes.Select(w => Target(w.Place)).ToList();
        if (Breaks(Into(place, alias), effects, writes, state) is { } reason)
            return reason;
        var local = place.Local;
        if (place.IsDeref && effects.Writes.Any(w => !w.Place.IsDeref && w.Place.Projections.Count == 0 && w.Place.Local == local))
            return $"it may reassign '{N(local)}'";
        if (place.IsDeref && effects.RunsCode && IsChangedByLambda(local, state))
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
}
