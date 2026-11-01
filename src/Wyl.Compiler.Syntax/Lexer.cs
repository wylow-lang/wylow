using System.Globalization;
using System.Text;
using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Syntax;

public sealed class Lexer(SourceText source, DiagnosticBag diagnostics)
{
    static readonly Dictionary<string, TokenKind> Keywords = new()
    {
        ["void"] = TokenKind.VoidKeyword,
        ["bool"] = TokenKind.BoolKeyword,
        ["sbyte"] = TokenKind.SByteKeyword,
        ["byte"] = TokenKind.ByteKeyword,
        ["short"] = TokenKind.ShortKeyword,
        ["ushort"] = TokenKind.UShortKeyword,
        ["int"] = TokenKind.IntKeyword,
        ["uint"] = TokenKind.UIntKeyword,
        ["long"] = TokenKind.LongKeyword,
        ["ulong"] = TokenKind.ULongKeyword,
        ["nint"] = TokenKind.NIntKeyword,
        ["nuint"] = TokenKind.NUIntKeyword,
        ["char"] = TokenKind.CharKeyword,
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
        ["pub"] = TokenKind.PubKeyword,
        ["public"] = TokenKind.JavaAccessKeyword,
        ["private"] = TokenKind.JavaAccessKeyword,
        ["protected"] = TokenKind.JavaAccessKeyword,
        ["static"] = TokenKind.StaticKeyword,
        ["mut"] = TokenKind.MutKeyword,
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
        ["enum"] = TokenKind.EnumKeyword,
        ["switch"] = TokenKind.SwitchKeyword,
        ["case"] = TokenKind.CaseKeyword,
        ["default"] = TokenKind.DefaultKeyword,
        ["try"] = TokenKind.TryKeyword,
        ["unsafe"] = TokenKind.UnsafeKeyword,
        ["extern"] = TokenKind.ExternKeyword,
        ["operator"] = TokenKind.OperatorKeyword,
        ["import"] = TokenKind.ImportKeyword,
        ["_"] = TokenKind.Underscore,
    };

    // Reserved now so that implementing them later doesn't break existing programs.
    static readonly HashSet<string> ReservedKeywords =
    [
        "const", "defer", "do", "package", "shared",
    ];

    // Longest first, so that maximal munch falls out of a linear scan.
    static readonly (string Text, TokenKind Kind)[] Punctuators =
    [
        ("<<=", TokenKind.LessLessAssign), (">>=", TokenKind.GreaterGreaterAssign), ("..=", TokenKind.DotDotEqual),
        ("??", TokenKind.QuestionQuestion), ("..", TokenKind.DotDot), ("&&", TokenKind.AmpersandAmpersand), ("||", TokenKind.PipePipe), ("==", TokenKind.EqualEqual),
        ("!=", TokenKind.BangEqual), ("<=", TokenKind.LessEqual), (">=", TokenKind.GreaterEqual),
        ("<<", TokenKind.LessLess), (">>", TokenKind.GreaterGreater), ("++", TokenKind.PlusPlus),
        ("--", TokenKind.MinusMinus), ("->", TokenKind.Arrow), ("+=", TokenKind.PlusAssign), ("-=", TokenKind.MinusAssign),
        ("*=", TokenKind.StarAssign), ("/=", TokenKind.SlashAssign), ("%=", TokenKind.PercentAssign),
        ("&=", TokenKind.AmpersandAssign), ("|=", TokenKind.PipeAssign), ("^=", TokenKind.CaretAssign), ("::", TokenKind.ColonColon),
        ("(", TokenKind.OpenParen), (")", TokenKind.CloseParen), ("{", TokenKind.OpenBrace),
        ("}", TokenKind.CloseBrace), ("[", TokenKind.OpenBracket), ("]", TokenKind.CloseBracket),
        (";", TokenKind.Semicolon), (",", TokenKind.Comma), (".", TokenKind.Dot), (":", TokenKind.Colon),
        ("?", TokenKind.Question), ("+", TokenKind.Plus), ("-", TokenKind.Minus), ("*", TokenKind.Star),
        ("/", TokenKind.Slash), ("%", TokenKind.Percent), ("&", TokenKind.Ampersand), ("|", TokenKind.Pipe),
        ("^", TokenKind.Caret), ("~", TokenKind.Tilde), ("!", TokenKind.Bang), ("<", TokenKind.Less),
        (">", TokenKind.Greater), ("=", TokenKind.Assign), ("@", TokenKind.At),
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
            return LexString(interpolated: false);
        if (c == '$' && Peek(1) == '"')
            return LexString(interpolated: true);
        if (c == '\'')
            return LexChar();
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

        var integerSuffix = IntegerSuffix.None;
        bool isDouble = false;
        switch (suffix)
        {
            case "":
                break;
            case "L" or "l" when !isFloat:
                integerSuffix = IntegerSuffix.Long;
                break;
            case "u" or "U" when !isFloat:
                integerSuffix = IntegerSuffix.Unsigned;
                break;
            case "uL" or "UL" or "ul" or "Ul" when !isFloat:
                integerSuffix = IntegerSuffix.UnsignedLong;
                break;
            case "f" or "F" when radix == 10:
                isFloat = true;
                break;
            case "d" or "D" when radix == 10:
                isFloat = true;
                isDouble = true;
                break;
            default:
                Error(span, $"invalid suffix '{suffix}' on number literal");
                break;
        }

        if (digits.Length == 0)
        {
            Error(span, "number literal has no digits");
            return Make(TokenKind.IntLiteral, start, new IntegerLiteralValue(0, integerSuffix));
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
        return Make(TokenKind.IntLiteral, start, new IntegerLiteralValue(integer, integerSuffix));
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

    /// <summary>
    /// A string literal, whose value is its text, or for a <c>$</c> string with holes an
    /// <see cref="InterpolatedText"/>.
    /// </summary>
    Token LexString(bool interpolated)
    {
        int start = position;
        position += interpolated ? 2 : 1;
        var value = new StringBuilder();
        var texts = new List<string>();
        var holes = new List<HoleText?>();
        while (true)
        {
            if (AtLineEnd)
            {
                Error(TextSpan.FromBounds(start, position), "unterminated string literal");
                break;
            }
            if (Current == '"')
            {
                position++;
                break;
            }
            if (interpolated && Current is '{' or '}' && Peek(1) == Current)
            {
                value.Append(Current);
                position += 2;
            }
            else if (interpolated && Current == '{')
            {
                texts.Add(value.ToString());
                value.Clear();
                holes.Add(LexHole());
            }
            else if (interpolated && Current == '}')
            {
                Error(new TextSpan(position, 1), "a '}' in an interpolated string is written '}}'");
                position++;
            }
            else if (LexCharacter() is { } character)
            {
                value.Append(character.ToString());
            }
        }
        if (holes.Count == 0)
            return Make(TokenKind.StringLiteral, start, value.ToString());
        texts.Add(value.ToString());
        return Make(TokenKind.StringLiteral, start, new InterpolatedText(texts, holes));
    }

    /// <summary>
    /// A hole in an interpolated string, from its '{' to its matching '}': the tokens the parser
    /// parses as a value and alignment, and the text of a format specifier after a ':'. Null for a
    /// hole with an error already reported. A hole ends with its line, like the string around it.
    /// </summary>
    HoleText? LexHole()
    {
        position++;
        var tokens = new List<Token>();
        int depth = 0;
        bool conditional = false;
        string? format = null;
        var formatSpan = default(TextSpan);
        while (true)
        {
            while (Current is ' ' or '\t')
                position++;
            // The string reports being unterminated.
            if (AtLineEnd || (Current == '/' && Peek(1) == '/'))
                return null;
            if (depth == 0 && Current == '}')
                break;
            if (LexToken() is not { } token)
                continue;
            if (token.Kind is TokenKind.OpenParen or TokenKind.OpenBracket or TokenKind.OpenBrace)
            {
                depth++;
            }
            else if (token.Kind is TokenKind.CloseParen or TokenKind.CloseBracket or TokenKind.CloseBrace)
            {
                depth = Math.Max(depth - 1, 0);
            }
            else if (depth == 0 && token.Kind == TokenKind.Question)
            {
                conditional = true;
            }
            else if (depth == 0 && token.Kind == TokenKind.Colon)
            {
                int formatStart = position;
                while (!AtLineEnd && Current is not ('}' or '"'))
                    position++;
                if (conditional)
                {
                    Error(token.Span, "a conditional expression in an interpolated string must be parenthesized, since ':' starts a format specifier");
                    if (Current == '}')
                        position++;
                    return null;
                }
                // The string reports being unterminated.
                if (Current != '}')
                    return null;
                formatSpan = TextSpan.FromBounds(formatStart, position);
                format = text[formatStart..position];
                break;
            }
            tokens.Add(token);
        }
        tokens.Add(new Token(TokenKind.EndOfFile, new TextSpan(format is null ? position : formatSpan.Start - 1, 0), "", null));
        position++;
        return new HoleText(tokens, format, formatSpan);
    }

    Token LexChar()
    {
        int start = position;
        position++;
        Rune? value = null;
        bool reported = false;
        if (Current == '\'')
        {
            Error(TextSpan.FromBounds(start, position + 1), "a character literal must hold a character");
            reported = true;
        }
        else if (!AtLineEnd)
        {
            value = LexCharacter();
            reported = value is null;
        }

        if (Current == '\'')
        {
            position++;
        }
        else
        {
            int afterCharacter = position;
            while (!AtLineEnd && Current != '\'')
                position++;
            if (AtLineEnd)
            {
                // Without a closing quote, the rest of the line is more likely code than characters.
                position = afterCharacter;
                Error(TextSpan.FromBounds(start, position), "unterminated character literal");
            }
            else
            {
                position++;
                // The rest of a malformed escape isn't another character.
                if (!reported)
                    Error(TextSpan.FromBounds(start, position), "a character literal holds exactly one character; use a string for more");
            }
        }
        return Make(TokenKind.CharLiteral, start, value ?? new Rune(0));
    }

    bool AtLineEnd => position >= text.Length || Current is '\n' or '\r';

    /// <summary>One character of a string or character literal, or null after reporting an invalid one.</summary>
    Rune? LexCharacter()
    {
        int start = position;
        if (Current != '\\')
        {
            // An unpaired surrogate decodes to U+FFFD, as reading a file already makes it.
            Rune.DecodeFromUtf16(text.AsSpan(position), out var rune, out int length);
            position += length;
            return rune;
        }

        if (Peek(1) == 'u')
            return LexUnicodeEscape();
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
        position += 2;
        if (escaped is null)
            Error(new TextSpan(start, 2), $"unknown escape sequence '\\{text[start + 1]}'");
        return escaped is { } c ? new Rune(c) : null;
    }

    /// <summary><c>\u{1F600}</c>: a code point in hex, with 1 to 6 digits.</summary>
    Rune? LexUnicodeEscape()
    {
        int start = position;
        position += 2;
        bool braced = Current == '{';
        int digitsStart = position + 1;
        if (braced)
        {
            position++;
            while (DigitValue(Current) < 16)
                position++;
        }
        int digits = position - digitsStart;
        if (!braced || Current != '}' || digits is < 1 or > 6)
        {
            Error(TextSpan.FromBounds(start, position), "'\\u' must be followed by 1 to 6 hex digits in braces, as in '\\u{1F600}'");
            return null;
        }
        position++;
        int value = int.Parse(text.AsSpan(digitsStart, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (!Rune.IsValid(value))
        {
            Error(TextSpan.FromBounds(start, position), $"'\\u{{{text[digitsStart..(digitsStart + digits)]}}}' is not a Unicode scalar value");
            return null;
        }
        return new Rune(value);
    }
}
