using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

/// <summary>
/// Warnings and <c>@SuppressWarnings</c> (spec annotations.suppress-warnings). The annotation on a
/// declaration or a local variable turns the warnings it names off inside that declaration, and on an
/// import it turns them off wherever a name the import brings in is used. Every warning goes through
/// <see cref="Warn"/>, which is the one place that decides whether it is turned off.
/// </summary>
public sealed partial class Binder
{
    const string SuppressWarnings = "SuppressWarnings";

    /// <summary>Where a <c>@SuppressWarnings</c> declaration's text is, and the warnings it turns off there.</summary>
    readonly record struct Suppression(TextSpan Span, IReadOnlySet<string> Names);

    readonly Dictionary<SourceText, List<Suppression>> suppressions = [];

    /// <summary>The import a name was found through, which says which module a qualified call would name.</summary>
    sealed record ImportVia(Module? From, string Name, ImportDecl Decl);

    /// <summary>The warning names that annotations' <c>@SuppressWarnings</c> list.</summary>
    static HashSet<string> SuppressedBy(IEnumerable<AnnotationSyntax> annotations) =>
        annotations.Where(a => a.Name == SuppressWarnings)
            .SelectMany(a => a.Arguments.OfType<StringLiteralExpr>().Select(s => s.Value))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The declarations' suppressions, before any body is bound. A local's is added where it is
    /// bound, before its initializer.
    /// </summary>
    void RegisterSuppressions(IReadOnlyList<CompilationUnit> units)
    {
        foreach (var decl in units.SelectMany(u => u.Declarations))
        {
            Suppress(decl.Location, decl.Annotations);
            if (decl is TypeDecl type)
            {
                foreach (var member in type.Fields.Concat<Declaration>(type.Methods))
                    Suppress(member.Location, member.Annotations);
            }
        }
    }

    void Suppress(Location at, IReadOnlyList<AnnotationSyntax> annotations)
    {
        if (SuppressedBy(annotations) is not { Count: > 0 } names)
            return;
        if (!suppressions.TryGetValue(at.Source, out var list))
            suppressions[at.Source] = list = [];
        list.Add(new Suppression(at.Span, names));
    }

    /// <summary>Checks that a <c>@SuppressWarnings</c> lists string literals, and warns of a name no warning has.</summary>
    void CheckSuppressWarnings(AnnotationSyntax annotation)
    {
        if (annotation.Arguments.Count == 0 || annotation.Arguments.Any(a => a is not StringLiteralExpr))
        {
            Error(Catalog.SuppressWarningsArguments(annotation.Location));
            return;
        }
        foreach (var name in annotation.Arguments.Cast<StringLiteralExpr>().Where(n => !Catalog.WarningNames.Contains(n.Value)))
            Warn(Catalog.UnknownWarning(name.Location, name.Value));
    }

    /// <summary>A local variable's annotations, of which only <c>@SuppressWarnings</c> applies, turning warnings off in its initializer.</summary>
    void CheckLocalAnnotations(VarDeclStmt s)
    {
        foreach (var annotation in s.Annotations)
        {
            if (annotation.Name == SuppressWarnings)
                CheckSuppressWarnings(annotation);
            else
                ReportMisplacedAnnotation(annotation);
        }
        Suppress(s.Location, s.Annotations);
    }

    /// <summary>
    /// Reports a warning unless a <c>@SuppressWarnings</c> turns it off: one on a declaration around
    /// it, or one in <paramref name="importAnnotations"/>, for a warning about a name an import brought in.
    /// </summary>
    void Warn(Diagnostic warning, IReadOnlyList<AnnotationSyntax>? importAnnotations = null)
    {
        var name = warning.Descriptor.WarningName
            ?? throw new InvalidOperationException($"{warning.Code} is not a warning");
        if (importAnnotations is not null && SuppressedBy(importAnnotations).Contains(name))
            return;
        var at = warning.Location;
        if (suppressions.TryGetValue(at.Source, out var list)
            && list.Any(s => s.Names.Contains(name) && s.Span.Start <= at.Span.Start && at.Span.End <= s.Span.End))
            return;
        diagnostics.Add(warning);
    }

    /// <summary>
    /// The path that names an imported function through its module, as a file of
    /// <paramref name="package"/> writes it: <c>std::print</c>, <c>std::threading::spawn</c>,
    /// <c>orders::format::text</c>, <c>parser::parse</c>. Null for a name of the package's own root
    /// module, which no path reaches, and for a name of a package that the file does not see.
    /// </summary>
    static string? QualifiedPath(ImportVia via, Package package)
    {
        if (via.From is not { } module)
            return null;
        if (module.Package == package)
            return module.IsRoot ? null : string.Join("::", [.. module.Path, via.Name]);
        if (!module.Package.IsStd && package.Dependencies.GetValueOrDefault(module.Package.Name!) != module.Package)
            return null;
        return string.Join("::", [module.Package.Name!, .. module.Path, via.Name]);
    }

    /// <summary>A call of a function by a bare name that an import brought in, which a reader can't place (decision 0073).</summary>
    void CheckQualifiedCall(NameExpr name, Entry entry)
    {
        if (entry is { Type: null, Via: { } via } && QualifiedPath(via, name.Location.Source.Module.Package) is { } path)
            Warn(Catalog.UnqualifiedCall(name.Location, name.Name, path), via.Decl.Annotations);
    }
}
