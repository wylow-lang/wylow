using System.Globalization;
using System.Text;
using Wyl.Compiler.Semantics;

namespace Wyl.Compiler.Interpret;

/// <summary>
/// The text and the checked arithmetic of the C++ runtime (<c>runtime/wyl/*.h</c>), which the
/// interpreter must reproduce exactly: the emitter's programs and the interpretation are compared.
/// </summary>
internal static class RuntimeText
{
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public static byte[] Bytes(string text) => Utf8.GetBytes(text);

    /// <summary>
    /// A value written as <c>print</c> and interpolation write it without a specifier, except that
    /// null of a nullable is <c>null</c> rather than a panic.
    /// </summary>
    public static byte[] Format(WylType type, Value value)
    {
        if (type.IsNullableValue)
            return value is OptionalValue { Inner: { } inner } ? Format(type.NonNull, inner.Value) : Bytes("null");
        if (value is NullValue or RefValue { Cell: null })
            return Bytes("null");
        return value switch
        {
            IntValue number => Bytes(number.Bits.ToString(CultureInfo.InvariantCulture)),
            BoolValue flag => Bytes(flag.Flag ? "true" : "false"),
            CharValue letter => Utf8.GetBytes(char.ConvertFromUtf32(letter.CodePoint)),
            FloatValue number => Bytes(FloatText.Text(number.Number, type.NonNull == WylType.Float)),
            StringValue text => text.Bytes,
            _ => throw new InterpreterException($"cannot format a {value.GetType().Name} of '{type}'"),
        };
    }

    /// <summary>
    /// A value written with the interpolation specifier's letter (0 for none), precision (-1 for
    /// none), and alignment (0 for none), as <c>StringBuffer</c> writes it.
    /// </summary>
    public static byte[] FormatSpecified(WylType type, Value value, int kind, int precision, int alignment, string? location = null)
    {
        byte[] text =
            type.IsNullableValue ? value is OptionalValue o ? (o.Inner is null ? Bytes("null") : FormatSpecified(type.NonNull, o.Inner.Value, kind, precision, 0, location)) : throw NotOptional(type)
            : value is NullValue ? Bytes("null")
            : kind == 0 ? Format(type, value)
            : type.NonNull.IsInteger ? FormatInteger(type.NonNull, (IntValue)value, kind, precision)
            : type.NonNull.IsFloatingPoint ? FormatFloat(type.NonNull, (FloatValue)value, kind, precision)
            : throw new InterpreterException($"format specifier '{(char)kind}' on '{type}'");
        return alignment == 0 ? text : Pad(text, alignment, location);
    }

    static InterpreterException NotOptional(WylType type) => new($"a nullable {type} is not optional");

    /// <summary>Pads the text to the alignment's width in characters, on the left when positive.</summary>
    static byte[] Pad(byte[] text, int alignment, string? location)
    {
        int characters = 0;
        foreach (var b in text)
        {
            if ((b & 0xC0) != 0x80)
                characters++;
        }
        var width = alignment < 0 ? -alignment : alignment;
        if (characters >= width)
            return text;
        // The runtime's StringBuffer refuses to grow past an int's length; allocating first would
        // otherwise crash the interpreter where the C++ build panics.
        if ((long)width - characters > int.MaxValue - text.Length)
            throw new WylPanic("string is too long", location ?? "<unknown>");
        var padding = new byte[width - characters];
        Array.Fill(padding, (byte)' ');
        return alignment > 0 ? [.. padding, .. text] : [.. text, .. padding];
    }

    static byte[] FormatInteger(WylType type, IntValue value, int kind, int precision)
    {
        var number = value.Bits;
        if (kind is 'D' or 'd')
        {
            var magnitude = (number < 0 ? -number : number).ToString(CultureInfo.InvariantCulture);
            if (precision > magnitude.Length)
                magnitude = new string('0', precision - magnitude.Length) + magnitude;
            return Bytes((number < 0 ? "-" : "") + magnitude);
        }
        var bits = Values.Unsigned(type, number);
        var digits = kind is 'B' or 'b' ? BinaryDigits(bits) : bits.ToString("x", CultureInfo.InvariantCulture);
        if (precision > digits.Length)
            digits = new string('0', precision - digits.Length) + digits;
        return Bytes(kind is 'X' ? digits.ToUpperInvariant() : digits);
    }

    static string BinaryDigits(Int128 bits)
    {
        if (bits == 0)
            return "0";
        var text = new StringBuilder();
        for (var value = bits; value > 0; value >>= 1)
            text.Insert(0, (value & 1) == 0 ? '0' : '1');
        return text.ToString();
    }

    static byte[] FormatFloat(WylType type, FloatValue value, int kind, int precision)
    {
        bool single = type == WylType.Float;
        double number = single ? (float)value.Number : value.Number;
        if (double.IsNaN(number))
            return Bytes("nan");
        if (double.IsPositiveInfinity(number))
            return Bytes("inf");
        if (double.IsNegativeInfinity(number))
            return Bytes("-inf");
        if (kind is 'F' or 'f')
        {
            int places = precision >= 0 ? precision : 2;
            return Bytes(number.ToString("F" + places.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
        }
        int digits = precision >= 0 ? precision : 6;
        var text = number.ToString("E" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return Bytes(kind == 'e' ? text.Replace("E", "e", StringComparison.Ordinal) : text);
    }

    /// <summary>
    /// The panic text of an index check, as <c>wyl::panic_index</c> writes it.
    /// </summary>
    public static string IndexMessage(Int128 index, Int128 length) =>
        $"index {index.ToString(CultureInfo.InvariantCulture)} is out of bounds for length {length.ToString(CultureInfo.InvariantCulture)}";

    public static string RangeMessage(Int128 start, Int128 count, Int128 length) =>
        $"range of {count.ToString(CultureInfo.InvariantCulture)} from {start.ToString(CultureInfo.InvariantCulture)} is out of bounds for length {length.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Byte order, which for UTF-8 is the order of the code points (<c>String::compareTo</c>).</summary>
    public static int CompareBytes(byte[] a, byte[] b)
    {
        int common = Math.Min(a.Length, b.Length);
        for (int i = 0; i < common; i++)
        {
            if (a[i] != b[i])
                return a[i] < b[i] ? -1 : 1;
        }
        return Math.Sign(a.Length.CompareTo(b.Length));
    }
}

/// <summary>
/// The shortest decimal text that reads back as the same value, in <c>std::to_chars</c>'s choice of
/// plain or exponent form, which <c>wyl::format</c> writes.
/// </summary>
internal static class FloatText
{
    public static string Text(double number, bool single)
    {
        if (double.IsNaN(number))
            return "nan";
        if (double.IsPositiveInfinity(number))
            return "inf";
        if (double.IsNegativeInfinity(number))
            return "-inf";
        // The shortest digits are the float's own, not the double that holds its value.
        var roundTrip = single ? ((float)number).ToString("R", CultureInfo.InvariantCulture) : number.ToString("R", CultureInfo.InvariantCulture);
        if (single)
            number = (float)number;
        var (sign, digits, exponent) = Parse(roundTrip);
        var text = Shortest(sign, number, digits, exponent);
        // Whole numbers read as "3.0", like Java, and non-finite values were handled above.
        return text.Contains('.') || text.Contains('e') || text.Contains('n') ? text : text + ".0";
    }

    /// <summary>
    /// The shortest of the plain and exponent forms; a tie is plain, as <c>to_chars</c> does. A
    /// whole number in the plain form is written exactly, as <c>to_chars</c> writes it: the
    /// significant digits may be fewer than its places, and it still names the whole value.
    /// </summary>
    static string Shortest(bool negative, double number, string digits, int exponent)
    {
        var scientific = Scientific(digits, exponent);
        var plain = Math.Floor(number) == number ? Whole(number) : Plain(digits, exponent);
        if (scientific.Length < plain.Length)
            return negative ? "-" + scientific : scientific;
        return negative ? "-" + plain : plain;
    }

    /// <summary>The exact digits of a whole number, which are as many as its places.</summary>
    static string Whole(double number) => Math.Abs(number).ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>The digits with a decimal point where the exponent puts it, or leading or trailing zeros.</summary>
    static string Plain(string digits, int exponent)
    {
        if (digits == "0")
            return "0";
        if (exponent >= 0)
        {
            int point = exponent + 1;
            if (point >= digits.Length)
                return digits + new string('0', point - digits.Length);
            return digits[..point] + "." + digits[point..];
        }
        return "0." + new string('0', -exponent - 1) + digits;
    }

    static string Scientific(string digits, int exponent)
    {
        if (digits == "0")
            return "0";
        var mantissa = digits.Length == 1 ? digits : digits[..1] + "." + digits[1..];
        var magnitude = exponent < 0 ? -exponent : exponent;
        var text = magnitude.ToString(CultureInfo.InvariantCulture);
        if (text.Length < 2)
            text = "0" + text;
        return $"{mantissa}e{(exponent < 0 ? '-' : '+')}{text}";
    }

    /// <summary>The sign, the significant digits, and the exponent of the first digit of a round-trip text.</summary>
    static (bool Negative, string Digits, int Exponent) Parse(string text)
    {
        bool negative = text.StartsWith('-');
        if (negative)
            text = text[1..];
        int exponent = 0;
        int e = text.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(text[(e + 1)..], CultureInfo.InvariantCulture);
            text = text[..e];
        }
        int point = text.IndexOf('.');
        int integerDigits = point < 0 ? text.Length : point;
        var digits = point < 0 ? text : text.Remove(point, 1);
        int leading = digits.Length - digits.TrimStart('0').Length;
        digits = digits.TrimStart('0');
        if (digits.Length == 0)
            return (negative, "0", 0);
        exponent += integerDigits - 1 - leading;
        digits = digits.TrimEnd('0');
        return (negative, digits, exponent);
    }
}
