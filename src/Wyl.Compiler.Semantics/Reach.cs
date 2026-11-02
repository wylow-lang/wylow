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
        Visibility.Package => use.Module.Package == Source!.Module.Package,
        // Exactly its directory: a module and its submodules see each other as any two modules do.
        Visibility.Module => use.Module == Source!.Module,
        _ => use == Source,
    };
}
