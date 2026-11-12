using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.Semantics;

// Extern blocks (spec interop.extern): their annotations, the C++ names of their members, and what
// only the standard library's blocks may declare (interop.extern.std), which ExternBlock.IsStd decides.
public sealed partial class Binder
{
    const string Include = "Include";
    const string CppNamespaceName = "CppNamespace";
    const string CppNameName = "CppName";

    /// <summary>Checks each block's annotations: exactly one <c>@Include</c>, and otherwise only <c>@CppNamespace</c>.</summary>
    void CheckExternBlocks(IEnumerable<CompilationUnit> units)
    {
        foreach (var block in units.SelectMany(u => u.ExternBlocks))
        {
            AnnotationSyntax? include = null;
            foreach (var annotation in block.Annotations)
            {
                if (annotation.Name == Include)
                {
                    StringArgument(annotation, "header.h");
                    if (include is null)
                        include = annotation;
                    else
                        Error(Catalog.DuplicateInclude(annotation.Location, include.Location));
                }
                else if (annotation.Name == CppNamespaceName)
                {
                    StringArgument(annotation, "name");
                }
                else
                {
                    ReportMisplacedAnnotation(annotation);
                }
            }
            if (include is null)
                Error(Catalog.MissingInclude(block.Location));
        }
    }

    /// <summary>The value of an annotation's one string literal, or null after reporting that it has none.</summary>
    string? StringArgument(AnnotationSyntax annotation, string example)
    {
        if (annotation.Arguments is [StringLiteralExpr literal])
            return literal.Value;
        Error(Catalog.AnnotationString(annotation.Location, annotation.Name, example));
        return null;
    }

    /// <summary>
    /// The C++ name of a member of an extern block: its <c>@CppName</c>, or its own name in its
    /// block's <c>@CppNamespace</c> (spec <c>interop.extern.names</c>).
    /// </summary>
    static string BlockMemberCppName(Declaration decl, string name, ExternBlock block) =>
        WrittenCppName(decl)
        ?? (block.Annotations.FirstOrDefault(a => a.Name == CppNamespaceName)?.Arguments is [StringLiteralExpr space] ? $"{space.Value}::{name}" : name);

    static string? WrittenCppName(Declaration decl) =>
        decl.Annotations.FirstOrDefault(a => a.Name == CppNameName)?.Arguments is [StringLiteralExpr written] ? written.Value : null;

    /// <summary>
    /// Whether C++ provides the function: it is declared without a body at the top level of an
    /// extern block, or as a method of a struct or union of one (spec <c>interop.extern</c>).
    /// </summary>
    static bool IsExternFunction(FunctionDecl decl, TypeSymbol? owner) =>
        decl.Body is null && !decl.IsConstructor && (decl.Block is not null || owner is { IsExtern: true });

    /// <summary>The extern block that a function is declared in, directly or through its struct or union.</summary>
    static ExternBlock? BlockOf(FunctionDecl decl, TypeSymbol? owner) => decl.Block ?? owner?.Declaration.Block;

    /// <summary>
    /// The C++ function that an extern function calls, or null for any other function. A top-level
    /// function is named as any member of its block is. An instance method calls the member function
    /// of its own name, and a static method the static member function of its type.
    /// </summary>
    static string? ExternFunctionName(FunctionDecl decl, TypeSymbol? owner)
    {
        if (!IsExternFunction(decl, owner))
            return null;
        if (owner is null)
            return BlockMemberCppName(decl, decl.Name, decl.Block!);
        var name = WrittenCppName(decl) ?? decl.Name;
        return (decl.Modifiers & Modifiers.Static) != 0 ? $"{owner.CppName}::{name}" : name;
    }

    /// <summary>Whether a call of the function needs unsafe code because C++ provides it and it is not marked <c>@Safe</c> (spec <c>interop.safety</c>).</summary>
    static bool IsUnsafeExtern(FunctionDecl decl, TypeSymbol? owner) =>
        IsExternFunction(decl, owner) && !decl.Annotations.Any(a => a.Name == "Safe");

    /// <summary>An extern function marked <c>@Pure</c>, which neither panics nor changes anything.</summary>
    static bool IsPure(FunctionDecl decl, TypeSymbol? owner) =>
        IsExternFunction(decl, owner) && decl.Annotations.Any(a => a.Name == "Pure");

    /// <summary>
    /// Reports what a struct or union of an extern block may not be: <c>unique</c> or <c>ref</c>,
    /// backed by a C++ class, or generic. The standard library's blocks may declare a generic struct
    /// and a struct backed by a C++ class, which may be <c>unique</c> (spec <c>interop.extern.std</c>).
    /// </summary>
    void CheckExternType(TypeSymbol s)
    {
        if (s.Declaration.Block is not { } block)
            return;
        var decl = s.Declaration;
        var kind = s.IsUnion ? "union" : "struct";
        if (s.IsUnique && !(block.IsStd && s.IsCppClass))
            Error(Catalog.ExternStructModifier(decl.NameLocation, kind, "unique"));
        if (s.IsRefStruct)
            Error(Catalog.ExternStructModifier(decl.NameLocation, kind, "ref"));
        if (block.IsStd)
            return;
        if (s.IsCppClass)
            Error(Catalog.ExternStructOutsideStd(decl.NameLocation));
        if (s.IsGeneric)
            Error(Catalog.GenericExtern(decl.NameLocation, decl.Name));
    }

    /// <summary>Reports a generic function or method of an extern block outside the standard library.</summary>
    void CheckExternFunction(FunctionSymbol function, TypeSymbol? owner)
    {
        var decl = function.Declaration!;
        if (BlockOf(decl, owner) is { IsStd: false } && function.IsGeneric)
            Error(Catalog.GenericExtern(decl.NameLocation, decl.Name));
    }

    /// <summary>Checks an extern constant's type, name, and annotations.</summary>
    void DeclareExternConstant(ExternConstantDecl decl)
    {
        HasCallerLocation(decl);
        BindType(decl.Type, allowVoid: false);
        TakesLanguageName(decl.Name, decl.NameLocation);
    }
}
