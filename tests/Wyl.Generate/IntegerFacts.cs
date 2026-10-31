using System.Globalization;
using System.Numerics;
using Wyl.Compiler.Semantics;

namespace Wyl.Generate;

/// <summary>
/// The one home for integer range arithmetic and literal spelling: every overflow guard and every
/// printed integer literal goes through it (docs/spec/02-types.md §2.4).
/// </summary>
static class IntegerFacts
{
    /// <summary>The fixed-width integer types. A range endpoint stays inside <see cref="Int128"/>.</summary>
    public static readonly IReadOnlyList<WylType> Types =
    [
        WylType.SByte,
        WylType.Byte,
        WylType.Short,
        WylType.UShort,
        WylType.Int,
        WylType.UInt,
        WylType.Long,
        WylType.ULong,
    ];

    public static IntegerRange Full(WylType type) =>
        type.HeldRange ?? throw new ArgumentException($"'{type.Name}' is not an integer type");

    public static bool Fits(WylType type, IntegerRange range) => Full(type).Contains(range);

    public static IntegerRange? Add(IntegerRange a, IntegerRange b) => Of(a.Min + b.Min, a.Max + b.Max);

    public static IntegerRange? Subtract(IntegerRange a, IntegerRange b) => Of(a.Min - b.Max, a.Max - b.Min);

    public static IntegerRange? Multiply(IntegerRange a, IntegerRange b)
    {
        BigInteger[] products =
        [
            (BigInteger)a.Min * b.Min,
            (BigInteger)a.Min * b.Max,
            (BigInteger)a.Max * b.Min,
            (BigInteger)a.Max * b.Max,
        ];
        return Of(products.Min(), products.Max());
    }

    public static IntegerRange? Divide(IntegerRange a, IntegerRange b)
    {
        if (b.Contains(0))
            return null;
        BigInteger[] quotients =
        [
            (BigInteger)a.Min / b.Min,
            (BigInteger)a.Min / b.Max,
            (BigInteger)a.Max / b.Min,
            (BigInteger)a.Max / b.Max,
        ];
        return Of(quotients.Min(), quotients.Max());
    }

    /// <summary>A conservative bound: a remainder is smaller than the divisor and has the dividend's sign.</summary>
    public static IntegerRange? Remainder(IntegerRange a, IntegerRange b)
    {
        if (b.Contains(0))
            return null;
        BigInteger limit = BigInteger.Max(BigInteger.Abs((BigInteger)b.Min), BigInteger.Abs((BigInteger)b.Max)) - 1;
        BigInteger min = a.Min < 0 ? -limit : 0;
        BigInteger max = a.Max > 0 ? limit : 0;
        return Of(min, max);
    }

    public static IntegerRange? Negate(IntegerRange a) => Of(-a.Max, -a.Min);

    /// <summary>Two's complement: <c>~x</c> is <c>-x - 1</c> for signed types and the type's maximum minus <c>x</c> otherwise.</summary>
    public static IntegerRange? Not(WylType type, IntegerRange a) => type.IsSigned
        ? Of(-a.Max - 1, -a.Min - 1)
        : Of(Full(type).Max - a.Max, Full(type).Max - a.Min);

    public static IntegerRange? ShiftLeft(IntegerRange a, int count)
    {
        BigInteger scale = BigInteger.One << count;
        return Of((BigInteger)a.Min * scale, (BigInteger)a.Max * scale);
    }

    /// <summary>Arithmetic for signed types and logical for unsigned ones, which is one shift on these endpoints.</summary>
    public static IntegerRange? ShiftRight(IntegerRange a, int count) => Of(a.Min >> count, a.Max >> count);

    /// <summary>The range of a compound assignment's result, or null when it may overflow the type.</summary>
    public static IntegerRange? Apply(string op, WylType type, IntegerRange a, IntegerRange b) => op switch
    {
        "+" => Add(a, b),
        "-" => Subtract(a, b),
        "*" => Multiply(a, b),
        // A bitwise operation on two values of one type is a value of that type; it cannot overflow.
        "&" or "|" or "^" => Full(type),
        "/" => Divide(a, b),
        "%" => Remainder(a, b),
        _ => null,
    };

    public static Int128 Random(WylType type, Rng rng)
    {
        var range = Full(type);
        var span = range.Max - range.Min;
        // A 64-bit span (long and ulong) would overflow the ulong cast of span + 1.
        if (span == ulong.MaxValue)
            return range.Min + (Int128)rng.NextU64();
        return range.Min + (Int128)(rng.NextU64() % (ulong)(span + 1));
    }

    /// <summary>Spells a value as a literal of exactly this type (docs/spec/02-types.md §2.2).</summary>
    public static string Literal(WylType type, Int128 value)
    {
        var digits = Constant(value);
        if (type == WylType.UInt)
            return digits + "u";
        if (type == WylType.Long)
            return digits + "L";
        if (type == WylType.ULong)
            return digits + "uL";
        if (type == WylType.Int)
            return Constant(value);
        // sbyte, byte, short, and ushort have no suffix, and an unsuffixed literal is an int: a cast
        // makes its type intrinsic, since a binary operator or conditional branch would widen it.
        return $"(({type.Name})({digits}))";
    }

    /// <summary>
    /// An unsuffixed constant: an <c>int</c> that converts to any integer type holding its value and to
    /// a float that holds it exactly (docs/spec/02-types.md §2.3). A negative one is a negated literal,
    /// which is still a constant.
    /// </summary>
    public static string Constant(Int128 value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether <see cref="Literal"/> spells this type's values as unsuffixed constants.</summary>
    public static bool SpellsConstant(WylType type) => type == WylType.Int;

    /// <summary>The curated edges: zero, one, both extremes, and the sign bit, where a type has one.</summary>
    public static IReadOnlyList<Int128> Literals(WylType type)
    {
        var full = Full(type);
        int width = type.ShiftWidth;
        Int128 signBit = Int128.One << (width - 1);
        List<Int128> values =
        [
            0, 1, -1, 2, -2, 42, -7,
            full.Max, full.Max - 1, full.Min, full.Min + 1,
            signBit, signBit - 1, -signBit,
        ];
        return [.. values.Where(full.Contains).Distinct()];
    }

    static IntegerRange? Of(Int128 min, Int128 max) => min <= max ? new IntegerRange(min, max) : null;

    static IntegerRange? Of(BigInteger min, BigInteger max) =>
        min < Int128.MinValue || max > Int128.MaxValue ? null : new IntegerRange((Int128)min, (Int128)max);
}
