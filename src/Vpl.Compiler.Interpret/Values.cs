using System.Text;
using Vpl.Compiler.Semantics;

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

    public override string ToString() => $"{Type} [{Elements.Length}]";
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

/// <summary>Making a value from a literal or a default, copying, releasing, and comparing it.</summary>
public static class Values
{
    public static Cell CopyCell(Cell cell) => new(cell.Type, Copy(cell.Type, cell.Value));

    /// <summary>
    /// A copy of the value: aggregates are deep copies of their cells, and everything immutable
    /// (numbers, strings, a reference) is shared. An object's count rises in step 2.
    /// </summary>
    public static Value Copy(VplType type, Value value) => value switch
    {
        IntValue or FloatValue or BoolValue or CharValue or StringValue or NullValue or RefValue or FormattedValue => value,
        StructValue s => new StructValue(s.Type, [.. s.Fields.Select(CopyCell)]),
        EnumValue e => new EnumValue(e.Type, e.Variant, [.. e.Fields.Select(CopyCell)]),
        ArrayValue a => new ArrayValue(a.Type, [.. a.Elements.Select(CopyCell)]),
        OptionalValue o => new OptionalValue(o.Inner is null ? null : CopyCell(o.Inner)),
        MovedValue => throw new InterpreterException("read a value that was moved from"),
        _ => throw new InterpreterException($"copy of a {value.GetType().Name}"),
    };

    /// <summary>
    /// Releases what a value owns. Nothing in step 1 releases observably: strings and numbers own
    /// nothing, and no struct has a destructor. Objects release their counts in step 2.
    /// </summary>
    public static void Release(VplType type, Value value)
    {
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
        : type.IsFunction ? throw new UnsupportedFeature("function values land in step 3")
        : type.IsTypeParameter ? throw new UnsupportedFeature("a type parameter's default lands in step 3")
        : throw new InterpreterException($"a default {type}");

    /// <summary>The fields of a struct type, with its type arguments applied, in declaration order.</summary>
    public static IReadOnlyList<(FieldSymbol Field, VplType Type)> Fields(VplType type)
    {
        var map = TypeMap.For(type);
        return [.. type.Symbol!.Fields.Select(f => (f, map.Apply(f.Type)))];
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
            (RefValue x, RefValue y) => ReferenceEquals(x.Cell, y.Cell),
            (StructValue x, StructValue y) => x.Fields.Length == y.Fields.Length && x.Fields.Zip(y.Fields).All(p => Equal(p.First.Type, p.First.Value, p.Second.Value)),
            (EnumValue x, EnumValue y) => x.Variant == y.Variant && x.Fields.Length == y.Fields.Length && x.Fields.Zip(y.Fields).All(p => Equal(p.First.Type, p.First.Value, p.Second.Value)),
            (ArrayValue x, ArrayValue y) => x.Elements.Length == y.Elements.Length && x.Elements.Zip(y.Elements).All(p => Equal(p.First.Type, p.First.Value, p.Second.Value)),
            _ => false,
        };
    }

    public static IntValue Int(Int128 value) => new(value);

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
