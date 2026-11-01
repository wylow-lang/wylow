namespace Wyl.Compiler.Syntax;

public static class SyntaxFacts
{
    /// <summary>The name an indexer is declared under, which no method can have.</summary>
    public const string IndexerName = "operator []";

    public const int RelationalPrecedence = 7;

    public static int BinaryPrecedence(TokenKind kind) => kind switch
    {
        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => 10,
        TokenKind.Plus or TokenKind.Minus => 9,
        TokenKind.LessLess or TokenKind.GreaterGreater => 8,
        TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual => RelationalPrecedence,
        TokenKind.EqualEqual or TokenKind.BangEqual => 6,
        TokenKind.Ampersand => 5,
        TokenKind.Caret => 4,
        TokenKind.Pipe => 3,
        TokenKind.AmpersandAmpersand => 2,
        TokenKind.PipePipe => 1,
        _ => 0,
    };

    public static bool IsAssignmentOperator(TokenKind kind) =>
        kind is TokenKind.Assign or TokenKind.PlusAssign or TokenKind.MinusAssign or TokenKind.StarAssign
            or TokenKind.SlashAssign or TokenKind.PercentAssign or TokenKind.AmpersandAssign
            or TokenKind.PipeAssign or TokenKind.CaretAssign or TokenKind.LessLessAssign
            or TokenKind.GreaterGreaterAssign;

    public static TokenKind CompoundToBinary(TokenKind kind) => kind switch
    {
        TokenKind.PlusAssign => TokenKind.Plus,
        TokenKind.MinusAssign => TokenKind.Minus,
        TokenKind.StarAssign => TokenKind.Star,
        TokenKind.SlashAssign => TokenKind.Slash,
        TokenKind.PercentAssign => TokenKind.Percent,
        TokenKind.AmpersandAssign => TokenKind.Ampersand,
        TokenKind.PipeAssign => TokenKind.Pipe,
        TokenKind.CaretAssign => TokenKind.Caret,
        TokenKind.LessLessAssign => TokenKind.LessLess,
        TokenKind.GreaterGreaterAssign => TokenKind.GreaterGreater,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static bool IsPrimitiveTypeKeyword(TokenKind kind) =>
        kind is TokenKind.VoidKeyword or TokenKind.BoolKeyword or TokenKind.SByteKeyword or TokenKind.ByteKeyword
            or TokenKind.ShortKeyword or TokenKind.UShortKeyword or TokenKind.IntKeyword or TokenKind.UIntKeyword
            or TokenKind.LongKeyword or TokenKind.ULongKeyword or TokenKind.NIntKeyword or TokenKind.NUIntKeyword
            or TokenKind.CharKeyword or TokenKind.FloatKeyword or TokenKind.DoubleKeyword;

    public static bool IsVisibilityKeyword(TokenKind kind) => kind is TokenKind.PubKeyword or TokenKind.JavaAccessKeyword;

    /// <summary>The visibility as written; a file-private declaration writes none.</summary>
    public static string VisibilityText(Visibility visibility) => visibility switch
    {
        Visibility.Module => "pub(module)",
        Visibility.Package => "pub(package)",
        Visibility.Public => "pub",
        _ => "",
    };

    public static string KindName(TypeKind kind) => kind switch
    {
        TypeKind.Struct => "struct",
        TypeKind.Class => "class",
        TypeKind.Enum => "enum",
        _ => "interface",
    };

    public static string KindNameWithArticle(TypeKind kind) => (kind is TypeKind.Enum or TypeKind.Interface ? "an " : "a ") + KindName(kind);

    /// <summary>
    /// Includes <c>fn</c>, which starts a function type. A '(' starts a type only before <c>fn</c>,
    /// grouping a function type for a suffix (the parser's <c>StartsType</c>).
    /// </summary>
    public static bool IsTypeStart(TokenKind kind) => IsPrimitiveTypeKeyword(kind) || kind is TokenKind.Identifier or TokenKind.FnKeyword;

    public static string GetText(TokenKind kind) => kind switch
    {
        TokenKind.Plus => "+",
        TokenKind.Minus => "-",
        TokenKind.Star => "*",
        TokenKind.Slash => "/",
        TokenKind.Percent => "%",
        TokenKind.Ampersand => "&",
        TokenKind.Pipe => "|",
        TokenKind.Caret => "^",
        TokenKind.Tilde => "~",
        TokenKind.Bang => "!",
        TokenKind.AmpersandAmpersand => "&&",
        TokenKind.PipePipe => "||",
        TokenKind.LessLess => "<<",
        TokenKind.GreaterGreater => ">>",
        TokenKind.Less => "<",
        TokenKind.Greater => ">",
        TokenKind.LessEqual => "<=",
        TokenKind.GreaterEqual => ">=",
        TokenKind.EqualEqual => "==",
        TokenKind.BangEqual => "!=",
        TokenKind.QuestionQuestion => "??",
        _ => kind.ToString(),
    };
}
