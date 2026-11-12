using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;

namespace Wyl.Compiler.Interpret;

public sealed partial class Interpreter
{
    // Raw memory, as runtime/wyl/intrinsics.h implements it: storage a pointer walks, one cell per
    // element, empty until a value is initialized in it. These are unsafe in Wylow, so using a
    // pointer outside its storage, or after freeing it, is the program's undefined behavior.

    /// <summary>
    /// Storage of the program's own size, paid from the step budget, so that a count no run could
    /// afford is a budget overrun rather than the host's out-of-memory. Every later path over the
    /// storage is then bounded by its length, which the budget has already paid for.
    /// </summary>
    Buffer AllocateBuffer(int count)
    {
        Charge(count);
        return Buffer.Allocate(count);
    }

    /// <summary>The element type of a generic method's <c>T*</c> parameter, with the caller's and the method's type arguments applied.</summary>
    static WylType PointedType(IrCall call, Frame caller, int parameter) =>
        ((PointerType)caller.Map.Compose(call.Method.Map).Apply(call.Method.Symbol.Parameters[parameter].Type)).Element;

    Value MemoryAllocate(IReadOnlyList<Value> arguments, string? location)
    {
        int count = (int)(long)Integer(arguments[0]);
        if (count < 0)
            throw new WylPanic("cannot allocate a negative number of elements", location ?? "<unknown>");
        // The runtime's size check can't trigger for a 32-bit count on a 64-bit target: no
        // type is 2^33 bytes, so SIZE_MAX / sizeof(T) always exceeds the largest count.
        return new PointerValue(AllocateBuffer(count), 0);
    }

    static Value? MemoryFree(IReadOnlyList<Value> arguments)
    {
        switch (arguments[0])
        {
            case PointerValue storage:
                storage.Buffer.Free();
                break;
            // As C++, which frees a null pointer as a no-op.
            case NullValue:
                break;
            default:
                throw new InterpreterException("memory::free was given something that is not a pointer");
        }
        return null;
    }

    Value? MemoryInitialize(IrCall call, Frame caller)
    {
        var arguments = ArgumentsOf(call, caller);
        var place = PointerAt(arguments[0]);
        place.Buffer.Write(place.Index, new Cell(PointedType(call, caller, 0), arguments[1]));
        return null;
    }

    Value? MemoryInitializeDefault(IrCall call, Frame caller)
    {
        var arguments = ArgumentsOf(call, caller);
        var data = PointerAt(arguments[0]);
        int count = (int)(long)Integer(arguments[1]);
        var element = PointedType(call, caller, 0);
        for (int i = 0; i < count; i++)
            data.Buffer.Write(data.Index + i, new Cell(element, DefaultValue(element, caller)));
        return null;
    }

    Value? MemoryDrop(IReadOnlyList<Value> arguments)
    {
        var place = PointerAt(arguments[0]);
        var cell = place.Buffer.Read(place.Index) ?? throw new UndefinedBehavior("dropped storage that was never initialized");
        place.Buffer.Write(place.Index, null);
        Release(cell);
        return null;
    }

    static Value MemoryMoveOut(IReadOnlyList<Value> arguments)
    {
        var place = PointerAt(arguments[0]);
        var cell = place.Buffer.Read(place.Index) ?? throw new UndefinedBehavior("moved storage that was never initialized");
        place.Buffer.Write(place.Index, null);
        return cell.Value;
    }

    static Value? MemoryMoveTo(IReadOnlyList<Value> arguments)
    {
        int count = (int)(long)Integer(arguments[2]);
        // Moving nothing touches no storage, so a list that has allocated none moves from null.
        if (count == 0)
            return null;
        MoveTo(PointerAt(arguments[0]), PointerAt(arguments[1]), count);
        return null;
    }

    /// <summary><c>memory::collectCycles</c>, with or without a limit on the objects it visits.</summary>
    Value? CollectCycles(IrCall call, Frame caller, string location)
    {
        var given = call.Arguments.Where(a => a is not IrFieldDefault).ToList();
        if (given.Count == 0)
        {
            Collect(long.MaxValue, location);
            return null;
        }
        var visits = Integer(ArgumentValue(given[0], caller));
        if (visits < 0)
            throw new WylPanic("cannot visit a negative number of objects", location);
        Collect((long)visits, location);
        return null;
    }

    /// <summary><c>memory::cycleStats</c>, which stores the counts in its <c>ref</c> arguments.</summary>
    Value? TakeCycleStats(IrCall call, Frame caller)
    {
        var given = call.Arguments.Where(a => a is not IrFieldDefault).ToList();
        var stats = TakeStats();
        var values = new[] { stats.Buffered, stats.Visited, stats.Freed };
        for (int i = 0; i < values.Length; i++)
        {
            var cell = given[i] switch
            {
                IrRefArgument reference => PlaceCell(reference.Place, caller),
                IrAddrArgument address => PlaceCell(address.Place, caller),
                _ => throw new InterpreterException("memory::cycleStats was given a value, not a ref"),
            };
            cell.Value = new IntValue(values[i] < int.MaxValue ? values[i] : int.MaxValue);
        }
        return null;
    }

    static Value MemoryCompare(IReadOnlyList<Value> arguments) =>
        new IntValue(RuntimeText.CompareBytes(SpanBytes(arguments[0]), SpanBytes(arguments[1])));

    static Value MemoryEqual(IReadOnlyList<Value> arguments) =>
        new BoolValue(SpanBytes(arguments[0]).AsSpan().SequenceEqual(SpanBytes(arguments[1])));

    /// <summary>
    /// The bytes that a <c>ReadOnlySpan&lt;byte&gt;</c> views. An empty span reads no storage, because
    /// its pointer may be null.
    /// </summary>
    static byte[] SpanBytes(Value value)
    {
        var span = (StructValue)value;
        Value Field(string name) => span.Fields[span.Type.Symbol!.Fields.FindIndex(f => f.Name == name)].Value;
        int length = (int)(long)Integer(Field("size"));
        if (length == 0)
            return [];
        var data = PointerAt(Field("elements"));
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            var cell = data.Buffer.Read(data.Index + i) ?? throw new UndefinedBehavior("a span viewed storage that was never initialized");
            bytes[i] = (byte)Integer(cell.Value);
        }
        return bytes;
    }

    static PointerValue PointerAt(Value value) => value switch
    {
        PointerValue pointer => pointer,
        NullValue => throw new UndefinedBehavior("raw memory was used through a null pointer"),
        var other => throw new InterpreterException($"raw memory was used through a {other.GetType().Name}"),
    };

    /// <summary>
    /// Moves <paramref name="count"/> values into uninitialized storage, leaving the source
    /// uninitialized. The ranges may overlap: as the runtime does, it moves forwards when the
    /// destination is below the source and backwards when it is above.
    /// </summary>
    static void MoveTo(PointerValue from, PointerValue to, int count)
    {
        if (ReferenceEquals(from.Buffer, to.Buffer) && to.Index > from.Index)
        {
            for (int i = count - 1; i >= 0; i--)
                MoveOne(from.Buffer, from.Index + i, to.Buffer, to.Index + i);
        }
        else
        {
            for (int i = 0; i < count; i++)
                MoveOne(from.Buffer, from.Index + i, to.Buffer, to.Index + i);
        }
    }

    static void MoveOne(Buffer from, int fromIndex, Buffer to, int toIndex)
    {
        var cell = from.Read(fromIndex) ?? throw new UndefinedBehavior("moved storage that was never initialized");
        from.Write(fromIndex, null);
        to.Write(toIndex, cell);
    }
}
