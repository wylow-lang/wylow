using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

/// <summary>
/// Where a declaration can be named from (spec 06-syntax.md §6.2): its visibility, counted from the
/// file that declares it. Every check of whether a use may name a declaration asks
/// <see cref="Includes"/>, so there is one definition of what each level reaches.
/// </summary>
public readonly record struct Reach(Visibility Level, SourceText? Source)
{
    /// <summary>What the compiler declares itself, such as <c>print</c>, which has no file.</summary>
    public static readonly Reach Everywhere = new(Visibility.Public, null);

    public static Reach Of(Declaration declaration) => new(declaration.Visibility, declaration.Location.Source);

    /// <summary>A member is visible only where its type is too.</summary>
    public Reach Within(Reach owner) => this with { Level = Level < owner.Level ? Level : owner.Level };

    public bool Includes(SourceText use) => Level switch
    {
        Visibility.Public => true,
        Visibility.Package => PackageOf(use) == PackageOf(Source!),
        Visibility.Module => PackageOf(use) == PackageOf(Source!) && ModuleOf(use) == ModuleOf(Source!),
        _ => use == Source,
    };

    /// <summary>How far it reaches, for messages: "everywhere", "only in its file (List.wyl)", ...</summary>
    public string Describe() => Level switch
    {
        Visibility.Public => "everywhere",
        Visibility.Package => PackageOf(Source!) is { Length: > 0 } package ? $"only in package '{package}'" : "only in this program's package",
        Visibility.Module => ModuleOf(Source!) is { Length: > 0 } module ? $"only in module '{module}'" : "only in this program's root module",
        _ => $"only in its file ({Source!.FileName})",
    };

    // Until packages are directories (§6.1), the standard library is the package 'std', the program
    // being compiled is the other, and each is one root module.
    static string PackageOf(SourceText source) => Stdlib.Contains(source) ? "std" : "";

    static string ModuleOf(SourceText source) => PackageOf(source);
}
