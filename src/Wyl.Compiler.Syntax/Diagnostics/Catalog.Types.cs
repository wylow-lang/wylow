using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Diagnostics;

// Chapter 4, types.
public static partial class Catalog
{
    [Error(74, "types.functions")]
    public static Diagnostic RedundantFunctionTypeParentheses(Location at) =>
        Make(at, "parentheses around this function type are not needed").Note("they are needed only before '?', '[', or '*'");

    [Error(75, "types.functions")]
    public static Diagnostic FunctionTypeWithoutFn(Location at) =>
        Make(at, "function type is missing 'fn'").Help("write 'fn(int) -> int'");

    [Error(76, "types.functions")]
    public static Diagnostic FunctionTypeWithoutParentheses(Location at) =>
        Make(at, "expected '(' after 'fn'").Note("a function type's parameters are always in parentheses");

    [Error(73, "types.arrays")]
    public static Diagnostic NestedArrayCreation(Location at) =>
        Make(at, "'new' cannot create an array of arrays").Help("create the outer array, then each inner one");

    [Error(79, "conversions.contexts")]
    public static Diagnostic ImplicitConversion(Location at, string expected, string found, ConversionHelp help = ConversionHelp.None, string? nullable = null) => help switch
    {
        ConversionHelp.Cast => Make(at, $"expected '{expected}', found '{found}'").Help("use an explicit cast"),
        ConversionHelp.NullCheck => Make(at, $"expected '{expected}', found '{found}'").Help("check it for null first, or use '!'"),
        ConversionHelp.SpanInsideNullable => Make(at, $"expected '{expected}', found '{found}'")
            .Note($"a span cannot view inside nullable '{nullable}'").InsideNullable(nullable!),
        ConversionHelp.SpanOfTemporaryArray => Make(at, $"expected '{expected}', found '{found}'")
            .Note("a span can only view an array stored in a variable or field"),
        ConversionHelp.SpanOfReadOnlyArray => Make(at, $"expected '{expected}', found '{found}'")
            .Note("the array is read-only").Help("view it with a 'ReadOnlySpan'"),
        _ => Make(at, $"expected '{expected}', found '{found}'"),
    };

    [Error(81, "conversions.functions")]
    public static Diagnostic FunctionToNonFunction(Location at, bool isReference, string expected) =>
        Make(at, $"expected '{expected}', found {(isReference ? "a function" : "a lambda")}").Note($"'{expected}' is not a function type");

    [Error(82, "conversions.functions")]
    public static Diagnostic ChoiceToNonFunction(Location at, string expected) =>
        Make(at, $"expected '{expected}', found a choice of lambdas").Note($"'{expected}' is not a function type");

    [Error(83, "conversions.casts")]
    public static Diagnostic InvalidCast(Location at, string from, string to) =>
        Make(at, $"cannot cast '{from}' to '{to}'");

    [Error(80, "conversions.constants")]
    public static Diagnostic IntegerLiteralOutOfRange(Location at, string type, bool fitsUInt) => type switch
    {
        "int" when fitsUInt => Make(at, "integer literal is too large for 'int'").Help("add a 'u' suffix for a 'uint', or an 'L' suffix for a 'long'"),
        "int" => Make(at, "integer literal is too large for 'int'").Help("add an 'L' suffix for a 'long'"),
        "uint" => Make(at, "integer literal is too large for 'uint'").Help("add a 'uL' suffix for a 'ulong'"),
        _ => Make(at, $"integer literal is too large for '{type}'"),
    };

    [Error(65, "types.nullable")]
    public static Diagnostic NullableReceiver(Location at, string text) =>
        Make(at, $"'{text}' might be null").Help("use '?.' or '!' to access its members");

    [Error(68, "types.fixed-arrays")]
    public static Diagnostic IndexOutOfBounds(Location at, int index, string type) =>
        Make(at, $"index {index} is out of bounds for '{type}'");

    [Error(69, "types.fixed-arrays")]
    public static Diagnostic VoidArrayElement(Location at) =>
        Make(at, "array element cannot be a 'void' value");

    [Error(70, "types.fixed-arrays")]
    public static Diagnostic EmptyArrayType(Location at) =>
        Make(at, "cannot infer the type of an empty array").Help("use it where a fixed array type is expected");

    [Error(71, "types.fixed-arrays")]
    public static Diagnostic ArrayElementTypes(Location at, IEnumerable<string> types) =>
        Make(at, $"elements of the array have incompatible types {string.Join(", ", types.Select(t => $"'{t}'"))}");

    [Error(78, "types.defaults")]
    public static Diagnostic NoDefaultElement(Location at, string element) =>
        Make(at, $"cannot fill a new array with default values of '{element}'")
            .Note($"'{element}' has no default value").Help($"write 'new Array<{element}>(length, value)'");

    [Error(62, "types.kinds")]
    public static Diagnostic VoidNotAllowed(Location at) =>
        Make(at, "'void' cannot be used here");

    [Error(66, "types.nullable")]
    public static Diagnostic NullablePointer(Location at, string type) =>
        Make(at, $"pointer type '{type}' cannot be nullable").Note("a pointer can already be null");

    [Error(67, "types.nullable")]
    public static Diagnostic NullableView(Location at, string type) =>
        Make(at, $"'{type}' cannot be nullable").Note($"'{type}' is a 'ref struct'");

    [Error(72, "types.fixed-arrays")]
    public static Diagnostic FixedArrayLength(Location at) =>
        Make(at, "fixed array's length must fit in an 'int'");

    [Error(63, "types.syntax")]
    public static Diagnostic UnknownType(Location at, string name) =>
        Make(at, $"cannot find type '{name}'");

    [Error(64, "types.syntax")]
    public static Diagnostic FunctionNotType(Location at, string text) =>
        Make(at, $"'{text}' is a function, not a type");

    [Error(77, "types.functions")]
    public static Diagnostic ViewReturningFunctionType(Location at, string type) =>
        Make(at, $"function type cannot return '{type}'").Note("there is nothing it could view");
}
