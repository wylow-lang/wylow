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

    Value? MemoryIntrinsic(string name, IrCall call, Frame caller, string? location)
    {
        // The collector returns nothing and takes no owned value; its `ref` arguments are cells.
        if (name is "collectCycles" or "takeCycleStats")
            return CollectorIntrinsic(name, call, caller, location ?? "<unknown>");
        var arguments = call.Arguments.Where(a => a is not IrFieldDefault).Select(a => ArgumentValue(a, caller)).ToList();
        var map = caller.Map.Compose(call.Method.Map);
        // A generic method's `T*` parameter, with the caller's and the method's type arguments applied.
        WylType Pointed(int parameter) => ((PointerType)map.Apply(call.Method.Symbol.Parameters[parameter].Type)).Element;
        switch (name)
        {
            case "allocate":
            {
                int count = (int)(long)Integer(arguments[0]);
                if (count < 0)
                    throw new WylPanic("cannot allocate a negative number of elements", location ?? "<unknown>");
                // The runtime's size check can't trigger for a 32-bit count on a 64-bit target: no
                // type is 2^33 bytes, so SIZE_MAX / sizeof(T) always exceeds the largest count.
                return new PointerValue(AllocateBuffer(count), 0);
            }
            case "free":
                switch (arguments[0])
                {
                    case PointerValue storage:
                        storage.Buffer.Free();
                        break;
                    // As C++, which frees a null pointer as a no-op.
                    case NullValue:
                        break;
                    default:
                        throw new InterpreterException("Memory.free was given something that is not a pointer");
                }
                return null;
            case "initialize":
            {
                var place = PointerAt(arguments[0]);
                place.Buffer.Write(place.Index, new Cell(Pointed(0), arguments[1]));
                return null;
            }
            case "initializeDefault":
            {
                var data = PointerAt(arguments[0]);
                int count = (int)(long)Integer(arguments[1]);
                var element = Pointed(0);
                for (int i = 0; i < count; i++)
                    data.Buffer.Write(data.Index + i, new Cell(element, DefaultValue(element, caller)));
                return null;
            }
            case "drop":
            {
                var place = PointerAt(arguments[0]);
                var cell = place.Buffer.Read(place.Index) ?? throw new UndefinedBehavior("dropped storage that was never initialized");
                place.Buffer.Write(place.Index, null);
                Release(cell);
                return null;
            }
            case "moveTo":
            {
                int count = (int)(long)Integer(arguments[2]);
                // Moving nothing touches no storage, so a list that has allocated none moves from null.
                if (count == 0)
                    return null;
                MoveTo(PointerAt(arguments[0]), PointerAt(arguments[1]), count);
                return null;
            }
            default:
                throw new UnsupportedFeature($"the intrinsic 'wyl::memory::{name}'");
        }
    }

    /// <summary><c>Memory.collectCycles</c> and <c>Memory.cycleStats</c>, as <c>wyl::memory</c> has them.</summary>
    Value? CollectorIntrinsic(string name, IrCall call, Frame caller, string location)
    {
        var given = call.Arguments.Where(a => a is not IrFieldDefault).ToList();
        if (name == "collectCycles")
        {
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

        var stats = TakeStats();
        var values = new[] { stats.Buffered, stats.Visited, stats.Freed };
        for (int i = 0; i < values.Length; i++)
        {
            var cell = given[i] switch
            {
                IrRefArgument reference => PlaceCell(reference.Place, caller),
                IrAddrArgument address => PlaceCell(address.Place, caller),
                _ => throw new InterpreterException("Memory.cycleStats was given a value, not a ref"),
            };
            cell.Value = new IntValue(values[i] < int.MaxValue ? values[i] : int.MaxValue);
        }
        return null;
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
