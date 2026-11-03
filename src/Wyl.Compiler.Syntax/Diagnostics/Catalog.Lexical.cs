using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Diagnostics;

// Chapter 2, lexical structure.
public static partial class Catalog
{
    [Error(3, "lexical.comments")]
    public static Diagnostic UnclosedBlockComment(Location at) =>
        Make(at, "block comment is not closed");

    [Error(2, "lexical.tokens")]
    public static Diagnostic UnexpectedCharacter(Location at, string character) =>
        Make(at, $"character '{character}' does not start a token");

    [Error(5, "lexical.literals")]
    public static Diagnostic MisplacedDigitSeparator(Location at) =>
        Make(at, "'_' in a number literal must be between digits");

    [Error(6, "lexical.literals")]
    public static Diagnostic UnknownNumberSuffix(Location at, string suffix) =>
        Make(at, $"'{suffix}' is not a number literal suffix");

    [Error(7, "lexical.literals.integer")]
    public static Diagnostic NumberWithoutDigits(Location at) =>
        Make(at, "number literal has no digits");

    [Error(8, "lexical.literals.integer")]
    public static Diagnostic IntegerLiteralTooLarge(Location at) =>
        Make(at, "integer literal does not fit in 64 bits");

    [Error(9, "lexical.literals.float")]
    public static Diagnostic FloatLiteralTooLarge(Location at, bool isDouble) => isDouble
        ? Make(at, "literal is too large for 'double'")
        : Make(at, "literal is too large for 'float'").Help("add a 'd' suffix to make it a 'double'");

    [Error(10, "lexical.literals.char")]
    public static Diagnostic EmptyCharacterLiteral(Location at) =>
        Make(at, "character literal is empty");

    [Error(11, "lexical.literals.char")]
    public static Diagnostic UnclosedCharacterLiteral(Location at) =>
        Make(at, "character literal is not closed");

    [Error(12, "lexical.literals.char")]
    public static Diagnostic CharacterLiteralTooLong(Location at) =>
        Make(at, "character literal holds more than one character").Help("use a string literal");

    [Error(13, "lexical.literals.string")]
    public static Diagnostic UnclosedStringLiteral(Location at) =>
        Make(at, "string literal is not closed");

    [Error(14, "lexical.literals.escapes")]
    public static Diagnostic UnknownEscape(Location at, string escape) =>
        Make(at, $"'{escape}' is not an escape sequence");

    [Error(15, "lexical.literals.escapes")]
    public static Diagnostic MalformedUnicodeEscape(Location at) =>
        Make(at, "'\\u' is not followed by 1 to 6 hex digits in braces").Help("write it as '\\u{1F600}'");

    [Error(16, "lexical.literals.escapes")]
    public static Diagnostic NotUnicodeScalar(Location at, string escape) =>
        Make(at, $"'{escape}' is not a Unicode scalar value");

    [Error(17, "lexical.literals.interpolated")]
    public static Diagnostic UnescapedCloseBrace(Location at) =>
        Make(at, "'}' in an interpolated string is not doubled").Help("write '}}'");

    [Error(435, "lexical.literals.interpolated")]
    public static Diagnostic UnclosedHole(Location at) =>
        Make(at, "'{' in an interpolated string is not closed").Help("write '}' after the format specifier");

    [Error(18, "lexical.literals.interpolated")]
    public static Diagnostic UnparenthesizedConditional(Location at) =>
        Make(at, "conditional expression in an interpolated string is not parenthesized")
            .Note("':' starts a format specifier");
}
