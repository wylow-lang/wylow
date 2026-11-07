using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.Interpret;

// What a value owns: copying, releasing, making a default, and comparing, defined once for
// every kind of value and driven by the WylType.

/// <summary>Making a value from a literal or a default, copying, releasing, and comparing it.</summary>
public static class Values
{
    public static Cell CopyCell(Cell cell) => new(cell.Type, Copy(cell.Type, cell.Value));

    /// <summary>
    /// A copy of the value: aggregates are deep copies of their cells, everything immutable
    /// (numbers, strings) is shared, and a class reference is shared with its object's count risen.
    /// </summary>
    public static Value Copy(WylType type, Value value) => value switch
    {
        IntValue or FloatValue or BoolValue or CharValue or StringValue or NullValue or RefValue or FormattedValue or PointerValue or ThreadValue or SyncStateValue => value,
        ObjectRef obj => Retain(obj),
        // A choice copies every callable it holds, as a tuple of them does.
        ChoiceValue choice => new ChoiceValue(choice.Index, [.. choice.Options.Select(CopyCell)]),
        // Only the escaping form holds a count; a non-escaping value refers to a closure it doesn't own.
        FunctionValue function when type.NonNull.IsEscaping && function.Closure is not null => function.Copy(),
        FunctionValue => value,
        WeakValue weak => weak.Copy(),
        StructValue s => new StructValue(s.Type, [.. s.Fields.Select(CopyCell)]) { Engaged = s.Engaged },
        EnumValue e => new EnumValue(e.Type, e.Variant, [.. e.Fields.Select(CopyCell)]),
        ArrayValue a => new ArrayValue(a.Type, [.. a.Elements.Select(CopyCell)]),
        OptionalValue o => new OptionalValue(o.Inner is null ? null : CopyCell(o.Inner)),
        MovedValue => throw new InterpreterException("read a value that was moved from"),
        _ => throw new InterpreterException($"copy of a {value.GetType().Name}"),
    };

    static ObjectRef Retain(ObjectRef obj)
    {
        obj.Retain();
        return obj;
    }

    /// <summary>
    /// Releases what a value owns: a class reference lowers its object's count, and the last one asks
    /// the lifetime to destroy or buffer it (<see cref="IObjectLifetime.Released"/>); an escaping
    /// function value lowers its closure's the same way. A struct's or enum's
    /// fields and an array's elements go in reverse declaration order, as C++ destroys members; a
    /// closure's captures and a choice's callables in the order they are held. Numbers, strings,
    /// references, and moved-from values own nothing.
    /// </summary>
    public static void Release(WylType type, Value value, IObjectLifetime lifetime)
    {
        // It is a run-time error to release a value that something refers into (spec memory.holds).
        if (value is StructValue { InUse: true } or EnumValue { InUse: true } or OptionalValue { InUse: true })
            throw new WylPanic("cannot release a value that is in use", "<runtime>");
        switch (value)
        {
            case ObjectRef obj:
                lifetime.Released(obj, obj.ReleasedOne());
                break;
            case FunctionValue function when type.NonNull.IsEscaping && function.Closure is { } closure:
                lifetime.Released(closure, closure.ReleasedOne());
                break;
            case WeakValue weak:
                weak.Release();
                break;
            case IsolatedStateValue state:
                lifetime.ReleasingIsolated(state);
                break;
            case ChoiceValue choice:
                ReleaseHeld(choice.Options, lifetime);
                break;
            case StructValue s:
                lifetime.ReleasingStruct(s);
                ReleaseCells(s.Fields, lifetime);
                break;
            case EnumValue e:
                ReleaseCells(e.Fields, lifetime);
                break;
            case ArrayValue a:
                ReleaseCells(a.Elements, lifetime);
                break;
            case OptionalValue { Inner: { } inner }:
                Release(inner.Type, inner.Value, lifetime);
                break;
        }
    }

    static void ReleaseCells(Cell[] cells, IObjectLifetime lifetime)
    {
        for (int i = cells.Length - 1; i >= 0; i--)
            Release(cells[i].Type, cells[i].Value, lifetime);
    }

    /// <summary>
    /// A closure's captures and a choice's callables are released in the order they are held, as the
    /// runtime's tuples destroy them, not in the reverse order a struct's fields take
    /// (spec memory.release.destruction).
    /// </summary>
    internal static void ReleaseHeld(Cell[] cells, IObjectLifetime lifetime)
    {
        foreach (var cell in cells)
            Release(cell.Type, cell.Value, lifetime);
    }

    /// <summary>
    /// The zeroed storage of a type, which <c>IrZeroed</c> makes: a constructor starts from it, and
    /// a struct with a destructor that holds no value has it. <paramref name="engaged"/> makes the
    /// structs and elements it builds real values instead, as a default value's are.
    /// </summary>
    public static Value Zeroed(WylType type, StdlibSymbols stdlib, bool engaged = false)
    {
        if (stdlib.IsSyncState(type))
            return new SyncStateValue(null);
        if (stdlib.IsIsolatedState(type))
            return new IsolatedStateValue(new GraphBuffer());
        if (stdlib.IsWeak(type))
            return new WeakValue(null, null);
        if (type.IsNullableValue)
            return new OptionalValue(null);
        if (type is FixedArrayType array)
            return new ArrayValue(type, [.. Enumerable.Range(0, array.Length).Select(_ => new Cell(array.Element, Zeroed(array.Element, stdlib, engaged)))]);
        if (type.IsStruct)
            return new StructValue(type, [.. Fields(type).Select(f => new Cell(f.Type, Zeroed(f.Type, stdlib, engaged)))]) { Engaged = engaged };
        if (type.IsEnum && type.Symbol!.Variants.Count > 0)
            return new EnumValue(type, type.Symbol.Variants[0], []);
        if (type.IsReference)
            return NullValue.Instance;
        return type == WylType.String ? StringValue.Empty
            : type.IsPointer ? NullValue.Instance
            : type == WylType.Bool ? new BoolValue(false)
            : type == WylType.Char ? new CharValue(0)
            : type.IsFloatingPoint ? new FloatValue(0.0)
            : type.IsInteger ? new IntValue(0)
            : type.IsFunction ? NullValue.Instance
            : type.IsTypeParameter ? throw new InterpreterException($"the type parameter '{type}' has no type argument")
            : throw new InterpreterException($"a default {type}");
    }

    /// <summary>The fields of a struct type, with its type arguments applied, in declaration order.</summary>
    public static IReadOnlyList<(FieldSymbol Field, WylType Type)> Fields(WylType type)
    {
        var map = TypeMap.For(type);
        return [.. type.Symbol!.Fields.Select(f => (f, map.Apply(f.Type)))];
    }

    /// <summary>
    /// An object of a class, its fields zeroed, one reference held by the <c>new</c> expression, and
    /// dispatching as the root class until a constructor reaches its own class.
    /// </summary>
    public static ObjectRef Allocate(WylType type, StdlibSymbols stdlib)
    {
        var fields = new Dictionary<TypeSymbol, Cell[]>();
        WylType? deepest = null;
        for (WylType? current = type.NonNull; current is not null && current.Symbol is { Kind: TypeKind.Class } symbol; current = current.BaseType)
        {
            var map = TypeMap.For(current);
            fields[symbol] = [.. symbol.Fields.Select(f => new Cell(map.Apply(f.Type), Zeroed(map.Apply(f.Type), stdlib)))];
            deepest = current;
        }
        return new ObjectRef(type, fields) { Active = deepest ?? type };
    }

    public static bool Equal(WylType type, Value left, Value right)
    {
        if (type.IsNullableValue)
        {
            var a = left as OptionalValue ?? throw new InterpreterException("a nullable value is not optional");
            var b = right as OptionalValue ?? throw new InterpreterException("a nullable value is not optional");
            return a.Inner is null ? b.Inner is null : b.Inner is not null && Equal(type.NonNull, a.Inner.Value, b.Inner.Value);
        }
        return (left, right) switch
        {
            (IntValue x, IntValue y) => x.Bits == y.Bits,
            (FloatValue x, FloatValue y) => x.Number == y.Number,
            (BoolValue x, BoolValue y) => x.Flag == y.Flag,
            (CharValue x, CharValue y) => x.CodePoint == y.CodePoint,
            (StringValue x, StringValue y) => x.Bytes.AsSpan().SequenceEqual(y.Bytes),
            (NullValue, NullValue) => true,
            (ObjectRef x, ObjectRef y) => ReferenceEquals(x, y),
            (RefValue x, RefValue y) => ReferenceEquals(x.Cell, y.Cell),
            (PointerValue x, PointerValue y) => SameAddress(x, y),
            (WeakValue x, WeakValue y) => ReferenceEquals(x.Block, y.Block),
            (StructValue x, StructValue y) => x.Fields.Length == y.Fields.Length && x.Fields.Zip(y.Fields).All(p => Equal(p.First.Type, p.First.Value, p.Second.Value)),
            (EnumValue x, EnumValue y) => x.Variant == y.Variant && x.Fields.Length == y.Fields.Length && x.Fields.Zip(y.Fields).All(p => Equal(p.First.Type, p.First.Value, p.Second.Value)),
            (ArrayValue x, ArrayValue y) => x.Elements.Length == y.Elements.Length && x.Elements.Zip(y.Elements).All(p => Equal(p.First.Type, p.First.Value, p.Second.Value)),
            _ => false,
        };
    }

    public static IntValue Int(Int128 value) => new(value);

    /// <summary>Whether two pointers point at the same element of the same storage, as their addresses comparing equal.</summary>
    public static bool SameAddress(PointerValue one, PointerValue other) =>
        ReferenceEquals(one.Buffer, other.Buffer) && one.Index == other.Index;

    public static bool Truth(Value value) => value is BoolValue flag && flag.Flag;

    /// <summary>The bits of an integer type: its width and whether it is signed.</summary>
    public static (int Width, bool Signed) Bits(WylType type) => type switch
    {
        _ when type == WylType.SByte => (8, true),
        _ when type == WylType.Byte => (8, false),
        _ when type == WylType.Short => (16, true),
        _ when type == WylType.UShort => (16, false),
        _ when type == WylType.Int => (32, true),
        _ when type == WylType.UInt => (32, false),
        _ when type == WylType.Long || type == WylType.NInt => (64, true),
        _ when type == WylType.ULong || type == WylType.NUInt => (64, false),
        _ when type == WylType.Char => (32, false),
        _ => throw new InterpreterException($"'{type}' is not an integer type"),
    };

    /// <summary>Wraps a number into the type's range, two's complement, as C++ does.</summary>
    public static Int128 Wrap(WylType type, Int128 value)
    {
        var (width, signed) = Bits(type);
        var mask = (Int128.One << width) - 1;
        var bits = value & mask;
        if (signed && (bits & (Int128.One << (width - 1))) != 0)
            bits -= Int128.One << width;
        return bits;
    }

    /// <summary>The unsigned bit pattern of a value, of the type's width.</summary>
    public static Int128 Unsigned(WylType type, Int128 value)
    {
        var (width, _) = Bits(type);
        return value & ((Int128.One << width) - 1);
    }
}
