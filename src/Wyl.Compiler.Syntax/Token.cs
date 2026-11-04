using Wyl.Compiler.Text;

namespace Wyl.Compiler.Syntax;

public enum TokenKind
{
    EndOfFile,
    Identifier,
    IntLiteral,
    FloatLiteral,
    CharLiteral,
    StringLiteral,

    VoidKeyword,
    BoolKeyword,
    SByteKeyword,
    ByteKeyword,
    ShortKeyword,
    UShortKeyword,
    IntKeyword,
    UIntKeyword,
    LongKeyword,
    ULongKeyword,
    NIntKeyword,
    NUIntKeyword,
    CharKeyword,
    FloatKeyword,
    DoubleKeyword,
    VarKeyword,
    FnKeyword,
    IfKeyword,
    ElseKeyword,
    WhileKeyword,
    ForKeyword,
    LoopKeyword,
    ReturnKeyword,
    BreakKeyword,
    ContinueKeyword,
    TrueKeyword,
    FalseKeyword,
    StructKeyword,
    PubKeyword,
    ModuleKeyword,
    PackageKeyword,

    /// <summary><c>public</c>, <c>private</c>, or <c>protected</c>, reserved so that each gets an error naming what Wylow writes instead.</summary>
    JavaAccessKeyword,
    StaticKeyword,
    MutKeyword,
    UniqueKeyword,
    EscapingKeyword,
    ThisKeyword,
    RefKeyword,
    InKeyword,
    ClassKeyword,
    InterfaceKeyword,
    ExtendsKeyword,
    ImplementsKeyword,
    NewKeyword,
    NullKeyword,
    SuperKeyword,
    IsKeyword,
    VirtualKeyword,
    OverrideKeyword,
    AbstractKeyword,
    FinalKeyword,
    EnumKeyword,
    SwitchKeyword,
    CaseKeyword,
    DefaultKeyword,
    WhenKeyword,
    TryKeyword,
    UnsafeKeyword,
    ExternKeyword,
    OperatorKeyword,
    ImportKeyword,
    Underscore,
    ReservedKeyword,

    OpenParen,
    CloseParen,
    OpenBrace,
    CloseBrace,
    OpenBracket,
    CloseBracket,
    Semicolon,
    Comma,
    Dot,
    DotDot,
    DotDotEqual,
    Colon,

    /// <summary><c>::</c>, which walks modules (spec programs.paths).</summary>
    ColonColon,
    Question,
    QuestionQuestion,
    QuestionDot,
    Arrow,
    At,

    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Ampersand,
    Pipe,
    Caret,
    Tilde,
    Bang,
    AmpersandAmpersand,
    PipePipe,
    LessLess,
    GreaterGreater,
    Less,
    Greater,
    LessEqual,
    GreaterEqual,
    EqualEqual,
    BangEqual,
    PlusPlus,
    MinusMinus,

    Assign,
    PlusAssign,
    MinusAssign,
    StarAssign,
    SlashAssign,
    PercentAssign,
    AmpersandAssign,
    PipeAssign,
    CaretAssign,
    LessLessAssign,
    GreaterGreaterAssign,
}

public readonly record struct Token(TokenKind Kind, TextSpan Span, string Text, object? Value);

public enum IntegerSuffix { None, Unsigned, Long, UnsignedLong }

public readonly record struct IntegerLiteralValue(ulong Value, IntegerSuffix Suffix);

public readonly record struct FloatLiteralValue(double Value, bool IsDouble);

/// <summary>
/// An interpolated string's text around its holes, one more than the holes, and each hole, or null
/// for a hole whose error the lexer reported.
/// </summary>
public sealed record InterpolatedText(IReadOnlyList<string> Texts, IReadOnlyList<HoleText?> Holes);

/// <summary>A hole's tokens before any format specifier, ending in <c>EndOfFile</c>, and the specifier's text after the ':'.</summary>
public sealed record HoleText(IReadOnlyList<Token> Tokens, string? Format, TextSpan FormatSpan);
