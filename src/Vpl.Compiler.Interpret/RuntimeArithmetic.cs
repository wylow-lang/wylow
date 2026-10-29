using System.Globalization;
using Vpl.Compiler.Semantics;

namespace Vpl.Compiler.Interpret;

/// <summary>
/// The checked arithmetic and conversions of <c>runtime/vpl/arithmetic.h</c>: overflow panics in
/// debug and wraps in release, division and <c>MIN / -1</c> always panic, shifts mask, floating
/// point to integer saturates, and casts to <c>char</c> and enums are checked.
/// </summary>
internal static class RuntimeArithmetic
{
    public static IntValue Add(VplType type, Int128 a, Int128 b, bool release, string? location)
    {
        var sum = a + b;
        if (!release && !InRange(type, sum))
            Overflow(location);
        return new IntValue(Values.Wrap(type, sum));
    }

    public static IntValue Subtract(VplType type, Int128 a, Int128 b, bool release, string? location)
    {
        var difference = a - b;
        if (!release && !InRange(type, difference))
            Overflow(location);
        return new IntValue(Values.Wrap(type, difference));
    }

    public static IntValue Multiply(VplType type, Int128 a, Int128 b, bool release, string? location)
    {
        if (!release && MulOverflows(type, a, b))
            Overflow(location);
        return new IntValue(Values.Wrap(type, unchecked(a * b)));
    }

    public static IntValue Negate(VplType type, Int128 a, bool release, string? location)
    {
        var (_, signed) = Values.Bits(type);
        if (!release && signed && a == Min(type))
            Overflow(location);
        return new IntValue(Values.Wrap(type, -a));
    }

    public static IntValue Divide(VplType type, Int128 a, Int128 b, string location)
    {
        if (b == 0)
            new Panic("division by zero", location).Raise();
        var (_, signed) = Values.Bits(type);
        if (signed && b == -1 && a == Min(type))
            Overflow(location);
        return new IntValue(Values.Wrap(type, a / b));
    }

    public static IntValue Remainder(VplType type, Int128 a, Int128 b, string location)
    {
        if (b == 0)
            new Panic("division by zero", location).Raise();
        var (_, signed) = Values.Bits(type);
        if (signed && b == -1)
            return new IntValue(0);
        return new IntValue(Values.Wrap(type, a % b));
    }

    public static IntValue ShiftLeft(VplType type, Int128 value, Int128 count)
    {
        var (width, _) = Values.Bits(type);
        int shift = (int)(count & (width - 1));
        return new IntValue(Values.Wrap(type, Values.Unsigned(type, value) << shift));
    }

    public static IntValue ShiftRight(VplType type, Int128 value, Int128 count)
    {
        var (width, signed) = Values.Bits(type);
        int shift = (int)(count & (width - 1));
        return new IntValue(Values.Wrap(type, signed ? value >> shift : Values.Unsigned(type, value) >> shift));
    }

    public static IntValue Bitwise(VplType type, BoundBinaryOperator op, Int128 a, Int128 b) =>
        new(Values.Wrap(type, op switch
        {
            BoundBinaryOperator.BitwiseAnd => a & b,
            BoundBinaryOperator.BitwiseOr => a | b,
            _ => a ^ b,
        }));

    public static IntValue Complement(VplType type, Int128 value) => new(Values.Wrap(type, ~value));

    public static FloatValue Floating(VplType type, BoundBinaryOperator op, double a, double b) =>
        new(Round(type, op switch
        {
            BoundBinaryOperator.Add => a + b,
            BoundBinaryOperator.Subtract => a - b,
            BoundBinaryOperator.Multiply => a * b,
            BoundBinaryOperator.Divide => a / b,
            _ => a % b,
        }));

    /// <summary>A float keeps single precision after every operation, as C++ does.</summary>
    public static double Round(VplType type, double number) => type == VplType.Float ? (float)number : number;

    // Any comparison with NaN is false in IEEE, so an unordered pair has no order at all.
    public static bool Less(VplType type, Value left, Value right) => CompareNumbers(left, right) is { } order && order < 0;
    public static bool LessOrEqual(VplType type, Value left, Value right) => CompareNumbers(left, right) is { } order && order <= 0;
    public static bool Greater(VplType type, Value left, Value right) => CompareNumbers(left, right) is { } order && order > 0;
    public static bool GreaterOrEqual(VplType type, Value left, Value right) => CompareNumbers(left, right) is { } order && order >= 0;

    static int? CompareNumbers(Value left, Value right) => (left, right) switch
    {
        (IntValue a, IntValue b) => a.Bits < b.Bits ? -1 : a.Bits > b.Bits ? 1 : 0,
        (FloatValue a, FloatValue b) => double.IsNaN(a.Number) || double.IsNaN(b.Number) ? null : a.Number < b.Number ? -1 : a.Number > b.Number ? 1 : 0,
        (CharValue a, CharValue b) => a.CodePoint.CompareTo(b.CodePoint),
        _ => throw new InterpreterException($"comparing {left.GetType().Name} with {right.GetType().Name}"),
    };

    /// <summary><c>compareTo</c>: a total order for floats, with -0 before 0 and NaN after everything.</summary>
    public static int CompareTotal(VplType type, Value left, Value right)
    {
        switch (left, right)
        {
            case (IntValue a, IntValue b): return a.Bits < b.Bits ? -1 : a.Bits > b.Bits ? 1 : 0;
            case (CharValue a, CharValue b): return a.CodePoint.CompareTo(b.CodePoint);
            case (StringValue a, StringValue b): return RuntimeText.CompareBytes(a.Bytes, b.Bytes);
            case (BoolValue a, BoolValue b): return a.Flag.CompareTo(b.Flag);
            case (FloatValue a, FloatValue b): return CompareFloats(a.Number, b.Number);
            default: throw new InterpreterException($"compareTo on {type}");
        }
    }

    static int CompareFloats(double value, double other)
    {
        if (value < other) return -1;
        if (value > other) return 1;
        bool valueIsNaN = double.IsNaN(value), otherIsNaN = double.IsNaN(other);
        if (valueIsNaN || otherIsNaN)
            return valueIsNaN == otherIsNaN ? 0 : valueIsNaN ? 1 : -1;
        bool valueIsNegative = double.IsNegative(value);
        return valueIsNegative == double.IsNegative(other) ? 0 : valueIsNegative ? -1 : 1;
    }

    /// <summary>A cast: wrapping between integers, saturating from floating point, checked to char and enums.</summary>
    public static Value Convert(VplType from, VplType to, Value value, string? location)
    {
        if (from == to)
            return value;
        if (value is NullValue)
        {
            if (to.IsNullableReference) return new RefValue(null);
            if (to.IsNullableValue) return new OptionalValue(null);
            if (to.IsPointer || to == VplType.Null) return NullValue.Instance;
            throw new InterpreterException($"cannot convert null to '{to}'");
        }
        if (to.IsNullableValue)
        {
            if (value is OptionalValue optional)
                return new OptionalValue(optional.Inner is null ? null : new Cell(to.NonNull, Convert(from.NonNull, to.NonNull, optional.Inner.Value, location)));
            return new OptionalValue(new Cell(to.NonNull, Convert(from, to.NonNull, value, location)));
        }
        if (to.IsNullableReference || to.IsReference)
            return value;
        if (location is not null)
            return to == VplType.Char ? ToChar(value, location) : FromValue(to, value, location);
        if (from.IsEnum && to.IsInteger)
            return new IntValue(Values.Wrap(to, ((EnumValue)value).Variant.Value));
        if (from.IsFloatingPoint && to.IsInteger)
            return new IntValue(FloatToInteger(to, ((FloatValue)value).Number));
        if (from == VplType.Double && to == VplType.Float)
            return new FloatValue(DoubleToFloat(((FloatValue)value).Number));
        if (from.IsFloatingPoint && to.IsFloatingPoint)
            return new FloatValue(Round(to, ((FloatValue)value).Number));
        if (from == VplType.Char && to.IsInteger)
            return new IntValue(Values.Wrap(to, ((CharValue)value).CodePoint));
        if (from.IsInteger && to.IsFloatingPoint)
        {
            var number = ((IntValue)value).Bits;
            return new FloatValue(to == VplType.Float ? (float)number : (double)number);
        }
        if (from.IsInteger && to.IsInteger)
            return new IntValue(Values.Wrap(to, ((IntValue)value).Bits));
        if (from == VplType.Bool || from == VplType.String || from.IsPointer)
            return value;
        throw new InterpreterException($"cannot convert '{from}' to '{to}'");
    }

    static Value ToChar(Value value, string location)
    {
        var number = ((IntValue)value).Bits;
        if (number >= 0 && number <= 0x10FFFF && (number < 0xD800 || number > 0xDFFF))
            return new CharValue((int)number);
        new Panic("integer is not a Unicode scalar value", location).Raise();
        throw new UnreachableException();
    }

    static Value FromValue(VplType to, Value value, string location)
    {
        var number = ((IntValue)value).Bits;
        bool compareUnsigned = to.Symbol!.UnderlyingType == VplType.ULong;
        bool fits = compareUnsigned ? number >= 0 : number >= long.MinValue && number <= long.MaxValue;
        if (fits)
        {
            foreach (var variant in to.Symbol!.Variants)
            {
                if (variant.Value == number)
                    return new EnumValue(to, variant, []);
            }
        }
        new Panic($"integer is not a value of enum '{to.Symbol.Name}'", location).Raise();
        throw new UnreachableException();
    }

    static Int128 FloatToInteger(VplType type, double number)
    {
        var (minimum, maximum) = Range(type);
        if (double.IsNaN(number))
            return 0;
        if (number <= (double)minimum)
            return minimum;
        if (number >= (double)maximum)
            return maximum;
        return Values.Wrap(type, (Int128)Math.Truncate(number));
    }

    static double DoubleToFloat(double number)
    {
        if (double.IsNaN(number))
            return double.NaN;
        double overflow = (double)float.MaxValue + Math.ScaleB(1.0, 103);
        if (number >= overflow) return double.PositiveInfinity;
        if (number <= -overflow) return double.NegativeInfinity;
        if (number > float.MaxValue) return float.MaxValue;
        if (number < -float.MaxValue) return -float.MaxValue;
        return (float)number;
    }

    static (Int128 Minimum, Int128 Maximum) Range(VplType type)
    {
        var (width, signed) = Values.Bits(type);
        if (!signed)
            return (0, (Int128.One << width) - 1);
        var half = Int128.One << (width - 1);
        return (-half, half - 1);
    }

    static Int128 Min(VplType type) => Range(type).Minimum;

    static bool InRange(VplType type, Int128 value)
    {
        var (minimum, maximum) = Range(type);
        return value >= minimum && value <= maximum;
    }

    static bool MulOverflows(VplType type, Int128 a, Int128 b)
    {
        var (minimum, maximum) = Range(type);
        var (_, signed) = Values.Bits(type);
        if (a == 0 || b == 0)
            return false;
        if (!signed)
            return a > maximum / b;
        if (a > 0)
            return b > 0 ? a > maximum / b : b < minimum / a;
        return b > 0 ? a < minimum / b : a < maximum / b;
    }

    static void Overflow(string? location) => new Panic("integer overflow", location).Raise();

    /// <summary>A panic, raised where the instruction says; <see cref="Panic.Raise"/> never returns.</summary>
    readonly struct Panic(string message, string? location)
    {
        public void Raise()
        {
            if (location is null)
                throw new InterpreterException($"the operation panics without a location: {message}");
            throw new VplPanic(message, location);
        }
    }
}

/// <summary>Unreachable code after a call that never returns.</summary>
public sealed class UnreachableException() : Exception("unreachable");
