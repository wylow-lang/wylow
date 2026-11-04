using System.Numerics;
using Wyl.Compiler.Semantics;

namespace Wyl.Generate;

/// <summary>
/// What the spec says about mixing stage-1 types: which implicit conversions exist
/// (docs/spec/05-conversions.md#conversions.numeric) and which type a binary operator on two of them
/// works in (docs/spec/13-expressions.md#expressions.arithmetic.operands). Written from the spec, not
/// asked of the compiler, so a binder that disagrees rejects or mistypes a generated program instead of
/// silently agreeing with itself; the self-check compares the table with <c>Conversions.IsImplicit</c>
/// before every run.
/// </summary>
static class ConversionFacts
{
    /// <summary>The integers a binary operator meets in, smallest first (§2.4); nint and nuint are never generated.</summary>
    static readonly WylType[] Integers = [.. IntegerFacts.Types];

    /// <summary>The §2.3 table among stage-1 types, without nint and nuint.</summary>
    static readonly Dictionary<WylType, WylType[]> Widenings = new()
    {
        [WylType.SByte] = [WylType.Short, WylType.Int, WylType.Long, WylType.Float, WylType.Double],
        [WylType.Byte] = [WylType.Short, WylType.UShort, WylType.Int, WylType.UInt, WylType.Long, WylType.ULong, WylType.Float, WylType.Double],
        [WylType.Short] = [WylType.Int, WylType.Long, WylType.Float, WylType.Double],
        [WylType.UShort] = [WylType.Int, WylType.UInt, WylType.Long, WylType.ULong, WylType.Float, WylType.Double],
        [WylType.Int] = [WylType.Long, WylType.Double],
        [WylType.UInt] = [WylType.Long, WylType.ULong, WylType.Double],
        [WylType.Long] = [],
        [WylType.ULong] = [],
        [WylType.Char] = [WylType.Int, WylType.UInt, WylType.Long, WylType.ULong],
        [WylType.Float] = [WylType.Double],
        [WylType.Double] = [],
    };

    /// <summary>The types the table covers: the numbers and <c>char</c>.</summary>
    public static IReadOnlyCollection<WylType> Numeric => Widenings.Keys;

    /// <summary>Every code point a <c>char</c> may hold, as a range; surrogates inside it are never values.</summary>
    public static readonly IntegerRange CodePoints = new(0, 0x10FFFF);

    public static bool Widens(WylType from, WylType to) => from != to && Widenings.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>The types whose every value converts implicitly to <paramref name="to"/>.</summary>
    public static IReadOnlyList<WylType> SourcesOf(WylType to) => [.. Widenings.Keys.Where(from => Widens(from, to))];

    /// <summary>
    /// The type an arithmetic or bitwise operator on two non-constant operands works in, or null when
    /// the spec makes it an error. A <c>char</c> operand is its code point, an <c>int</c>, first.
    /// </summary>
    public static WylType? Arithmetic(WylType left, WylType right) =>
        Common(left == WylType.Char ? WylType.Int : left, right == WylType.Char ? WylType.Int : right);

    /// <summary>
    /// The type a comparison of two non-constant numbers compares in, or null when it is an error;
    /// <c>char</c> compares only with <c>char</c>, so it has no mixed comparison.
    /// </summary>
    public static WylType? Comparison(WylType left, WylType right) =>
        left == WylType.Char || right == WylType.Char ? null : Common(left, right);

    static WylType? Common(WylType left, WylType right)
    {
        if (left == right)
            return left;
        if (Widens(left, right))
            return right;
        if (Widens(right, left))
            return left;
        if (left.IsInteger && right.IsInteger)
            return Integers.FirstOrDefault(type => Widens(left, type) && Widens(right, type));
        return null;
    }

    /// <summary>
    /// The operand type pairs whose arithmetic works in <paramref name="type"/> with at least one
    /// operand converted: a widening, the meet of two integers, or a <c>char</c>'s code point.
    /// </summary>
    public static IReadOnlyList<(WylType Left, WylType Right)> ArithmeticPairs(WylType type) =>
        [.. Pairs().Where(pair => (pair.Left != type || pair.Right != type) && Arithmetic(pair.Left, pair.Right) == type)];

    /// <summary>The pairs of different types that compare in a type neither converts to the other in, or in one.</summary>
    public static IReadOnlyList<(WylType Left, WylType Right)> ComparisonPairs { get; } =
        [.. Pairs().Where(pair => pair.Left != pair.Right && Comparison(pair.Left, pair.Right) is not null)];

    static IEnumerable<(WylType Left, WylType Right)> Pairs() =>
        from left in Widenings.Keys from right in Widenings.Keys select (left, right);

    /// <summary>The exact value of an integer converted to a floating-point type, or null when it is not one value or would round.</summary>
    public static ExactFloat? Exact(IntegerRange? range, WylType floatingType)
    {
        if (range is not { } single || single.Min != single.Max)
            return null;
        var value = new ExactFloat(single.Min, 0);
        return value.Fits(floatingType) ? value : null;
    }

    /// <summary>A float cast to an integer: toward zero, then saturated (§2.3); NaN is never generated.</summary>
    public static Int128 Truncate(ExactFloat value, WylType type)
    {
        var whole = value.Exponent >= 0
            ? value.Mantissa << value.Exponent
            : BigInteger.Divide(value.Mantissa, BigInteger.One << -value.Exponent);
        var full = IntegerFacts.Full(type);
        return whole < (BigInteger)full.Min ? full.Min : whole > (BigInteger)full.Max ? full.Max : (Int128)whole;
    }

    /// <summary>Whether every value in the range is a Unicode scalar value, so a cast to <c>char</c> cannot panic.</summary>
    public static bool AreScalars(IntegerRange range) =>
        (range.Min >= 0 && range.Max <= 0xD7FF) || (range.Min >= 0xE000 && range.Max <= 0x10FFFF);
}
