using System.Numerics;
using Wyl.Compiler.Semantics;

namespace Wyl.Generate;

/// <summary>
/// A dyadic rational, <c>Mantissa * 2^Exponent</c>. Float arithmetic is emitted only when both
/// operands are exact and the result is exactly representable, so a C++ compiler cannot contract an
/// addition and a multiplication into an FMA and change the last bit (docs/spec/13-expressions.md#expressions.arithmetic).
/// </summary>
readonly record struct ExactFloat(BigInteger Mantissa, int Exponent)
{
    public bool IsZero => Mantissa.IsZero;

    public ExactFloat Add(ExactFloat other)
    {
        int exponent = Math.Min(Exponent, other.Exponent);
        var left = Mantissa << (Exponent - exponent);
        var right = other.Mantissa << (other.Exponent - exponent);
        return Normalized(new ExactFloat(left + right, exponent));
    }

    public ExactFloat Subtract(ExactFloat other) => Add(new ExactFloat(-other.Mantissa, other.Exponent));

    public ExactFloat Multiply(ExactFloat other) =>
        Normalized(new ExactFloat(Mantissa * other.Mantissa, Exponent + other.Exponent));

    /// <summary>Division by <c>2^shift</c>, the only float division the generator emits.</summary>
    public ExactFloat DivideByPowerOfTwo(int shift) => new(Mantissa, Exponent - shift);

    /// <summary>Whether IEEE 754 stores this value exactly in <paramref name="type"/>, without subnormals.</summary>
    public bool Fits(WylType type)
    {
        if (Mantissa.IsZero)
            return true;
        var (mantissa, exponent) = Reduced();
        int bits = (int)BigInteger.Abs(mantissa).GetBitLength();
        int valueExponent = exponent + bits - 1;
        int precision = type == WylType.Float ? 24 : type == WylType.Double ? 53 : throw new ArgumentException($"'{type.Name}' is not a floating-point type");
        int minimum = type == WylType.Float ? -126 : -1022;
        int maximum = type == WylType.Float ? 127 : 1023;
        return bits <= precision && valueExponent >= minimum && valueExponent <= maximum;
    }

    static ExactFloat Normalized(ExactFloat value) => value.Mantissa.IsZero ? new ExactFloat(BigInteger.Zero, 0) : value;

    (BigInteger Mantissa, int Exponent) Reduced()
    {
        var mantissa = Mantissa;
        int exponent = Exponent;
        while (!mantissa.IsZero && mantissa.IsEven)
        {
            mantissa >>= 1;
            exponent++;
        }
        return (mantissa, exponent);
    }
}

/// <summary>The exactly representable literals and their values, in one place (docs/spec/02-lexical.md#lexical.literals.float).</summary>
static class FloatFacts
{
    static readonly (string Text, ExactFloat Value)[] Values =
    [
        ("0.0", new ExactFloat(BigInteger.Zero, 0)),
        ("1.0", new ExactFloat(BigInteger.One, 0)),
        ("-1.0", new ExactFloat(BigInteger.MinusOne, 0)),
        ("2.0", new ExactFloat(2, 0)),
        ("-2.0", new ExactFloat(-2, 0)),
        ("3.0", new ExactFloat(3, 0)),
        ("-3.0", new ExactFloat(-3, 0)),
        ("4.0", new ExactFloat(4, 0)),
        ("-4.0", new ExactFloat(-4, 0)),
        ("8.0", new ExactFloat(8, 0)),
        ("0.5", new ExactFloat(1, -1)),
        ("-0.5", new ExactFloat(-1, -1)),
        ("1.5", new ExactFloat(3, -1)),
        ("-1.5", new ExactFloat(-3, -1)),
        ("2.5", new ExactFloat(5, -1)),
        ("0.25", new ExactFloat(1, -2)),
    ];

    static readonly (string Text, ExactFloat Value)[] FloatLiterals = [.. Values];
    static readonly (string Text, ExactFloat Value)[] DoubleLiterals = [.. Values.Select(value => (value.Text + "d", value.Value))];

    public static IReadOnlyList<(string Text, ExactFloat Value)> Literals(WylType type) => type == WylType.Float
        ? FloatLiterals
        : type == WylType.Double
            ? DoubleLiterals
            : throw new ArgumentException($"'{type.Name}' is not a floating-point type");

    /// <summary>Divisors that are powers of two, with the exponent they divide by.</summary>
    public static readonly IReadOnlyList<(string Text, int Shift)> Divisors =
    [
        ("2.0", 1),
        ("4.0", 2),
        ("8.0", 3),
        ("0.5", -1),
        ("0.25", -2),
    ];
}
