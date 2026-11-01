using System.Globalization;
using System.Text;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.CodeGen;

public sealed partial class CppEmitter
{
    // An operation the IR says may panic is given the location it panics at, and does the checked thing.

    static string UnaryText(BoundUnaryOperator op, string operand, string? location) => op switch
    {
        BoundUnaryOperator.Negate when location is not null => $"wyl::neg({operand}, {location})",
        BoundUnaryOperator.Negate => $"(-{operand})",
        BoundUnaryOperator.LogicalNot => $"(!{operand})",
        BoundUnaryOperator.BitwiseNot => $"wyl::bnot({operand})",
        _ => throw new InvalidOperationException($"unexpected unary operator {op}"),
    };

    /// <summary>An operator applied to operands of <paramref name="operandType"/>.</summary>
    static string BinaryText(BoundBinaryOperator op, WylType operandType, WylType type, string left, string right, string? location)
    {
        if (operandType.IsReference || operandType == WylType.Null)
            return $"(wyl::identity({left}) {(op == BoundBinaryOperator.Equal ? "==" : "!=")} wyl::identity({right}))";
        if (location is not null)
        {
            var name = op switch
            {
                BoundBinaryOperator.Add => "add",
                BoundBinaryOperator.Subtract => "sub",
                BoundBinaryOperator.Multiply => "mul",
                BoundBinaryOperator.Divide => "div",
                BoundBinaryOperator.Remainder => "rem",
                _ => throw new InvalidOperationException($"unexpected checked operator {op}"),
            };
            return $"wyl::{name}({left}, {right}, {location})";
        }
        if (operandType.IsInteger && op is BoundBinaryOperator.ShiftLeft or BoundBinaryOperator.ShiftRight)
            return $"wyl::{(op == BoundBinaryOperator.ShiftLeft ? "shl" : "shr")}({left}, {right})";
        if (operandType.IsFloatingPoint && op == BoundBinaryOperator.Remainder)
            return $"std::fmod({left}, {right})";
        if (operandType.IsFloatingPoint && op == BoundBinaryOperator.Divide)
            return $"wyl::fdiv({left}, {right})";

        var cppOperator = op switch
        {
            BoundBinaryOperator.Add => "+",
            BoundBinaryOperator.Subtract => "-",
            BoundBinaryOperator.Multiply => "*",
            BoundBinaryOperator.Divide => "/",
            BoundBinaryOperator.BitwiseAnd => "&",
            BoundBinaryOperator.BitwiseOr => "|",
            BoundBinaryOperator.BitwiseXor => "^",
            BoundBinaryOperator.Equal => "==",
            BoundBinaryOperator.NotEqual => "!=",
            BoundBinaryOperator.Less => "<",
            BoundBinaryOperator.LessOrEqual => "<=",
            BoundBinaryOperator.Greater => ">",
            BoundBinaryOperator.GreaterOrEqual => ">=",
            _ => throw new InvalidOperationException($"unexpected binary operator {op}"),
        };

        // C++ promotes bitwise operands to int; cast back so overloads see the Wylow type.
        if (op is BoundBinaryOperator.BitwiseAnd or BoundBinaryOperator.BitwiseOr or BoundBinaryOperator.BitwiseXor)
            return $"static_cast<{CppType(type)}>({left} {cppOperator} {right})";
        return $"({left} {cppOperator} {right})";
    }

    /// <summary>The receiver and member operator. Calls go through the declaring type; see <c>wyl::as</c>.</summary>
    string ReceiverText(WylType receiverType, MethodReference method, string receiver)
    {
        var declaring = ObjectType(method.Owner!);
        if (receiverType.IsTypeParameter)
            return $"wyl::bound<{declaring}>({receiver}).";
        if (receiverType.IsReference)
            return receiverType.NonNull.Symbol == method.Owner!.Symbol ? $"{receiver}->" : $"wyl::as<{declaring}>({receiver})->";
        return receiver + ".";
    }

    string ThisReceiverText(MethodReference method, bool isSuperCall)
    {
        var declaring = ObjectType(method.Owner!);
        if (isSuperCall)
            return $"this->{declaring}::";
        if (method.Owner!.Symbol == currentType)
            return "this->";
        return $"static_cast<{declaring}*>(this)->";
    }

    /// <summary>A conversion of a value.</summary>
    string ConversionText(WylType from, WylType to, string operand, string? location)
    {
        if (location is not null)
            return to.IsEnum ? $"{ObjectType(to)}::from_value({operand}, {location})" : $"wyl::to_char({operand}, {location})";
        if (to.IsNullableValue)
            return $"{CppType(to)}({operand})";
        if (to.IsReference)
            return CppType(from) == CppType(to) ? operand : $"{CppType(to)}({operand})";
        if (stdlib.IsWeak(to))
            return $"{CppType(to)}({operand})";
        if (from.IsEnum)
            return $"static_cast<{CppType(to)}>({operand}.tag)";
        if (from.IsFloatingPoint && to.IsInteger)
            return $"wyl::f2i<{CppType(to)}>({operand})";
        if (from == WylType.Double && to == WylType.Float)
            return $"wyl::d2f({operand})";
        return $"static_cast<{CppType(to)}>({operand})";
    }

    static string Literal(BoundLiteral literal) => Literal(literal.Type, literal.Value);

    static string Literal(WylType type, object value) => value switch
    {
        bool b => b ? "true" : "false",
        int i when i == int.MinValue => "INT32_MIN",
        int i => i < 0 ? $"({i})" : i.ToString(CultureInfo.InvariantCulture),
        long l when l == long.MinValue => "INT64_MIN",
        long l => l < 0 ? $"(-INT64_C({-l}))" : $"INT64_C({l})",
        uint u => $"UINT32_C({u})",
        ulong u => $"UINT64_C({u})",
        // Typed, so that templates like wyl::add deduce the Wylow type rather than int.
        sbyte or byte or short or ushort or nint or nuint => $"{CppType(type)}{{{System.Convert.ToString(value, CultureInfo.InvariantCulture)}}}",
        Rune r => $"char32_t{{0x{r.Value:X}}}",
        float f => FloatText(f.ToString("R", CultureInfo.InvariantCulture)) + "f",
        double d => FloatText(d.ToString("R", CultureInfo.InvariantCulture)),
        string s => $"wyl::String{{{CppStringLiteral(s)}, {Encoding.UTF8.GetByteCount(s)}}}",
        _ => throw new InvalidOperationException($"unexpected literal {value.GetType().Name}"),
    };

    static string FloatText(string text) => text.Contains('.') || text.Contains('E') ? text : text + ".0";

    const string CallerLocationParameter = "wyl_location";

    static string SourceLocation(Location location) => CppStringLiteral(location.PanicText);

    static string CppStringLiteral(string value)
    {
        var result = new StringBuilder("\"");
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            switch (b)
            {
                case (byte)'"':
                    result.Append("\\\"");
                    break;
                case (byte)'\\':
                    result.Append("\\\\");
                    break;
                case >= 0x20 and < 0x7F:
                    result.Append((char)b);
                    break;
                default:
                    // Octal escapes stop after three digits; hex escapes would swallow following hex characters.
                    result.Append('\\').Append(System.Convert.ToString(b, 8).PadLeft(3, '0'));
                    break;
            }
        }
        return result.Append('"').ToString();
    }
}
