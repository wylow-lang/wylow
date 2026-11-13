using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Diagnostics;

// Chapters 20 to 22: unsafe code, interop, and annotations.
public static partial class Catalog
{
    [Error(424, "interop.extern")]
    public static Diagnostic ExternBody(Location at, string name) =>
        Make(at, $"extern function '{name}' cannot have a body");

    [Error(422, "unsafe.pointers")]
    public static Diagnostic PointerArithmetic(Location at) =>
        Make(at, "pointer arithmetic can only be used in 'unsafe' code");

    [Error(423, "unsafe.pointers")]
    public static Diagnostic PointerIndexing(Location at) =>
        Make(at, "pointer indexing can only be used in 'unsafe' code");

    [Error(514, "unsafe.pointers")]
    public static Diagnostic AddressOutsideUnsafe(Location at) =>
        Make(at, "'&' can only be used in 'unsafe' code");

    [Error(515, "unsafe.pointers")]
    public static Diagnostic AddressOfTemporary(Location at) =>
        Make(at, "cannot take the address of a temporary value").Help("store it in a 'mut' variable first");

    [Error(516, "unsafe.pointers")]
    public static Diagnostic AddressOfReadOnly(Location at, string text, ReadOnlyReason reason) =>
        Make(at, $"cannot take the address of '{text}'").Because(reason);

    [Error(517, "unsafe.pointers")]
    public static Diagnostic PointerCast(Location at) =>
        Make(at, "pointer casts can only be used in 'unsafe' code");

    [Error(432, "annotations.list")]
    public static Diagnostic MisplacedAnnotation(Location at, string name) =>
        Make(at, name switch
        {
            "CallerLocation" => "'@CallerLocation' can only be used on functions, methods, and constructors",
            "Acyclic" => "'@Acyclic' can only be used on classes",
            "Repr" => "'@Repr' can only be used on enums",
            "SuppressWarnings" => "'@SuppressWarnings' can only be used on declarations, local variables, and imports",
            "Include" or "CppNamespace" => $"'@{name}' can only be used on extern blocks",
            "CppName" => "'@CppName' can only be used on members of extern blocks and on the fields of their structs and unions",
            "CCallback" => "'@CCallback' can only be used on static and top-level functions with a body",
            _ => $"'@{name}' can only be used on functions and methods without a body in an extern block",
        });

    [Error(433, "annotations.list")]
    public static Diagnostic UnknownAnnotation(Location at, string name) =>
        Make(at, $"cannot find annotation '@{name}'");

    [Error(482, "annotations.suppress-warnings")]
    public static Diagnostic SuppressWarningsArguments(Location at) =>
        Make(at, "'@SuppressWarnings' takes one or more string literals").Help("write '@SuppressWarnings(\"unqualified-call\")'");

    [Warning(481, "annotations.suppress-warnings", "unknown-warning")]
    public static Diagnostic UnknownWarning(Location at, string name) =>
        Make(at, $"cannot find warning '{name}'");

    [Error(431, "annotations.syntax")]
    public static Diagnostic AnnotationArguments(Location at, string name) =>
        Make(at, $"'@{name}' takes no arguments");

    [Error(427, "interop.extern.extern-structs")]
    public static Diagnostic ExternStructOutsideStd(Location at) =>
        Make(at, "only the standard library can declare a struct backed by a C++ class")
            .Help("declare the struct's fields, or write ';' in place of its body to declare it opaque");

    [Error(429, "interop.extern.c-structs")]
    public static Diagnostic ExternStructConstructor(Location at, string kind) =>
        Make(at, $"{kind} in an extern block cannot declare constructors");

    [Error(496, "interop.extern")]
    public static Diagnostic ExternOutsideBlock(Location at) =>
        Make(at, "'extern' can only begin an extern block")
            .Help("declare the function or struct inside '@Include(\"header.h\") extern { ... }'");

    [Error(497, "interop.extern")]
    public static Diagnostic ModifierOnExternBlock(Location at, string modifier) =>
        Make(at, $"'{modifier}' cannot be used on an extern block");

    [Error(498, "interop.extern.constants")]
    public static Diagnostic ModifierOnExternConstant(Location at, string modifier) =>
        Make(at, $"'{modifier}' cannot be used on an extern constant");

    [Error(499, "interop.extern")]
    public static Diagnostic MissingInclude(Location at) =>
        Make(at, "extern block has no '@Include'").Help("write '@Include(\"header.h\")' before 'extern'");

    [Error(500, "interop.extern")]
    public static Diagnostic DuplicateInclude(Location at, Location first) =>
        Make(at, "extern block has more than one '@Include'").Note(first, "first '@Include' here");

    [Error(545, "interop.native")]
    public static Diagnostic HeaderNotFound(Location at, string header, string? package, IReadOnlyList<string> searched, string? elsewhere)
    {
        var diagnostic = Make(at, package is null
            ? $"cannot find header '{header}'"
            : $"cannot find header '{header}' in package '{package}'");
        diagnostic = package is null
            ? diagnostic.Note("a file compiled on its own has no 'native/' directory")
            : searched.Count == 0
                ? diagnostic.Note($"package '{package}' has no 'native/' directory and no 'include' directories")
                : searched.Aggregate(diagnostic, (d, directory) => d.Note($"searched '{directory}'"));
        if (elsewhere is not null)
            return diagnostic.Note($"package '{elsewhere}' has a header '{header}', which only its own extern blocks can include");
        return diagnostic.Help($"write '@Include(\"<{header}>\")' if it is a system header");
    }

    [Error(546, "interop.extern")]
    public static Diagnostic MalformedSystemHeader(Location at, string path) =>
        Make(at, $"'@Include' path '{path}' does not name a system header").Help("write a system header as '@Include(\"<header.h>\")'");

    [Error(501, "interop.extern")]
    public static Diagnostic ExternStructModifier(Location at, string kind, string modifier) =>
        Make(at, $"{kind} in an extern block cannot be '{modifier}'");

    [Error(502, "interop.extern.std")]
    public static Diagnostic GenericExtern(Location at, string name) =>
        Make(at, $"'{name}' cannot be generic").Note("only the standard library's extern blocks declare generic members");

    [Error(503, "interop.extern.std")]
    public static Diagnostic CallerLocationOnExtern(Location at) =>
        Make(at, "'@CallerLocation' cannot be used on a function without a body");

    [Error(504, "interop.extern.c-structs")]
    public static Diagnostic ExternFieldInitializer(Location at, string name, string kind) =>
        Make(at, $"field '{name}' of a {kind} in an extern block cannot have an initializer");

    [Error(505, "annotations.list")]
    public static Diagnostic AnnotationString(Location at, string name, string example) =>
        Make(at, $"'@{name}' takes one string literal").Help($"write '@{name}(\"{example}\")'");

    [Error(434, "annotations.caller-location")]
    public static Diagnostic CallerLocationOnDispatched(Location at, (string Method, string Implemented)? implementation = null) =>
        implementation is var (method, implemented)
            ? Make(at, "'@CallerLocation' cannot be used on virtual, abstract, or interface methods").Note($"'{method}' implements '{implemented}'")
            : Make(at, "'@CallerLocation' cannot be used on virtual, abstract, or interface methods");

    [Error(506, "interop.blittable")]
    public static Diagnostic NotBlittable(Location at, string what, string type) =>
        Make(at, $"{what} has type '{type}', which is not blittable")
            .Note("an extern declaration uses only types whose memory representation is the same in C++");

    [Error(507, "interop.blittable")]
    public static Diagnostic EnumNotParameter(Location at, string what, string type) =>
        Make(at, $"{what} has enum type '{type}', which only a parameter or the result of an extern function can have")
            .Help($"declare the integer type, and convert the value with a cast, as in '({type})value'");

    [Error(508, "interop.extern.c-structs")]
    public static Diagnostic UnionRead(Location at, string union, string field) =>
        Make(at, $"reading field '{field}' of union '{union}' can only be done in 'unsafe' code");

    [Error(509, "interop.extern.c-structs")]
    public static Diagnostic UnionPattern(Location at, string union) =>
        Make(at, $"cannot match union '{union}' with a pattern");

    [Error(510, "interop.signatures")]
    public static Diagnostic SignatureMismatch(Location at, string function, string cppName, string part) =>
        Make(at, $"{part} of '{function}' does not match the C++ function '{cppName}'");

    [Error(511, "interop.extern.constants")]
    public static Diagnostic ConstantMismatch(Location at, string constant, string type, string cppName) =>
        Make(at, $"type '{type}' of constant '{constant}' does not match the type of C++ '{cppName}'");

    [Error(543, "interop.extern.constants")]
    public static Diagnostic ExternConstantValue(Location at, string constant, string type, bool literalType) =>
        literalType
            ? Make(at, $"value of constant '{constant}' must be a literal of type '{type}'")
            : Make(at, $"value of constant '{constant}' must be a literal of type '{type}'")
                .Note("only a constant of an integer type, 'char', or 'bool' gives its value");

    [Error(544, "interop.extern.constants")]
    public static Diagnostic ConstantValueMismatch(Location at, string constant, string value, string cppName) =>
        Make(at, $"value {value} of constant '{constant}' does not match the value of C++ '{cppName}'");

    [Error(512, "interop.blittable.layout")]
    public static Diagnostic LayoutMismatch(Location at, string part, string kind, string type, string cppName) =>
        Make(at, $"{part} of {kind} '{type}' does not match the C++ type '{cppName}'");

    [Error(519, "interop.extern")]
    public static Diagnostic UndefinedExtern(Location at, string what, string cppName, bool cppLinkageWithCSources = false)
    {
        var diagnostic = Make(at, $"nothing that the program links defines '{cppName}', which {what} names");
        if (cppLinkageWithCSources)
            diagnostic = diagnostic.Note($"the program looks for '{cppName}' as a C++ function, and a function that a C source defines is a C function");
        return diagnostic.Help(cppLinkageWithCSources
            ? "if a C source under 'native/' defines it, declare it in the header inside 'extern \"C\"', guarded by '#ifdef __cplusplus'"
            : "define it in a source file under 'native/', or name the library that defines it in the manifest's 'link' list");
    }

    [Error(520, "unsafe.pointers")]
    public static Diagnostic VoidPointerAccess(Location at, string operation) =>
        Make(at, $"cannot {operation} a 'void*', which does not say what type it points to")
            .Help("cast it to a pointer to the type of the storage in 'unsafe' code");

    [Error(521, "interop.callbacks")]
    public static Diagnostic GenericCallback(Location at, string name) =>
        Make(at, $"'@CCallback' function '{name}' cannot be generic")
            .Note("a C function pointer calls one function with fixed parameter types");

    [Error(522, "interop.callbacks")]
    public static Diagnostic CallbackNotMarked(Location at, string name, string type) =>
        Make(at, $"'{name}' cannot be converted to '{type}' because it is not marked '@CCallback'")
            .Help($"write '@CCallback' before the declaration of '{name}'");

    [Error(523, "interop.callbacks")]
    public static Diagnostic CallbackMismatch(Location at, string name, string type, string? actual) =>
        actual is null
            ? Make(at, $"no function named '{name}' has the parameter and return types of '{type}'")
            : Make(at, $"'{name}' has type '{actual}', which cannot be converted to '{type}'")
                .Note("a '@CCallback' function converts only to the 'extern fn' type with its own parameter and return types");

    [Error(524, "interop.callbacks")]
    public static Diagnostic LambdaToExternFunction(Location at, string type) =>
        Make(at, $"a lambda cannot be converted to '{type}'")
            .Help("declare a static or top-level function marked '@CCallback', and pass its name");

    [Error(525, "interop.callbacks")]
    public static Diagnostic EscapingExternFunction(Location at, string type) =>
        Make(at, $"'escaping' cannot be declared on '{type}'").Note("a C function pointer captures nothing, so it is always escaping");

    [Error(513, "interop.extern.names")]
    public static Diagnostic CppRejected(Location at, string what, string cppMessage) =>
        Make(at, $"the C++ compiler rejects {what}").Note(cppMessage);
}
