namespace Wyl.Compiler.Text;

/// <summary>
/// A package (spec 06-syntax.md §6.1): the unit of compilation, whose modules are its directories.
/// A file compiled on its own is an unnamed package of one module.
/// </summary>
public sealed class Package
{
    Package(string? name)
    {
        Name = name;
        Root = new Module(this, null, "");
    }

    /// <summary>The standard library, which every program is compiled with.</summary>
    public static Package Std { get; } = new("std");

    /// <summary>A lone file's package, which no other package can name.</summary>
    public static Package Unnamed() => new(null);

    /// <summary>A package with a manifest; its name was checked against the manifest's rules (§6.1, The manifest).</summary>
    public static Package Named(string name) =>
        name == Std.Name ? throw new ArgumentException("only the standard library is 'std'", nameof(name)) : new(name);

    /// <summary>The name other packages write it by, or null for a lone file.</summary>
    public string? Name { get; }

    public bool IsStd => this == Std;

    /// <summary>The module of <c>src/</c> itself.</summary>
    public Module Root { get; }
}

/// <summary>A module: a directory of a package, whose files share its names (§6.1).</summary>
public sealed class Module
{
    readonly Dictionary<string, Module> children = new(StringComparer.Ordinal);

    internal Module(Package package, Module? parent, string name)
    {
        Package = package;
        Parent = parent;
        Name = name;
    }

    public Package Package { get; }

    /// <summary>The module whose directory holds this one's, or null for the root.</summary>
    public Module? Parent { get; }

    /// <summary>The directory's name, empty for the root.</summary>
    public string Name { get; }

    public bool IsRoot => Parent is null;

    /// <summary>The names from the package's root down to this module, empty for the root: <c>world::gen</c> is <c>[world, gen]</c>.</summary>
    public IReadOnlyList<string> Path => Parent is null ? [] : [.. Parent.Path, Name];

    public IReadOnlyCollection<Module> Children => children.Values;

    /// <summary>The submodule of that name, made if there is none yet.</summary>
    public Module Child(string name)
    {
        if (!children.TryGetValue(name, out var child))
            children[name] = child = new Module(Package, this, name);
        return child;
    }

    /// <summary>As a path names it within its package: <c>world::gen</c>, or empty for the root.</summary>
    public override string ToString() => string.Join("::", Path);
}
