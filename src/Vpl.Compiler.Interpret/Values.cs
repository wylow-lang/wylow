using System.Text;
using Vpl.Compiler.Ir;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Interpret;

// The interpreter's values and storage. Every place is a Cell, so a reference to a field is a
// reference to the same cell after any number of projections. Copy, release, default, and equality
// are defined once here, driven by the VplType, and nothing else in the interpreter decides them.

/// <summary>A type and the value in it, mutable in place: every place the IR names.</summary>
public sealed class Cell(VplType type, Value value)
{
    public VplType Type { get; } = type;
    public Value Value { get; set; } = value;

    public override string ToString() => $"{Type}: {Value}";
}

/// <summary>A VPL value: an immutable scalar or string, or a tree of cells.</summary>
public abstract class Value;

/// <summary>An integer of any width, already normalized to its type's range.</summary>
public sealed class IntValue(Int128 bits) : Value
{
    public Int128 Bits { get; } = bits;

    public override string ToString() => Bits.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A <c>float</c> or <c>double</c>, which keeps its IEEE value; a float is rounded on every operation.</summary>
public sealed class FloatValue(double number) : Value
{
    public double Number { get; } = number;

    public override string ToString() => Number.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class BoolValue(bool flag) : Value
{
    public bool Flag { get; } = flag;

    public override string ToString() => Flag ? "true" : "false";
}

/// <summary>A <c>char</c>, a Unicode scalar value.</summary>
public sealed class CharValue(int codePoint) : Value
{
    public int CodePoint { get; } = codePoint;

    public override string ToString() => char.ConvertFromUtf32(CodePoint);
}

/// <summary>An immutable UTF-8 string; copies share the bytes, as the runtime's counted block does.</summary>
public sealed class StringValue(byte[] bytes) : Value
{
    public static readonly StringValue Empty = new([]);

    public byte[] Bytes { get; } = bytes;

    public static StringValue Of(string text) => new(Encoding.UTF8.GetBytes(text));

    public string Text => Encoding.UTF8.GetString(Bytes);
    public override string ToString() => Text;
}

/// <summary>Null of a nullable reference or a pointer.</summary>
public sealed class NullValue : Value
{
    public static readonly NullValue Instance = new();

    public override string ToString() => "null";
}

/// <summary>
/// A class object: its fields, one array per class that declares them, an intrusive count, and the
/// class a dispatched call runs on it now (a base during its construction or destruction, as C++).
/// Every copy of a reference is this same instance with the count raised, so a field written
/// through one is seen through every other.
/// </summary>
public sealed class ObjectRef(VplType type, Dictionary<TypeSymbol, Cell[]> fields) : Value
{
    public VplType Type { get; } = type;
    public Dictionary<TypeSymbol, Cell[]> Fields { get; } = fields;

    /// <summary>The references to it; the <c>new</c> expression owns the first.</summary>
    public long Count { get; set; } = 1;

    /// <summary>The class whose methods dispatch on it: its own, or a base while it is constructed or destroyed.</summary>
    public VplType Active { get; set; } = type;

    /// <summary>The classes of its chain, most derived first, which each own the fields they declare.</summary>
    public IEnumerable<VplType> Chain
    {
        get
        {
            for (VplType? current = Type.NonNull; current is not null && current.Symbol is { Kind: TypeKind.Class }; current = current.BaseType)
                yield return current;
        }
    }

    public Cell FieldCell(FieldSymbol field) => Fields[field.Owner][field.Owner.Fields.IndexOf(field)];

    public override string ToString() => $"{Type}";
}

/// <summary>A struct: its fields, in declaration order.</summary>
public sealed class StructValue(VplType type, Cell[] fields) : Value
{
    public VplType Type { get; } = type;
    public Cell[] Fields { get; } = fields;

    public override string ToString() => $"{Type} {{...}}";
}

/// <summary>An enum: the active variant and its fields.</summary>
public sealed class EnumValue(VplType type, VariantSymbol variant, Cell[] fields) : Value
{
    public VplType Type { get; } = type;
    public VariantSymbol Variant { get; } = variant;
    public Cell[] Fields { get; } = fields;

    public override string ToString() => $"{Variant}";
}

/// <summary>A fixed array: its elements, stored inline.</summary>
public sealed class ArrayValue(VplType type, Cell[] elements) : Value
{
    public VplType Type { get; } = type;
    public Cell[] Elements { get; } = elements;

    /// <summary>
    /// The storage <c>data</c> points into, made once so that two pointers to this array compare
    /// equal, as their addresses do. Its cells are this array's own, so a write through a pointer
    /// is seen through the array.
    /// </summary>
    public Buffer View => view ??= Buffer.Over(Elements);

    Buffer? view;

    public override string ToString() => $"{Type} [{Elements.Length}]";
}

/// <summary>
/// Raw storage a pointer walks, a slot per element that is empty until a value is initialized in
/// it, as <c>Memory.allocate</c> hands out. A fixed array's elements are a buffer whose cells it
/// shares; a string's bytes are a buffer of their own.
/// </summary>
public sealed class Buffer
{
    readonly Cell?[] cells;
    bool freed;

    Buffer(Cell?[] cells) => this.cells = cells;

    public static Buffer Allocate(int count) => new(new Cell?[count]);

    /// <summary>A buffer over an array's own element cells, which a write through a pointer reaches.</summary>
    public static Buffer Over(Cell[] elements) => new([.. elements.Select(cell => (Cell?)cell)]);

    /// <summary>The cell at <paramref name="index"/>, or null while the storage is uninitialized.</summary>
    public Cell? Read(int index)
    {
        Check(index);
        return cells[index];
    }

    public void Write(int index, Cell? cell)
    {
        Check(index);
        cells[index] = cell;
    }

    public void Free() => freed = true;

    /// <summary>Indexing outside a buffer, or using freed storage, is the program's undefined behavior.</summary>
    void Check(int index)
    {
        if (freed)
            throw new UndefinedBehavior("memory was used after it was freed");
        if ((uint)index >= (uint)cells.Length)
            throw new UndefinedBehavior($"a pointer read element {index} of storage holding {cells.Length}");
    }
}

/// <summary>A pointer: a buffer and the element it points at, which may be one past the end. Null is <see cref="NullValue"/>.</summary>
public sealed class PointerValue(Buffer buffer, int index) : Value
{
    public Buffer Buffer { get; } = buffer;
    public int Index { get; } = index;

    public override string ToString() => $"pointer to element {Index}";
}

/// <summary>A nullable value: the value, or null.</summary>
public sealed class OptionalValue(Cell? inner) : Value
{
    public Cell? Inner { get; } = inner;

    public override string ToString() => Inner is null ? "null" : Inner.Value.ToString()!;
}

/// <summary>A <c>ref?</c> or <c>in?</c> value: the cell it refers to, or null.</summary>
public sealed class RefValue(Cell? cell) : Value
{
    public Cell? Cell { get; } = cell;

    public override string ToString() => Cell is null ? "null ref" : "ref";
}

/// <summary>
/// A function value: a lambda's code, the type arguments its body runs with, and what it reads when
/// it runs — the frame whose locals it uses as places, or the closure holding copies of what it
/// captured. Its escaping form counts its closure, as <c>vpl::Function</c> does; the non-escaping
/// form refers to one it doesn't own.
/// </summary>
internal sealed class FunctionValue(IrFunction function, TypeMap map, Interpreter.Frame? enclosing, ClosureValue? closure) : Value
{
    public IrFunction Function { get; } = function;
    public TypeMap Map { get; } = map;
    public Interpreter.Frame? Enclosing { get; } = enclosing;
    public ClosureValue? Closure { get; } = closure;

    public override string ToString() => Function.Name;
}

/// <summary>
/// An escaping lambda's captures, counted like an object: copies of the function value share it, and
/// the last releases its captures, in the order the lambda captured them.
/// </summary>
public sealed class ClosureValue(Cell[] captures) : Value
{
    public Cell[] Captures { get; } = captures;

    /// <summary>The references to it; making it holds the first.</summary>
    public long Count { get; set; } = 1;

    public override string ToString() => "closure";
}

/// <summary>A choice of functions: the index of the one it selects, and every callable it may select, which it owns.</summary>
public sealed class ChoiceValue(int index, Cell[] options) : Value
{
    public int Index { get; } = index;
    public Cell[] Options { get; } = options;

    public override string ToString() => $"choice {Index}";
}

/// <summary>A value interpolation defers to write with its format specifier and alignment.</summary>
public sealed class FormattedValue(VplType innerType, Value inner, int kind, int precision, int alignment) : Value
{
    public VplType InnerType { get; } = innerType;
    public Value Inner { get; } = inner;
    public int Kind { get; } = kind;
    public int Precision { get; } = precision;
    public int Alignment { get; } = alignment;

    public override string ToString() => "formatted";
}

/// <summary>What a move leaves in a cell; reading or releasing it is a compiler bug.</summary>
public sealed class MovedValue : Value
{
    public static readonly MovedValue Instance = new();

    public override string ToString() => "<moved>";
}

/// <summary>What releasing a value asks when an object's last reference goes: to run its destruction.</summary>
public interface IObjectLifetime
{
    void Destroy(ObjectRef obj);
}

/// <summary>Making a value from a literal or a default, copying, releasing, and comparing it.</summary>
public static class Values
{
    public static Cell CopyCell(Cell cell) => new(cell.Type, Copy(cell.Type, cell.Value));

    /// <summary>
    /// A copy of the value: aggregates are deep copies of their cells, everything immutable
    /// (numbers, strings) is shared, and a class reference is shared with its object's count risen.
    /// </summary>
    public static Value Copy(VplType type, Value value) => value switch
    {
        IntValue or FloatValue or BoolValue or CharValue or StringValue or NullValue or RefValue or FormattedValue or PointerValue => value,
        ObjectRef obj => Retain(obj),
        // A choice copies every callable it holds, as a tuple of them does.
        ChoiceValue choice => new ChoiceValue(choice.Index, [.. choice.Options.Select(CopyCell)]),
        // Only the escaping form holds a count; a non-escaping value refers to a closure it doesn't own.
        FunctionValue function when type.NonNull.IsEscaping => Retain(function),
        FunctionValue => value,
        StructValue s => new StructValue(s.Type, [.. s.Fields.Select(CopyCell)]),
        EnumValue e => new EnumValue(e.Type, e.Variant, [.. e.Fields.Select(CopyCell)]),
        ArrayValue a => new ArrayValue(a.Type, [.. a.Elements.Select(CopyCell)]),
        OptionalValue o => new OptionalValue(o.Inner is null ? null : CopyCell(o.Inner)),
        MovedValue => throw new InterpreterException("read a value that was moved from"),
        _ => throw new InterpreterException($"copy of a {value.GetType().Name}"),
    };

    static ObjectRef Retain(ObjectRef obj)
    {
        obj.Count++;
        return obj;
    }

    /// <summary>A copy of a function value is the same two words with the closure's count risen.</summary>
    static FunctionValue Retain(FunctionValue function)
    {
        if (function.Closure is { } closure)
            closure.Count++;
        return function;
    }

    /// <summary>
    /// Releases what a value owns: a class reference lowers its object's count, and an object's last
    /// reference runs its destructors (<see cref="IObjectLifetime.Destroy"/>); an escaping function
    /// value lowers its closure's, which releases what it captured at zero. A struct's or enum's
    /// fields and an array's elements go in reverse declaration order, as C++ destroys members; a
    /// closure's captures and a choice's callables in the order they are held. Numbers, strings,
    /// references, and moved-from values own nothing.
    /// </summary>
    public static void Release(VplType type, Value value, IObjectLifetime lifetime)
    {
        switch (value)
        {
            case ObjectRef obj:
                if (--obj.Count == 0)
                    lifetime.Destroy(obj);
                break;
            case FunctionValue function when type.NonNull.IsEscaping && function.Closure is { } closure:
                if (--closure.Count == 0)
                    ReleaseHeld(closure.Captures, lifetime);
                break;
            case ChoiceValue choice:
                ReleaseHeld(choice.Options, lifetime);
                break;
            case StructValue s:
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
    /// (docs/spec/03-memory-model.md, When references are released).
    /// </summary>
    static void ReleaseHeld(Cell[] cells, IObjectLifetime lifetime)
    {
        foreach (var cell in cells)
            Release(cell.Type, cell.Value, lifetime);
    }

    /// <summary>The zeroed storage of a type, which <c>IrZeroed</c> makes and a default builds on.</summary>
    public static Value Zeroed(VplType type) =>
        type.IsNullableValue ? new OptionalValue(null)
        : type.IsFixedArray ? new ArrayValue(type, [.. Enumerable.Range(0, ((FixedArrayType)type).Length).Select(_ => new Cell(((FixedArrayType)type).Element, Zeroed(((FixedArrayType)type).Element)))])
        : type.IsStruct ? new StructValue(type, [.. Fields(type).Select(f => new Cell(f.Type, Zeroed(f.Type)))])
        : type.IsEnum && type.Symbol!.Variants.Count > 0 ? new EnumValue(type, type.Symbol.Variants[0], [])
        : type.IsReference ? NullValue.Instance
        : type == VplType.String ? StringValue.Empty
        : type.IsPointer ? NullValue.Instance
        : type == VplType.Bool ? new BoolValue(false)
        : type == VplType.Char ? new CharValue(0)
        : type.IsFloatingPoint ? new FloatValue(0.0)
        : type.IsInteger ? new IntValue(0)
        : type.IsFunction ? NullValue.Instance
        : type.IsTypeParameter ? throw new InterpreterException($"the type parameter '{type}' has no type argument")
        : throw new InterpreterException($"a default {type}");

    /// <summary>The fields of a struct type, with its type arguments applied, in declaration order.</summary>
    public static IReadOnlyList<(FieldSymbol Field, VplType Type)> Fields(VplType type)
    {
        var map = TypeMap.For(type);
        return [.. type.Symbol!.Fields.Select(f => (f, map.Apply(f.Type)))];
    }

    /// <summary>
    /// An object of a class, its fields zeroed, one reference held by the <c>new</c> expression, and
    /// dispatching as the root class until a constructor reaches its own class.
    /// </summary>
    public static ObjectRef Allocate(VplType type)
    {
        var fields = new Dictionary<TypeSymbol, Cell[]>();
        VplType? deepest = null;
        for (VplType? current = type.NonNull; current is not null && current.Symbol is { Kind: TypeKind.Class } symbol; current = current.BaseType)
        {
            var map = TypeMap.For(current);
            fields[symbol] = [.. symbol.Fields.Select(f => new Cell(map.Apply(f.Type), Zeroed(map.Apply(f.Type))))];
            deepest = current;
        }
        return new ObjectRef(type, fields) { Active = deepest ?? type };
    }

    public static bool Equal(VplType type, Value left, Value right)
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
    public static (int Width, bool Signed) Bits(VplType type) => type switch
    {
        _ when type == VplType.SByte => (8, true),
        _ when type == VplType.Byte => (8, false),
        _ when type == VplType.Short => (16, true),
        _ when type == VplType.UShort => (16, false),
        _ when type == VplType.Int => (32, true),
        _ when type == VplType.UInt => (32, false),
        _ when type == VplType.Long || type == VplType.NInt => (64, true),
        _ when type == VplType.ULong || type == VplType.NUInt => (64, false),
        _ when type == VplType.Char => (32, false),
        _ => throw new InterpreterException($"'{type}' is not an integer type"),
    };

    /// <summary>Wraps a number into the type's range, two's complement, as C++ does.</summary>
    public static Int128 Wrap(VplType type, Int128 value)
    {
        var (width, signed) = Bits(type);
        var mask = (Int128.One << width) - 1;
        var bits = value & mask;
        if (signed && (bits & (Int128.One << (width - 1))) != 0)
            bits -= Int128.One << width;
        return bits;
    }

    /// <summary>The unsigned bit pattern of a value, of the type's width.</summary>
    public static Int128 Unsigned(VplType type, Int128 value)
    {
        var (width, _) = Bits(type);
        return value & ((Int128.One << width) - 1);
    }
}
