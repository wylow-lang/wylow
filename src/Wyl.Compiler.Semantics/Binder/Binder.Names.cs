using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

/// <summary>
/// Top-level names (spec programs.names): each module's, which its files make visible beyond
/// themselves, and each file's file-private ones, which are kept out of the module's so that two
/// files may each have a private helper of one name. Every lookup of a top-level name where it is
/// used goes through <see cref="ResolveName"/>, and of a module through <see cref="ResolveModule"/>.
/// </summary>
public sealed partial class Binder
{
    sealed class NameTable
    {
        public readonly Dictionary<string, TypeSymbol> Types = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<FunctionSymbol>> Functions = new(StringComparer.Ordinal);

        public bool Declares(string name) => Types.ContainsKey(name) || Functions.ContainsKey(name);
    }

    /// <summary>
    /// What a name means in one place: a type or a function's overloads, and, in a namespace of its
    /// own, a module. Empty if it means nothing there.
    /// </summary>
    readonly record struct Entry(TypeSymbol? Type, IReadOnlyList<FunctionSymbol>? Functions, Module? Module)
    {
        /// <summary>The file's import the name was found through, if it was.</summary>
        public ImportVia? Via { get; init; }

        public bool HasValue => Type is not null || Functions is not null;

        public bool IsEmpty => !HasValue && Module is null;

        /// <summary>Whether two entries mean the same thing, as one name reached by two imports does.</summary>
        public bool SameAs(Entry other) =>
            Type == other.Type && Module == other.Module
            && (Functions ?? []).ToHashSet().SetEquals(other.Functions ?? []);
    }

    readonly Dictionary<Module, NameTable> moduleNames = [];
    readonly Dictionary<SourceText, NameTable> fileNames = [];

    // An ambiguity is reported once where it is used, however often the binder asks there.
    readonly HashSet<(Location, string)> reportedAmbiguities = [];

    NameTable ModuleNames(Module module) => moduleNames.TryGetValue(module, out var table) ? table : moduleNames[module] = new();

    NameTable FileNames(SourceText file) => fileNames.TryGetValue(file, out var table) ? table : fileNames[file] = new();

    /// <summary>Where a top-level declaration's name goes: its file's names if it is file-private, else its module's.</summary>
    NameTable TableOf(Declaration declaration) => declaration.Visibility == Visibility.File
        ? FileNames(declaration.Location.Source)
        : ModuleNames(declaration.Location.Source.Module);

    /// <summary>The type of the name the file or its module declares, the nearest of a file's names.</summary>
    TypeSymbol? OwnType(string name, SourceText file) =>
        FileNames(file).Types.GetValueOrDefault(name) ?? ModuleNames(file.Module).Types.GetValueOrDefault(name);

    /// <summary>The overloads of the name the file and its module declare, one set across the module's files.</summary>
    List<FunctionSymbol> OwnFunctions(string name, SourceText file) =>
        [.. FileNames(file).Functions.GetValueOrDefault(name) ?? [], .. ModuleNames(file.Module).Functions.GetValueOrDefault(name) ?? []];

    /// <summary>
    /// The places a file looks up a top-level name (spec programs.imports), nearest first, with what
    /// the name means in each: the names the file and its module declare, those the module
    /// re-exports, the file's named import, each of its <c>*</c> imports, and the types the language
    /// is built on. The <c>*</c> imports are together one place, so each comes with the module it
    /// imports from. Lazy, so a lookup that stops early reaches no farther.
    /// </summary>
    IEnumerable<(Entry Entry, Module? Star)> Places(string name, SourceText from)
    {
        var functions = OwnFunctions(name, from);
        yield return (new Entry(OwnType(name, from), functions.Count > 0 ? functions : null, null), null);
        yield return (ReExported(from.Module, name, from), null);
        if (fileImports.TryGetValue(from, out var imports))
        {
            if (imports.Named.TryGetValue(name, out var imported))
                yield return (Target(imported, from) with { Via = new ImportVia(imported.From, imported.Name, imported.Decl) }, null);
            foreach (var all in imports.All)
                yield return (VisibleTo(NamesOf(all.From, name, from), from) with { Module = null, Via = new ImportVia(all.From, name, all.Decl) }, all.From);
        }
        yield return (new Entry(stdlib.LanguageType(name), null, null), null);
    }

    /// <summary>
    /// The nearest meaning of a top-level name where it is used: the first of <see cref="Places"/>
    /// that has it, which hides the rest, so overloads never merge across them. The <c>*</c>
    /// imports give way to the places before them and are ambiguous only where used.
    /// </summary>
    Entry ResolveName(string name, Location use)
    {
        var stars = new List<(Entry Entry, Module From)>();
        foreach (var (entry, star) in Places(name, use.Source))
        {
            if (star is not null)
            {
                if (entry.HasValue && !stars.Any(f => f.Entry.SameAs(entry)))
                    stars.Add((entry, star));
                continue;
            }
            if (stars.Count > 0)
                return OneOfStars(stars, name, use);
            if (entry.HasValue)
                return entry;
        }
        return default;
    }

    /// <summary>
    /// The module a path's first name means where it is used: a submodule of the file's module, one
    /// its module re-exports, one the file imports, or else the package <c>std</c>, which every file sees.
    /// </summary>
    Module? ResolveModule(string name, Location use)
    {
        var from = use.Source;
        if (from.Module.FindChild(name) is { } child)
            return child;
        if (ReExported(from.Module, name, from) is { Module: { } reExported })
            return reExported;
        if (fileImports.TryGetValue(from, out var imports))
        {
            if (imports.Named.TryGetValue(name, out var imported) && Target(imported, from) is { Module: { } module })
                return module;
            if (FromAll(imports, name, from, use)?.Module is { } all)
                return all;
        }
        return name == Package.Std.Name ? Package.Std.Root : null;
    }

    /// <summary>The one module a name means among a file's <c>*</c> imports, if any.</summary>
    Entry? FromAll(ImportTable imports, string name, SourceText from, Location use)
    {
        var found = new List<(Entry Entry, Module From)>();
        foreach (var all in imports.All)
        {
            var entry = new Entry(null, null, NamesOf(all.From, name, from).Module);
            if (!entry.IsEmpty && !found.Any(f => f.Entry.SameAs(entry)))
                found.Add((entry, all.From));
        }
        return found.Count > 0 ? OneOfStars(found, name, use) : null;
    }

    /// <summary>
    /// The meaning a name takes from a file's <c>*</c> imports when they give it one or more. Two
    /// different ones are an error where the name is used, reported once, and the first is taken.
    /// </summary>
    Entry OneOfStars(List<(Entry Entry, Module From)> found, string name, Location use)
    {
        if (found.Count > 1 && reportedAmbiguities.Add((use, name)))
            Error(Catalog.AmbiguousStarImport(use, name, found[0].From, found[1].From));
        return found[0].Entry;
    }

    /// <summary>Only what is visible to the file, as a <c>*</c> import brings in.</summary>
    static Entry VisibleTo(Entry entry, SourceText from)
    {
        var functions = entry.Functions?.Where(f => f.Reach.Includes(from)).ToList();
        return new Entry(
            entry.Type is { } type && type.Reach.Includes(from) ? type : null,
            functions is { Count: > 0 } ? functions : null,
            entry.Module);
    }

    /// <summary>
    /// The names a module has, as another module reaches them (spec programs.names): its files' declarations
    /// visible beyond them, its submodules, and what it re-exports to <paramref name="from"/>. Their
    /// own visibility is the caller's to check, so a miss can say how far a name reaches.
    /// </summary>
    Entry NamesOf(Module module, string name, SourceText from, int depth = 0)
    {
        var table = ModuleNames(module);
        var type = table.Types.GetValueOrDefault(name);
        IReadOnlyList<FunctionSymbol>? functions = table.Functions.GetValueOrDefault(name);
        var submodule = module.FindChild(name);
        if ((type is null && functions is null) || submodule is null)
        {
            var reExported = ReExported(module, name, from, depth);
            if (type is null && functions is null)
                (type, functions) = (reExported.Type, reExported.Functions);
            submodule ??= reExported.Module;
        }
        return new Entry(type, functions, submodule);
    }

    TypeSymbol? LookupType(string name, Location use) => ResolveName(name, use).Type;

    /// <summary>The overloads a name means where it is used; null if it means no function there.</summary>
    IReadOnlyList<FunctionSymbol>? LookupFunctions(string name, Location use) => ResolveName(name, use).Functions;

    /// <summary>
    /// How far a declaration of the name reaches that the file can't see but another file of its
    /// module has, so a miss says it is private there rather than that it doesn't exist.
    /// </summary>
    Diagnostic? HiddenElsewhere(string name, Location at)
    {
        foreach (var file in at.Source.Module.Files)
        {
            if (file == at.Source || !fileNames.TryGetValue(file, out var table))
                continue;
            if (table.Types.TryGetValue(name, out var type))
                return Catalog.NotVisible(at, $"type '{name}'", type.Reach.Level, type.Reach.Source);
            if (table.Functions.TryGetValue(name, out var functions))
                return Catalog.NotVisible(at, $"function '{name}'", functions[0].Reach.Level, functions[0].Reach.Source);
        }
        return null;
    }

    /// <summary>
    /// Whether a declaration outside the standard library, of any kind, takes a name every file
    /// sees (spec programs.builtin-names), reporting it if so. Every place that declares a name asks.
    /// </summary>
    bool TakesLanguageName(string name, Location at)
    {
        if (at.Source.Module.Package.IsStd || !stdlib.IsLanguageName(name))
            return false;
        Error(Catalog.LanguageTypeName(at, name));
        return true;
    }

    /// <summary>A function the compiler declares (<c>print</c>, <c>panic</c>), a name of the std module at <paramref name="path"/>.</summary>
    void AddBuiltin(string[] path, FunctionSymbol function)
    {
        var module = path.Aggregate(Package.Std.Root, (parent, name) => parent.FindChild(name)
            ?? throw new InvalidOperationException($"the standard library has no module '{name}' for '{function.Name}'"));
        var table = ModuleNames(module);
        if (!table.Functions.TryGetValue(function.Name, out var overloads))
            table.Functions[function.Name] = overloads = [];
        overloads.Add(function);
    }

    void AddFunction(FunctionSymbol function, FunctionDecl decl)
    {
        var table = TableOf(decl);
        if (!table.Functions.TryGetValue(function.Name, out var overloads))
            table.Functions[function.Name] = overloads = [];
        overloads.Add(function);
    }

    /// <summary>
    /// Another file of the module that a new top-level declaration's name would clash in: one that
    /// declares the name beyond itself, or, if the new one is visible beyond its own file, one that
    /// declares it at all, since that file would then see two meanings of it (spec programs.names). Functions of
    /// one name are one overload set across the module's files, so only a type clashes with a
    /// function; a repeated signature is the overload check's.
    /// </summary>
    SourceText? ClashingFile(Declaration declaration, string name)
    {
        var source = declaration.Location.Source;
        bool function = declaration is FunctionDecl;
        foreach (var file in source.Module.Files)
        {
            if (file == source || declaration.Visibility == Visibility.File || !fileNames.TryGetValue(file, out var other))
                continue;
            if (other.Types.ContainsKey(name) || (!function && other.Functions.ContainsKey(name)))
                return file;
        }
        var module = ModuleNames(source.Module);
        if (module.Types.TryGetValue(name, out var type) && type.Declaration.Location.Source != source)
            return type.Declaration.Location.Source;
        return function ? null : module.Functions.GetValueOrDefault(name)?.Select(f => f.Declaration?.Location.Source).FirstOrDefault(s => s != source);
    }

    /// <summary>The file-private functions of the name in the module's other files.</summary>
    IEnumerable<FunctionSymbol> PrivateElsewhere(string name, SourceText from) =>
        from.Module.Files.Where(f => f != from && fileNames.ContainsKey(f))
            .SelectMany(f => fileNames[f].Functions.GetValueOrDefault(name) ?? []);

    /// <summary>
    /// Gives each type that shares its name with another type of the program a name messages can
    /// tell apart (docs/diagnostics/README.md): its package and module path, as rustc trims
    /// paths, and the file too for file-private types of one module. Before any type is named, so
    /// every name built from it says the same.
    /// </summary>
    static void NameAmbiguousTypes(IReadOnlyList<TypeSymbol> types)
    {
        foreach (var same in types.GroupBy(t => t.Name).Where(g => g.Count() > 1))
        {
            foreach (var type in same)
            {
                var module = type.Declaration.Location.Source.Module;
                IEnumerable<string> path = module.Package.Name is { } package ? [package, .. module.Path, type.Name] : [.. module.Path, type.Name];
                type.DisplayName = string.Join("::", path);
            }
            foreach (var still in same.GroupBy(t => t.DisplayName).Where(g => g.Count() > 1).SelectMany(g => g))
                still.DisplayName += $" ({still.Declaration.Location.Source.Name})";
        }
    }

    /// <summary>
    /// The program's entry point: the one <c>main</c> of the package's root module, or null for a
    /// library, which must have none (spec programs.entry).
    /// </summary>
    FunctionSymbol? FindMain(Package package)
    {
        var root = package.Root;
        var candidates = root.Files.SelectMany(f => FileNames(f).Functions.GetValueOrDefault("main") ?? [])
            .Concat(ModuleNames(root).Functions.GetValueOrDefault("main") ?? [])
            .ToList();
        if (package.Kind == PackageKind.Library)
        {
            foreach (var candidate in candidates)
                Error(Catalog.LibraryMain(candidate.Declaration!.NameLocation, package.Name!));
            return null;
        }
        if (candidates.Count == 0)
        {
            if (root.Files.FirstOrDefault() is { } first)
                Error(Catalog.NoMain(new Location(first, new TextSpan(0, 0)), package.Name));
            return null;
        }

        FunctionSymbol? main = null;
        foreach (var candidate in candidates)
        {
            if (candidate.IsGeneric)
                Error(Catalog.MainParameters(candidate.Declaration!.NameLocation, typeParameters: true));
            else if (!TakesProgramArguments(candidate) && candidate.Parameters.Count != 0)
                Error(Catalog.MainParameters(candidate.Declaration!.NameLocation, typeParameters: false));
            else if (candidate.ReturnType != WylType.Void && candidate.ReturnType != WylType.Int)
                Error(Catalog.MainReturnType(candidate.Declaration!.NameLocation));
            else if (main is not null)
                Error(Catalog.DuplicateMain(candidate.Declaration!.NameLocation, main.Declaration!.NameLocation));
            else
                main = candidate;
        }
        return main;
    }

    /// <summary>Whether a <c>main</c> is declared <c>main(String[] args)</c>, the one parameter list it may have but none.</summary>
    bool TakesProgramArguments(FunctionSymbol main) =>
        main.Parameters is [{ RefKind: RefKind.None } parameter] && stdlib.IsStringArray(parameter.Type);
}
