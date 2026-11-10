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
            _ => "'@Pure' can only be used on extern functions of the standard library",
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
        Make(at, "extern structs can only be declared in the standard library");

    [Error(428, "interop.extern.extern-structs")]
    public static Diagnostic ExternStructField(Location at) =>
        Make(at, "extern struct cannot declare fields").Note("its C++ class holds its data");

    [Error(429, "interop.extern.extern-structs")]
    public static Diagnostic ExternStructConstructor(Location at) =>
        Make(at, "extern struct cannot declare constructors");

    [Error(430, "interop.extern.extern-structs")]
    public static Diagnostic NonExternMethodInExternStruct(Location at) =>
        Make(at, "methods of an extern struct must be extern");

    [Error(425, "interop.extern")]
    public static Diagnostic InstanceExternMethod(Location at) =>
        Make(at, "extern methods must be static outside an extern struct");

    [Error(426, "interop.extern")]
    public static Diagnostic ExternFunctionOutsideStd(Location at) =>
        Make(at, "extern functions can only be declared in the standard library");

    [Error(434, "annotations.caller-location")]
    public static Diagnostic CallerLocationOnDispatched(Location at, (string Method, string Implemented)? implementation = null) =>
        implementation is var (method, implemented)
            ? Make(at, "'@CallerLocation' cannot be used on virtual, abstract, or interface methods").Note($"'{method}' implements '{implemented}'")
            : Make(at, "'@CallerLocation' cannot be used on virtual, abstract, or interface methods");
}
