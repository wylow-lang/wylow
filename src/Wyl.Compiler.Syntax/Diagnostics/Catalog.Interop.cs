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
        Make(at, $"{what} has enum type '{type}', which only a parameter of an extern function can have")
            .Help("declare the integer type, and convert the value with 'as'");

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

    [Error(512, "interop.blittable.layout")]
    public static Diagnostic LayoutMismatch(Location at, string part, string kind, string type, string cppName) =>
        Make(at, $"{part} of {kind} '{type}' does not match the C++ type '{cppName}'");

    [Error(513, "interop.extern.names")]
    public static Diagnostic CppRejected(Location at, string what, string cppMessage) =>
        Make(at, $"the C++ compiler rejects {what}").Note(cppMessage);
}
