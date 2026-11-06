using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;

namespace Wyl.Compiler.Interpret;

// Calls into lowered code, making objects, and destroying them: what a function does to the
// objects and frames around it, as runtime/wyl/object.h does.

public sealed partial class Interpreter
{
    // ---------------------------------------------------------------------------------------------
    // Calls
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs a call. A method, constructor, or extern is found from the IR's one index, and a
    /// dispatched call's implementation from the object's class, or the class being constructed or
    /// destroyed, as C++ dispatches.
    /// </summary>
    Value? Call(IrCall call, Frame caller)
    {
        var method = call.Method;
        if (method.Symbol.IsBuiltin)
            return CallIntrinsic(call, caller);
        // A base or chained constructor call has no receiver in the IR: it runs on the object being made.
        if (call.IsSuperCall && method.Symbol.IsConstructor)
            return BaseConstruct(call, caller);

        var storage = call.Receiver is { } receiver ? ReceiverStorage(receiver, caller) : null;
        var obj = (storage as OwnedStorage)?.Cell.Value as ObjectRef;
        var callee = Resolve(call, storage, obj);
        // A primitive's or String's implementation is an extern member of the standard library.
        if (callee.Symbol.IsBuiltin)
            return CallIntrinsic(call, caller, callee.Symbol);
        if (callee.Symbol.IsAbstract)
            throw new WylPanic(callee.Symbol.AbstractCallMessage, callee.Symbol.Declaration!.NameLocation.PanicText);
        var function = program.Functions.Single(callee.Symbol)
            ?? throw new UnsupportedFeature($"no lowered function for '{callee.Symbol.Signature}'");
        var frame = CalleeFrame(caller, callee.Map);
        if (function.HasLocationParameter)
            frame.CallerLocation = Location(call.Loc, caller);
        if (function.This is not null)
            frame.Locals[function.This] = storage ?? throw new InterpreterException($"no receiver for '{function.Name}'");
        BindArguments(call.Arguments, function.Parameters, frame, caller);
        return Invoke(function, frame);
    }

    /// <summary>
    /// Which method a call runs: the one named, or for a dispatched call the receiver's
    /// implementation. An object dispatches as its current class, and a struct as its own.
    /// </summary>
    MethodReference Resolve(IrCall call, Storage? storage, ObjectRef? obj)
    {
        var method = call.Method;
        if (call.IsSuperCall || !method.Symbol.IsDispatched)
            return method;
        // A struct value carries the type it was made as, which a type parameter's substitution
        // gives to the call; a reference dispatches on its object's current class.
        WylType? runtime = obj?.Active ?? storage switch
        {
            OwnedStorage { Cell.Value: StructValue value } => value.Type,
            OwnedStorage owned => owned.Cell.Type,
            RefStorage { Cell: { } cell } => cell.Type,
            _ => null,
        };
        if (runtime is null || runtime.ContainsTypeParameters)
            return method;
        return callees.Overrides.Implementation(method, runtime.NonNull) ?? method;
    }

    /// <summary>A method's <c>this</c>: a reference to the caller's place for a struct, the object for a class.</summary>
    Storage ReceiverStorage(IrArgument receiver, Frame caller) => receiver switch
    {
        IrRefArgument reference => new RefStorage(PlaceCell(reference.Place, caller)),
        IrAddrArgument address => new RefStorage(PlaceCell(address.Place, caller)),
        // The storage's type, not the read's, which a type parameter's substitution made concrete.
        // `this` is never owned: a class's is a borrowed handle, a struct's the caller's cell.
        IrRead read => new OwnedStorage(new Cell(PlaceCell(read.Place, caller).Type, ReadInPlace(read, caller))),
        _ => throw new InterpreterException($"unexpected receiver {receiver.GetType().Name}"),
    };

    /// <summary>Runs a base class constructor on the object the calling constructor is making.</summary>
    Value? BaseConstruct(IrCall call, Frame caller)
    {
        var method = call.Method;
        var obj = caller.Constructing ?? throw new InterpreterException("a constructor call outside a constructor");
        var function = program.Functions.Single(method.Symbol)
            ?? throw new UnsupportedFeature($"no lowered function for '{method.Symbol.Signature}'");
        var frame = CalleeFrame(caller, method.Map);
        frame.Constructing = obj;
        if (function.HasLocationParameter)
            frame.CallerLocation = Location(call.Loc, caller);
        // The class it constructs as is the caller's instantiation of it, as for 'new'.
        var type = method.Owner is { } owner ? caller.Type(owner) : obj.Type;
        if (function.This is { } self)
            frame.Locals[self] = new OwnedStorage(new Cell(type, obj));
        BindArguments(call.Arguments, function.Parameters, frame, caller);
        return RunConstructor(function, frame, obj, type);
    }

    /// <summary>Binds a call's arguments in order, skipping the fields a memberwise form defaults.</summary>
    void BindArguments(IReadOnlyList<IrArgument> arguments, IReadOnlyList<IrLocal> parameters, Frame frame, Frame caller)
    {
        var given = arguments.Where(a => a is not IrFieldDefault).ToList();
        if (given.Count != parameters.Count)
            throw new InterpreterException($"a call gave {given.Count} arguments to {parameters.Count} parameters");
        for (int i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            var storage = given[i] switch
            {
                IrRefArgument reference => (Storage)new RefStorage(PlaceCell(reference.Place, caller)),
                IrAddrArgument address => new RefStorage(PlaceCell(address.Place, caller)),
                IrRead { Type.IsNullableReference: true } read => new RefStorage((EvalOperand(read, caller) as RefValue)?.Cell),
                IrConstant { Type.IsNullableReference: true } => new RefStorage(null),
                var operand => new OwnedStorage(ArgumentCell(parameter.Type, operand, frame, caller)),
            };
            frame.Locals[parameter] = storage;
        }
    }

    /// <summary>
    /// A by-value parameter's cell. A parameter of nullable value type may be given a value that
    /// isn't nullable, as a lambda is given to a nullable function parameter without allocating:
    /// the value becomes the nullable's value, as <c>wyl::OptionalRef</c> makes it.
    /// </summary>
    Cell ArgumentCell(WylType parameterType, IrArgument argument, Frame frame, Frame caller)
    {
        var type = frame.Type(parameterType);
        var value = EvalArgument(argument, type, caller);
        return new Cell(type, type.IsNullableValue && value is not OptionalValue ? new OptionalValue(new Cell(type.NonNull, value)) : value);
    }

    /// <summary>Runs a function's body and releases what it owns when it returns.</summary>
    Value? Invoke(IrFunction function, Frame frame)
    {
        if (++depth > options.CallDepthBudget)
            throw new BudgetExceededException();
        try
        {
            Value? result = null;
            try
            {
                ExecBlock(function.Body, frame);
            }
            catch (ReturnSignal returned)
            {
                result = returned.Value;
            }
            ReleaseParameters(function, frame);
            return result;
        }
        finally
        {
            depth--;
        }
    }

    /// <summary>
    /// Runs a class constructor on the object being made, in the order C++ constructs it: the base
    /// constructor first (whose frame runs the base's own body), then the class's field
    /// initializers and body, with dispatch on the derived class only from there on.
    /// </summary>
    Value? RunConstructor(IrFunction function, Frame frame, ObjectRef obj, WylType type)
    {
        if (++depth > options.CallDepthBudget)
            throw new BudgetExceededException();
        try
        {
            Value? result = null;
            try
            {
                int start = 0;
                if (function.ConstructorCall is { } constructorCall)
                {
                    if (function.Body.Statements.Count == 0 || !ReferenceEquals(function.Body.Statements[0], constructorCall))
                        throw new InterpreterException($"'{function.Name}' does not run its constructor call first");
                    ExecBlock(constructorCall, frame);
                    start = 1;
                }
                // The emitter's prologue: the constructor of this class marks its object's class
                // before its body, so a release during construction already knows whether to buffer
                // it. A constructor that chains has had it done by the one it called.
                if (!function.Chains)
                {
                    obj.Cyclic = IsCyclicClass(type);
                    obj.Active = type;
                }
                ExecBlock(function.Body, frame, start);
            }
            catch (ReturnSignal returned)
            {
                result = returned.Value;
            }
            ReleaseParameters(function, frame);
            return result;
        }
        finally
        {
            depth--;
        }
    }

    /// <summary>By-value parameters are released after the body's own locals, the last first.</summary>
    void ReleaseParameters(IrFunction function, Frame frame)
    {
        for (int i = function.Parameters.Count - 1; i >= 0; i--)
        {
            if (frame.Locals.TryGetValue(function.Parameters[i], out var storage) && storage is OwnedStorage owned)
                Release(owned.Cell);
        }
    }

    /// <summary>
    /// Makes a class object and runs its constructor. The <c>new</c> expression owns the object's
    /// first reference, and a stack slot it is put in holds another, as <c>wyl::StackSlot::make</c>
    /// returns a counted copy of the object it just made.
    /// </summary>
    Value New(IrNew creation, Frame caller)
    {
        var type = caller.Type(creation.Type).NonNull;
        var obj = Values.Allocate(type, program.Bound.Stdlib);
        obj.Created = objectsCreated++;
        var constructor = creation.Constructor;
        if (program.Functions.Single(constructor.Symbol) is { } function)
        {
            var frame = CalleeFrame(caller, constructor.Map);
            frame.Constructing = obj;
            if (function.HasLocationParameter)
                frame.CallerLocation = Location(creation.Loc, caller);
            if (function.This is { } self)
                frame.Locals[self] = new OwnedStorage(new Cell(type, obj));
            BindArguments(creation.Arguments, function.Parameters, frame, caller);
            RunConstructor(function, frame, obj, type);
        }
        if (creation.Slot is { } slot)
        {
            FillSlot(slot, obj, caller);
            obj.Count++;
        }
        return obj;
    }

    /// <summary>
    /// Fills a stack slot, whose object leaves the collector: nothing outside its scope refers to
    /// it, so it can be part of no unreachable cycle, and its slot's end destroys it.
    /// </summary>
    void FillSlot(IrLocal local, HeapObject heap, Frame frame)
    {
        if (frame.Find(local) is not SlotStorage slot)
            throw new InterpreterException($"'{local.Name}' is not a stack slot");
        if (!options.Release && slot.Object is not null)
            throw new WylPanic("internal compiler error: a stack slot was filled twice", "<runtime>");
        slot.Object = heap;
        heap.Cyclic = false;
        if (heap.Buffered)
            RemoveFromBuffer(heap);
    }

    void EndSlot(SlotStorage slot)
    {
        if (slot.Object is not { } heap)
            return;
        slot.Object = null;
        DestroyInSlot(heap);
    }

    /// <summary>
    /// Releases an object's last reference: holds its count at one so its destructor's temporaries
    /// cannot free it, then destroys it. <c>wyl::Object::destroy</c>.
    /// </summary>
    void DestroyHeapObject(HeapObject heap)
    {
        if (++depth > options.CallDepthBudget)
            throw new BudgetExceededException();
        try
        {
            heap.Count = 1;
            heap.ClearWeak();
            if (heap.Buffered)
                RemoveFromBuffer(heap);
            destroying++;
            try
            {
                Delete(heap);
            }
            finally
            {
                destroying--;
            }
        }
        finally
        {
            depth--;
        }
    }

    /// <summary>
    /// Destroys the object a stack slot holds, whose one reference is the slot's:
    /// <c>wyl::Object::destroy_in_slot</c>.
    /// </summary>
    void DestroyInSlot(HeapObject heap)
    {
        if (++depth > options.CallDepthBudget)
            throw new BudgetExceededException();
        try
        {
            if (!options.Release && heap.Count != 1)
                throw new WylPanic("internal compiler error: an object on the stack outlived its scope", "<runtime>");
            heap.ClearWeak();
            if (heap.Buffered)
                RemoveFromBuffer(heap);
            destroying++;
            try
            {
                Delete(heap);
            }
            finally
            {
                destroying--;
            }
        }
        finally
        {
            depth--;
        }
    }

    /// <summary>
    /// <c>~Object</c>: runs what the object owns, then panics if a reference to it survived. An
    /// object the collector finalized already ran its destructors, so only its fields are released.
    /// </summary>
    void Delete(HeapObject heap)
    {
        switch (heap)
        {
            case ObjectRef obj:
                foreach (var type in obj.Chain.ToList())
                {
                    obj.Active = type;
                    var symbol = type.Symbol!;
                    // The collector already ran the destructors it finalizes; a class whose
                    // destructor releases its elements with its fields has none run early.
                    bool finalizedHere = obj.Finalized && symbol.HasSeparateDestructor;
                    if (!finalizedHere && symbol.Destructor is { } destructor && program.Functions.Single(destructor) is { } function)
                    {
                        var frame = new Frame(TypeMap.For(type));
                        if (function.This is { } self)
                            frame.Locals[self] = new OwnedStorage(new Cell(type, obj));
                        Invoke(function, frame);
                    }
                    var cells = obj.Fields[symbol];
                    for (int i = cells.Length - 1; i >= 0; i--)
                        Release(cells[i]);
                }
                break;
            case ClosureValue closure:
                Values.ReleaseHeld(closure.Captures, this);
                break;
            default:
                throw new InterpreterException($"an object of {heap.GetType().Name}");
        }
        if (heap.Count != 1)
            throw new WylPanic("an object's destructor kept a reference to the object", "<destructor>");
        heap.Count = 0;
    }

    /// <summary>
    /// A unique struct value is released: its destructor's body runs, unless a move or a
    /// <c>discard</c> took the value away, and then its fields are released by the caller.
    /// </summary>
    public void ReleasingStruct(StructValue value)
    {
        if (!value.Engaged)
            return;
        value.Engaged = false;
        if (value.Type.Symbol is { Destructor: { } destructor }
            && program.Functions.Single(destructor) is { } function)
        {
            var frame = new Frame(TypeMap.For(value.Type));
            if (function.This is { } self)
                frame.Locals[self] = new OwnedStorage(new Cell(value.Type, value));
            Invoke(function, frame);
        }
    }

    /// <summary>
    /// Whether objects of the class may be on a cycle: what the analysis found, or, for an
    /// instantiation only C++ sees, what <c>wyl::IS_CYCLIC</c> defaults to, which is true.
    /// </summary>
    bool IsCyclicClass(WylType type)
    {
        if (type.Symbol is { IsShared: true })
            return false;
        type = type.CppIdentity;
        if (type.ContainsTypeParameters)
            return true;
        if (type.Symbol is { IsGeneric: true } && !callees.Cycles.Classes.Contains(type))
            return true;
        return callees.Cycles.IsCyclic(type);
    }

    /// <summary>The compiler's calls around a borrow of a <c>Borrowable</c> object, which its own class implements.</summary>
    void BeginBorrow(ObjectRef obj) => BorrowHook(obj, "beginBorrow");

    void EndBorrow(ObjectRef obj) => BorrowHook(obj, "endBorrow");

    void BorrowHook(ObjectRef obj, string name)
    {
        if (!program.Bound.IsBorrowable(obj.Type) || program.Bound.Stdlib.Borrowable is not { } borrowable)
            return;
        if (!borrowable.Methods.TryGetValue(name, out var methods) || methods.FirstOrDefault() is not { } declared)
            throw new InterpreterException($"the standard library's Borrowable declares no '{name}'");
        var implementation = callees.Overrides.Implementation(new MethodReference(declared, borrowable.Type, []), obj.Active)
            ?? throw new InterpreterException($"'{obj.Type}' implements Borrowable without '{name}'");
        var function = program.Functions.Single(implementation.Symbol)
            ?? throw new UnsupportedFeature($"no lowered function for '{implementation.Symbol.Signature}'");
        var frame = new Frame(implementation.Map);
        if (function.This is { } self)
            frame.Locals[self] = new OwnedStorage(new Cell(implementation.Owner ?? obj.Type, obj));
        Invoke(function, frame);
    }

    /// <summary>Makes the type's default value: its <c>make_default</c> where it has code, and zeroed storage otherwise.</summary>
    Value DefaultValue(WylType type, Frame frame)
    {
        type = frame.Type(type);
        if (program.Bound.Stdlib.IsWeak(type))
            return new WeakValue(null, null);
        if (type.IsNullableValue)
            return new OptionalValue(null);
        // Each element's default is made by its own type's rule, which may run its initializers.
        if (type is FixedArrayType array)
            return new ArrayValue(type, [.. Enumerable.Range(0, array.Length).Select(_ => new Cell(array.Element, DefaultValue(array.Element, frame)))]);
        if (type.IsStruct && type.HasDefaultCode)
        {
            var build = program.Functions.Default(type.Symbol!) ?? throw new InterpreterException($"no make_default for '{type}'");
            var frameFor = CalleeFrame(frame, TypeMap.For(type));
            var value = Invoke(build, frameFor) ?? throw new InterpreterException($"'{build.Name}' returned nothing");
            // A value of a type without a default value is storage a constructor starts from: its
            // initializers have run, but the value is not one yet.
            if (value is StructValue made)
                made.Engaged = type.IsDefaultable;
            return value;
        }
        if (type.IsEnum && type.Symbol!.Variants.Count > 0)
            return new EnumValue(type, type.Symbol.Variants[0], []);
        return Values.Zeroed(type, program.Bound.Stdlib, engaged: type.IsDefaultable);
    }

    /// <summary>Makes a struct value with one of its constructors, including its implicit and memberwise forms.</summary>
    Value Construct(IrConstruct construction, Frame caller)
    {
        var type = caller.Type(construction.Type);
        var method = construction.Constructor;
        if (method.Symbol.IsImplicit)
            return DefaultValue(type, caller);
        var arguments = construction.Arguments;
        if (method.Symbol.IsMemberwise)
        {
            var form = arguments.Select(a => a is not IrFieldDefault).ToList();
            var build = program.Functions.Memberwise(method.Symbol, form)
                ?? throw new InterpreterException($"no memberwise form of '{method.Symbol.Name}'");
            var frame = CalleeFrame(caller, TypeMap.For(type));
            BindArguments(arguments, build.Parameters, frame, caller);
            return Engaged(Invoke(build, frame) ?? throw new InterpreterException($"'{build.Name}' returned nothing"));
        }
        var function = program.Functions.Single(method.Symbol) ?? throw new UnsupportedFeature($"no lowered function for '{method.Symbol.Signature}'");
        var body = CalleeFrame(caller, method.Map);
        if (function.HasLocationParameter)
            body.CallerLocation = Location(construction.Loc, caller);
        BindArguments(arguments, function.Parameters, body, caller);
        return Engaged(Invoke(function, body) ?? throw new InterpreterException($"constructor '{function.Name}' returned nothing"));
    }

    /// <summary>The value a constructor made: its fields are set, so it holds a value now.</summary>
    static Value Engaged(Value value)
    {
        if (value is StructValue structure)
            structure.Engaged = true;
        return value;
    }
}
