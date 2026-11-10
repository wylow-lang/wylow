using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

/// <summary>
/// Imports and paths (spec programs.imports, programs.paths). An import records which name of which
/// module it stands for, and the name is looked up where it is used, so an import may name any
/// declaration of the program in any order. Imports are registered before anything is declared,
/// and checked to name something visible once everything is.
/// </summary>
public sealed partial class Binder
{
    /// <summary>
    /// A name an import brings in: <see cref="Name"/> of <see cref="From"/>, or, with no module, of
    /// the top of <see cref="Package"/>, where a top-level module, a package, and a name of the root
    /// module are found. A <c>pub import</c> reaches as far as its visibility, and others only their file.
    /// </summary>
    sealed record Imported(Module? From, string Name, Package Package, Reach Reach, ImportedName Syntax, ImportDecl Decl);

    sealed record ImportedAll(Module From, Reach Reach, ImportDecl Decl);

    sealed class ImportTable
    {
        public readonly Dictionary<string, Imported> Named = new(StringComparer.Ordinal);
        public readonly List<ImportedAll> All = [];
    }

    readonly Dictionary<SourceText, ImportTable> fileImports = [];

    /// <summary>What each module re-exports with <c>pub import</c>, which are among the module's names.</summary>
    readonly Dictionary<Module, ImportTable> reExports = [];

    // A re-export of a re-export is followed this far, so that a cycle of them ends.
    const int MaxReExportDepth = 16;

    static ImportTable TableIn<TKey>(Dictionary<TKey, ImportTable> tables, TKey key) where TKey : notnull =>
        tables.TryGetValue(key, out var table) ? table : tables[key] = new();

    /// <summary>A path's first name in an import: a top-level module of the package, or a package, which <c>std</c> always is.</summary>
    static Module? TopLevel(string name, Package package) =>
        name == Package.Std.Name ? Package.Std.Root : package.Root.FindChild(name);

    /// <summary>What an import stands for, seen from <paramref name="from"/>.</summary>
    Entry Target(Imported imported, SourceText from, int depth = 0)
    {
        if (depth > MaxReExportDepth)
            return default;
        if (imported.From is { } module)
            return NamesOf(module, imported.Name, from, depth + 1);
        return NamesOf(imported.Package.Root, imported.Name, from, depth + 1) with { Module = TopLevel(imported.Name, imported.Package) };
    }

    /// <summary>What a module re-exports under the name to <paramref name="from"/>: by name first, then by <c>*</c>.</summary>
    Entry ReExported(Module module, string name, SourceText from, int depth = 0)
    {
        if (depth > MaxReExportDepth || !reExports.TryGetValue(module, out var exported))
            return default;
        if (exported.Named.TryGetValue(name, out var imported) && imported.Reach.Includes(from))
            return Target(imported, from, depth + 1);
        foreach (var all in exported.All.Where(a => a.Reach.Includes(from)))
        {
            if (VisibleTo(NamesOf(all.From, name, from, depth + 1), from) is { IsEmpty: false } entry)
                return entry;
        }
        return default;
    }

    void RegisterImports(IReadOnlyList<CompilationUnit> units)
    {
        foreach (var unit in units)
        {
            foreach (var import in unit.Imports)
                RegisterImport(unit.Source, import);
        }
    }

    void RegisterImport(SourceText source, ImportDecl import)
    {
        foreach (var annotation in import.Annotations)
        {
            if (annotation.Name == SuppressWarnings)
                CheckSuppressWarnings(annotation);
            else
                ReportMisplacedAnnotation(annotation);
        }
        var package = source.Module.Package;
        var reach = new Reach(import.Visibility, source);
        Module? from = null;
        foreach (var name in import.Module)
        {
            var next = from is null ? TopLevel(name.Name, package) : from.FindChild(name.Name);
            if (next is null)
            {
                Error(from is null ? Catalog.NoTopLevelModule(name.Location, name.Name) : Catalog.NoSubmodule(name.Location, from, name.Name));
                return;
            }
            from = next;
        }

        var imports = TableIn(fileImports, source);
        if (import.IsAll)
        {
            imports.All.Add(new ImportedAll(from!, reach, import));
            if (import.Visibility != Visibility.File)
                TableIn(reExports, source.Module).All.Add(new ImportedAll(from!, reach, import));
            return;
        }
        foreach (var name in import.Names)
        {
            var local = name.Local;
            var imported = new Imported(from, name.Name.Name, package, reach, name, import);
            if (!imports.Named.TryAdd(local.Name, imported))
            {
                Error(Catalog.DuplicateImport(local.Location, local.Name));
                continue;
            }
            if (import.Visibility != Visibility.File && !TableIn(reExports, source.Module).Named.TryAdd(local.Name, imported))
                Error(Catalog.DuplicateReExport(local.Location, local.Name));
        }
    }

    /// <summary>
    /// That each named import names something its file may see, doesn't take a name its module
    /// already has, and, re-exported, reaches no farther than what it names (spec programs.imports).
    /// </summary>
    void CheckImports()
    {
        foreach (var (source, imports) in fileImports)
        {
            foreach (var (local, imported) in imports.Named)
            {
                var name = imported.Syntax.Name;
                var entry = Target(imported, source);
                if (entry.IsEmpty)
                {
                    var module = imported.From ?? imported.Package.Root;
                    Error(PrivateIn(name.Location, module, name.Name)
                        ?? (imported.From is null ? Catalog.NoRootName(name.Location, name.Name) : Catalog.NoNameInModule(name.Location, module, name.Name)));
                    continue;
                }
                var reach = entry.Type?.Reach ?? entry.Functions?.MaxBy(f => f.Reach.Level)?.Reach;
                if (reach is { } named && !named.Includes(source))
                {
                    Error(Catalog.NotVisible(name.Location, $"'{name.Name}'", named.Level, named.Source));
                    continue;
                }
                // Only the language's own declaration may be imported under its name, so the name means it everywhere.
                if (entry.HasValue && !source.Module.Package.IsStd && stdlib.IsLanguageName(local)
                    && (entry.Functions is not null || entry.Type is null || entry.Type != stdlib.LanguageType(local)))
                {
                    Error(Catalog.LanguageNameImported(imported.Syntax.Local.Location, local));
                    continue;
                }
                if ((entry.HasValue && (OwnType(local, source) is not null || OwnFunctions(local, source).Count > 0))
                    || (entry.Module is not null && source.Module.FindChild(local) is not null))
                {
                    Error(Catalog.ImportTakesModuleName(imported.Syntax.Local.Location, local));
                    continue;
                }
                if (imported.Reach.Level > Visibility.File && reach is { } item && item.Level < imported.Reach.Level)
                    Error(Catalog.ReExportReachesFarther(name.Location, name.Name, item.Level, item.Source));
            }
        }
    }

    /// <summary>
    /// The module a path in code walks to (spec programs.paths: it starts with a module the file sees, one it
    /// imported or a submodule of its own), or null after reporting why not.
    /// </summary>
    Module? WalkModules(IReadOnlyList<PathName> modules, bool report = true)
    {
        var first = modules[0];
        if (ResolveModule(first.Name, first.Location) is not { } module)
        {
            if (!report)
                return null;
            Error(TopLevel(first.Name, first.Location.Source.Module.Package) is not null
                ? Catalog.ModuleNotImported(first.Location, first.Name)
                : Catalog.NotAModule(first.Location, first.Name));
            return null;
        }
        foreach (var name in modules.Skip(1))
        {
            if (NamesOf(module, name.Name, name.Location.Source).Module is not { } next)
            {
                if (!report)
                    return null;
                Error(Catalog.NoSubmodule(name.Location, module, name.Name));
                return null;
            }
            module = next;
        }
        return module;
    }

    /// <summary>
    /// What a name in a module means, as <c>world::Chunk</c> or <c>gen::seed</c> write it, or null
    /// after reporting why not. Whether the use may see it is checked where it is used, as for any name.
    /// </summary>
    Entry? ResolvePath(IReadOnlyList<PathName> modules, string name, Location nameLocation)
    {
        if (WalkModules(modules) is not { } module)
            return null;
        var entry = NamesOf(module, name, nameLocation.Source);
        if (entry.HasValue)
            return entry;
        Error(PrivateIn(nameLocation, module, name) ?? Catalog.NoNameInModule(nameLocation, module, name));
        return null;
    }

    /// <summary>
    /// How far a file-private declaration of the name in one of the module's files reaches, which
    /// isn't one of the module's names, so that a miss says it is private (spec programs.imports).
    /// </summary>
    Diagnostic? PrivateIn(Location at, Module module, string name)
    {
        foreach (var file in module.Files)
        {
            if (!fileNames.TryGetValue(file, out var table))
                continue;
            if (table.Types.TryGetValue(name, out var type))
                return Catalog.NotVisible(at, $"type '{name}'", type.Reach.Level, type.Reach.Source);
            if (table.Functions.TryGetValue(name, out var functions))
                return Catalog.NotVisible(at, $"function '{name}'", functions[0].Reach.Level, functions[0].Reach.Source);
        }
        return null;
    }

    /// <summary>What a path means, without reporting anything if it means nothing: for asking whether it names a type or functions.</summary>
    Entry? FindPath(IReadOnlyList<PathName> modules, string name, Location nameLocation) =>
        WalkModules(modules, report: false) is { } module && NamesOf(module, name, nameLocation.Source) is { HasValue: true } entry ? entry : null;

    /// <summary>A type written with its module, as in <c>memory::CycleStats</c>.</summary>
    WylType BindPathType(TypeSyntax syntax)
    {
        if (ResolvePath(syntax.Modules, syntax.Name, syntax.Location) is not { } entry)
            return WylType.Error;
        if (entry.Type is not { } symbol)
        {
            Error(Catalog.FunctionNotType(syntax.Location, TextOf(syntax.Location)));
            return WylType.Error;
        }
        var arguments = syntax.TypeArguments.Select(a => BindType(a, allowVoid: false)).ToList();
        return arguments.Contains(WylType.Error) ? WylType.Error : Construct(symbol, arguments, syntax.Location);
    }
}
