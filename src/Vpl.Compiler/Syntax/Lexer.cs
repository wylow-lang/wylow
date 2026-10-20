using System.Globalization;
using System.Text;
using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Syntax;

public sealed class Lexer(SourceText source, DiagnosticBag diagnostics)
{
    static readonly Dictionary<string, TokenKind> Keywords = new()
    {
        ["void"] = TokenKind.VoidKeyword,
        ["bool"] = TokenKind.BoolKeyword,
        ["int"] = TokenKind.IntKeyword,
        ["long"] = TokenKind.LongKeyword,
        ["float"] = TokenKind.FloatKeyword,
        ["double"] = TokenKind.DoubleKeyword,
        ["var"] = TokenKind.VarKeyword,
        ["if"] = TokenKind.IfKeyword,
        ["else"] = TokenKind.ElseKeyword,
        ["while"] = TokenKind.WhileKeyword,
        ["for"] = TokenKind.ForKeyword,
        ["return"] = TokenKind.ReturnKeyword,
        ["break"] = TokenKind.BreakKeyword,
        ["continue"] = TokenKind.ContinueKeyword,
        ["true"] = TokenKind.TrueKeyword,
        ["false"] = TokenKind.FalseKeyword,
        ["struct"] = TokenKind.StructKeyword,
        ["public"] = TokenKind.PublicKeyword,
        ["private"] = TokenKind.PrivateKeyword,
        ["protected"] = TokenKind.ProtectedKeyword,
        ["static"] = TokenKind.StaticKeyword,
        ["readonly"] = TokenKind.ReadonlyKeyword,
        ["this"] = TokenKind.ThisKeyword,
        ["ref"] = TokenKind.RefKeyword,
        ["in"] = TokenKind.InKeyword,
        ["class"] = TokenKind.ClassKeyword,
        ["interface"] = TokenKind.InterfaceKeyword,
        ["extends"] = TokenKind.ExtendsKeyword,
        ["implements"] = TokenKind.ImplementsKeyword,
        ["new"] = TokenKind.NewKeyword,
        ["null"] = TokenKind.NullKeyword,
        ["super"] = TokenKind.SuperKeyword,
        ["is"] = TokenKind.IsKeyword,
        ["virtual"] = TokenKind.VirtualKeyword,
        ["override"] = TokenKind.OverrideKeyword,
        ["abstract"] = TokenKind.AbstractKeyword,
        ["final"] = TokenKind.FinalKeyword,
        ["weak"] = TokenKind.WeakKeyword,
    };

    // Reserved now so that implementing them later doesn't break existing programs.
    static readonly HashSet<string> ReservedKeywords =
    [
        "byte", "case", "char", "const", "default", "defer", "do", "enum", "extern", "import", "nint",
        "nuint", "operator", "package", "sbyte", "shared", "short", "switch", "try", "uint", "ulong",
        "unsafe", "ushort",
    ];

    // Longest first, so that maximal munch falls out of a linear scan.
    static readonly (string Text, TokenKind Kind)[] Punctuators =
    [
        ("<<=", TokenKind.LessLessAssign), (">>=", TokenKind.GreaterGreaterAssign),
        ("??", TokenKind.QuestionQuestion), ("&&", TokenKind.AmpersandAmpersand), ("||", TokenKind.PipePipe), ("==", TokenKind.EqualEqual),
        ("!=", TokenKind.BangEqual), ("<=", TokenKind.LessEqual), (">=", TokenKind.GreaterEqual),
        ("<<", TokenKind.LessLess), (">>", TokenKind.GreaterGreater), ("++", TokenKind.PlusPlus),
        ("--", TokenKind.MinusMinus), ("+=", TokenKind.PlusAssign), ("-=", TokenKind.MinusAssign),
        ("*=", TokenKind.StarAssign), ("/=", TokenKind.SlashAssign), ("%=", TokenKind.PercentAssign),
        ("&=", TokenKind.AmpersandAssign), ("|=", TokenKind.PipeAssign), ("^=", TokenKind.CaretAssign),
        ("(", TokenKind.OpenParen), (")", TokenKind.CloseParen), ("{", TokenKind.OpenBrace),
        ("}", TokenKind.CloseBrace), ("[", TokenKind.OpenBracket), ("]", TokenKind.CloseBracket),
        (";", TokenKind.Semicolon), (",", TokenKind.Comma), (".", TokenKind.Dot), (":", TokenKind.Colon),
        ("?", TokenKind.Question), ("+", TokenKind.Plus), ("-", TokenKind.Minus), ("*", TokenKind.Star),
        ("/", TokenKind.Slash), ("%", TokenKind.Percent), ("&", TokenKind.Ampersand), ("|", TokenKind.Pipe),
        ("^", TokenKind.Caret), ("~", TokenKind.Tilde), ("!", TokenKind.Bang), ("<", TokenKind.Less),
        (">", TokenKind.Greater), ("=", TokenKind.Assign),
    ];

    readonly string text = source.Text;
    int position;

    public List<Token> Lex()
    {
        var tokens = new List<Token>();
        while (true)
        {
            SkipTrivia();
            if (position >= text.Length)
            {
                tokens.Add(new Token(TokenKind.EndOfFile, new TextSpan(text.Length, 0), "", null));
                return tokens;
            }
            if (LexToken() is { } token)
                tokens.Add(token);
        }
    }

    char Current => Peek(0);
    char Peek(int offset) => position + offset < text.Length ? text[position + offset] : '\0';

    void Error(TextSpan span, string message) => diagnostics.Error(new Location(source, span), message);

    Token Make(TokenKind kind, int start, object? value = null) =>
        new(kind, TextSpan.FromBounds(start, position), text[start..position], value);

    void SkipTrivia()
    {
        while (position < text.Length)
        {
            if (char.IsWhiteSpace(Current))
            {
                position++;
            }
            else if (Current == '/' && Peek(1) == '/')
            {
                while (position < text.Length && Current != '\n')
                    position++;
            }
            else if (Current == '/' && Peek(1) == '*')
            {
                int end = text.IndexOf("*/", position + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    Error(new TextSpan(position, 2), "unterminated block comment");
                    position = text.Length;
                }
                else
                {
                    position = end + 2;
                }
            }
            else
            {
                return;
            }
        }
    }

    Token? LexToken()
    {
        int start = position;
        char c = Current;
        if (char.IsAsciiDigit(c))
            return LexNumber();
        if (char.IsAsciiLetter(c) || c == '_')
            return LexIdentifierOrKeyword();
        if (c == '"')
            return LexString();
        // Like C#, `c ?.5 : x` stays a conditional.
        if (c == '?' && Peek(1) == '.' && !char.IsAsciiDigit(Peek(2)))
        {
            position += 2;
            return Make(TokenKind.QuestionDot, start);
        }

        foreach (var (punctuator, kind) in Punctuators)
        {
            if (string.CompareOrdinal(text, position, punctuator, 0, punctuator.Length) == 0)
            {
                position += punctuator.Length;
                return Make(kind, start);
            }
        }

        position++;
        Error(new TextSpan(start, 1), $"unexpected character '{c}'");
        return null;
    }

    Token LexIdentifierOrKeyword()
    {
        int start = position;
        while (char.IsAsciiLetterOrDigit(Current) || Current == '_')
            position++;

        var word = text[start..position];
        if (Keywords.TryGetValue(word, out var kind))
            return Make(kind, start);
        return Make(ReservedKeywords.Contains(word) ? TokenKind.ReservedKeyword : TokenKind.Identifier, start);
    }

    Token LexNumber()
    {
        int start = position;
        int radix = 10;
        if (Current == '0' && Peek(1) is 'x' or 'X')
        {
            radix = 16;
            position += 2;
        }
        else if (Current == '0' && Peek(1) is 'b' or 'B')
        {
            radix = 2;
            position += 2;
        }

        int digitsStart = position;
        while (DigitValue(Current) < radix || Current == '_')
            position++;

        bool isFloat = false;
        if (radix == 10)
        {
            if (Current == '.' && char.IsAsciiDigit(Peek(1)))
            {
                isFloat = true;
                position++;
                while (char.IsAsciiDigit(Current) || Current == '_')
                    position++;
            }
            if (Current is 'e' or 'E' && (char.IsAsciiDigit(Peek(1)) || (Peek(1) is '+' or '-' && char.IsAsciiDigit(Peek(2)))))
            {
                isFloat = true;
                position += 2;
                while (char.IsAsciiDigit(Current))
                    position++;
            }
        }

        string raw = text[digitsStart..position];
        string digits = raw.Replace("_", "");

        int suffixStart = position;
        while (char.IsAsciiLetterOrDigit(Current) || Current == '_')
            position++;
        string suffix = text[suffixStart..position];
        var span = TextSpan.FromBounds(start, position);

        if (raw.StartsWith('_') || raw.EndsWith('_') || raw.Contains("_.") || raw.Contains("._"))
            Error(span, "misplaced '_' in number literal");

        bool isLong = false, isDouble = false;
        switch (suffix)
        {
            case "":
                break;
            case "L" or "l" when !isFloat:
                isLong = true;
                break;
            case "f" or "F" when radix == 10:
                isFloat = true;
                break;
            case "d" or "D" when radix == 10:
                isFloat = true;
                isDouble = true;
                break;
            case "u" or "U" or "uL" or "UL" when !isFloat:
                Error(span, "unsigned integer types are not supported yet");
                break;
            default:
                Error(span, $"invalid suffix '{suffix}' on number literal");
                break;
        }

        if (digits.Length == 0)
        {
            Error(span, "number literal has no digits");
            return Make(TokenKind.IntLiteral, start, new IntegerLiteralValue(0, isLong));
        }

        if (isFloat)
        {
            double value;
            if (isDouble)
            {
                value = double.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (double.IsInfinity(value))
                    Error(span, "double literal is out of range");
            }
            else
            {
                float f = float.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (float.IsInfinity(f))
                    Error(span, "float literal is out of range; add a 'd' suffix to make it a 'double'");
                value = f;
            }
            return Make(TokenKind.FloatLiteral, start, new FloatLiteralValue(value, isDouble));
        }

        if (!TryParseInteger(digits, radix, out ulong integer))
            Error(span, "integer literal is too large");
        return Make(TokenKind.IntLiteral, start, new IntegerLiteralValue(integer, isLong));
    }

    static int DigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => int.MaxValue,
    };

    static bool TryParseInteger(string digits, int radix, out ulong value)
    {
        value = 0;
        foreach (char c in digits)
        {
            ulong digit = (ulong)DigitValue(c);
            if (value > (ulong.MaxValue - digit) / (ulong)radix)
                return false;
            value = value * (ulong)radix + digit;
        }
        return true;
    }

    Token LexString()
    {
        int start = position;
        position++;
        var value = new StringBuilder();
        while (true)
        {
            if (position >= text.Length || Current is '\n' or '\r')
            {
                Error(TextSpan.FromBounds(start, position), "unterminated string literal");
                break;
            }
            if (Current == '"')
            {
                position++;
                break;
            }
            if (Current == '\\')
            {
                char? escaped = Peek(1) switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '0' => '\0',
                    '\\' => '\\',
                    '"' => '"',
                    '\'' => '\'',
                    _ => null,
                };
                if (escaped is null)
                    Error(new TextSpan(position, 2), $"unknown escape sequence '\\{Peek(1)}'");
                else
                    value.Append(escaped.Value);
                position += 2;
                continue;
            }
            value.Append(Current);
            position++;
        }
        return Make(TokenKind.StringLiteral, start, value.ToString());
    }
}
