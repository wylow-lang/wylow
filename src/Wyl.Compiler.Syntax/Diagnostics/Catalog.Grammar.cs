using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Diagnostics;

// Chapter 1's grammar notation: the syntax errors any production can produce.
public static partial class Catalog
{
    [Error(1, "introduction.notation.grammar")]
    public static Diagnostic Expected(Location at, string what) =>
        Make(at, $"expected {what}");

    [Error(4, "lexical.keywords")]
    public static Diagnostic ReservedKeyword(Location at, string keyword) =>
        Make(at, $"'{keyword}' is a reserved word and cannot be used");
}
