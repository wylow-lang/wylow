using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

// Extern blocks (spec interop.extern): their annotations, the C++ names of their members, and what
// only the standard library's blocks may declare (interop.extern.std), which ExternBlock.IsStd decides.
public sealed partial class Binder
{
    const string Include = "Include";
    const string CppNamespaceName = "CppNamespace";
    const string CppNameName = "CppName";

    /// <summary>The header of each extern block, each once, in the order the blocks name them.</summary>
    readonly List<ExternHeader> headers = [];

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
                    var header = StringArgument(annotation, "header.h");
                    if (include is not null)
                        Error(Catalog.DuplicateInclude(annotation.Location, include.Location));
                    else
                    {
                        include = annotation;
                        if (header is not null && !headers.Any(h => h.Path == header))
                            headers.Add(new ExternHeader(header, annotation.Location));
                    }
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

    /// <summary>
    /// Declares an extern constant as an extern function without parameters, which reading the
    /// constant calls. The call needs no unsafe code and changes nothing (spec <c>interop.extern.constants</c>).
    /// </summary>
    FunctionSymbol DeclareExternConstant(ExternConstantDecl constant)
    {
        HasCallerLocation(constant);
        var type = BindType(constant.Type, allowVoid: false);
        var decl = new FunctionDecl(constant.Location, constant.Modifiers, constant.Type, constant.Name, constant.NameLocation, [], [], null)
        {
            Visibility = constant.Visibility,
            Block = constant.Block,
        };
        var function = new FunctionSymbol(constant.Name, [], type)
        {
            Declaration = decl,
            Reach = Reach.Of(decl),
            BuiltinCppName = BlockMemberCppName(constant, constant.Name, constant.Block!),
            IsPure = true,
            IsExternConstant = true,
        };
        AddTopLevelFunction(function, decl);
        return function;
    }

    /// <summary>
    /// Checks that each type an extern declaration of a block outside the standard library uses is
    /// blittable, after every struct's fields are declared (spec <c>interop.blittable</c>). A
    /// <c>@Repr</c> enum may only be a parameter's type.
    /// </summary>
    void CheckBlittable(IEnumerable<FunctionSymbol> functions, IEnumerable<TypeSymbol> types)
    {
        foreach (var function in functions.Where(f => f.IsProgramExtern))
        {
            var decl = function.Declaration!;
            if (function.IsExternConstant)
            {
                CheckBlittable(decl.ReturnType!.Location, function.ReturnType, $"constant '{function.Name}'", parameter: false);
                continue;
            }
            for (int i = 0; i < function.Parameters.Count && i < decl.Parameters.Count; i++)
            {
                var parameter = function.Parameters[i];
                var what = $"parameter '{parameter.Name}'";
                if (parameter.RefKind != RefKind.None)
                    Error(Catalog.NotBlittable(decl.Parameters[i].Location, what, $"{FunctionSymbol.RefPrefix(parameter.RefKind)}{parameter.Type}"));
                else
                    CheckBlittable(decl.Parameters[i].Type.Location, parameter.Type, what, parameter: true);
            }
            if (function.ReturnType != WylType.Void)
                CheckBlittable(decl.ReturnType!.Location, function.ReturnType, $"the result of '{function.Name}'", parameter: false);
        }
        foreach (var s in types.Where(s => s.Declaration.Block is { IsStd: false }))
        {
            foreach (var field in s.Fields)
                CheckBlittable(field.Declaration.Type.Location, field.Type, $"field '{field.Name}'", parameter: false);
        }
    }

    void CheckBlittable(Location at, WylType type, string what, bool parameter)
    {
        if (type.Symbol is { HasRepr: true, HasData: false })
        {
            if (!parameter)
                Error(Catalog.EnumNotParameter(at, what, type.ToString()));
        }
        else if (!type.IsBlittable)
        {
            Error(Catalog.NotBlittable(at, what, type.ToString()));
        }
    }

    /// <summary>
    /// The accesses of union fields outside unsafe code that were bound, less those that an
    /// assignment writes. Each one left after every body is bound is a read, which needs unsafe code
    /// (spec <c>interop.extern.c-structs</c>).
    /// </summary>
    readonly HashSet<BoundFieldAccess> unionReads = new(ReferenceEqualityComparer.Instance);

    /// <summary>Takes the union fields that a plain assignment's target goes through out of the reads, since the assignment writes them.</summary>
    void WritesUnionFields(BoundExpression target)
    {
        foreach (var step in PlacePath.Of(target).Steps.OfType<BoundFieldAccess>())
            unionReads.Remove(step);
    }

    /// <summary>Reports each read of a union field outside unsafe code once, however often the binder bound it.</summary>
    void ReportUnionReads()
    {
        foreach (var read in unionReads.DistinctBy(r => r.Location))
            Error(Catalog.UnionRead(read.Location, read.Field.Owner.Name, read.Field.Name));
    }
}
