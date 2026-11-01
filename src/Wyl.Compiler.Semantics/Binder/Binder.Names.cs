using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

/// <summary>
/// Top-level names (spec 06-syntax.md §6.1): each module's, which its files make visible beyond
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
    /// The nearest meaning of a top-level name where it is used (§6.1, Imports): the names the file
    /// and its module declare or re-export, then the file's named imports, then its <c>*</c> imports,
    /// which give way to both and are ambiguous only where used, then the types the language is
    /// built on. The first that has the name hides the rest, so overloads never merge across them.
    /// </summary>
    Entry ResolveName(string name, Location use)
    {
        var from = use.Source;
        var functions = OwnFunctions(name, from);
        var own = new Entry(OwnType(name, from), functions.Count > 0 ? functions : null, null);
        if (own.HasValue)
            return own;
        if (ReExported(from.Module, name, from) is { HasValue: true } reExported)
            return reExported;
        if (fileImports.TryGetValue(from, out var imports))
        {
            if (imports.Named.TryGetValue(name, out var imported) && Target(imported, from) is { HasValue: true } named)
                return named;
            if (FromAll(imports, name, from, use, modules: false) is { } all)
                return all;
        }
        return new Entry(stdlib.LanguageType(name), null, null);
    }

    /// <summary>The module a path's first name means where it is used: a submodule of the file's module, or one it imported.</summary>
    Module? ResolveModule(string name, Location use)
    {
        var from = use.Source;
        if (from.Module.FindChild(name) is { } child)
            return child;
        if (ReExported(from.Module, name, from) is { Module: { } reExported })
            return reExported;
        if (!fileImports.TryGetValue(from, out var imports))
            return null;
        if (imports.Named.TryGetValue(name, out var imported) && Target(imported, from) is { Module: { } module })
            return module;
        return FromAll(imports, name, from, use, modules: true)?.Module;
    }

    /// <summary>
    /// The one meaning a name has among a file's <c>*</c> imports, of those they make visible to it,
    /// in the namespace of modules or of everything else. Two different ones are an error where the
    /// name is used, and the first is taken.
    /// </summary>
    Entry? FromAll(ImportTable imports, string name, SourceText from, Location use, bool modules)
    {
        var found = new List<(Entry Entry, Module From)>();
        foreach (var all in imports.All)
        {
            var entry = VisibleTo(NamesOf(all.From, name, from), from);
            entry = modules ? new Entry(null, null, entry.Module) : entry with { Module = null };
            if (!entry.IsEmpty && !found.Any(f => f.Entry.SameAs(entry)))
                found.Add((entry, all.From));
        }
        if (found.Count > 1 && reportedAmbiguities.Add((use, name)))
            Error(use, $"'{name}' is ambiguous here: '*' imports it from both {DescribeModule(found[0].From)} and {DescribeModule(found[1].From)}; import the one meant by name");
        return found.Count > 0 ? found[0].Entry : null;
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
    /// The names a module has, as another module reaches them (§6.1): its files' declarations
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

    /// <summary>How a message names a module: <c>'world::gen'</c>, <c>'std'</c>, or the root module.</summary>
    static string DescribeModule(Module module)
    {
        var package = module.Package.Name;
        if (module.IsRoot)
            return package is null ? "the root module" : $"'{package}'";
        return package is null ? $"'{module}'" : $"'{package}::{module}'";
    }

    TypeSymbol? LookupType(string name, Location use) => ResolveName(name, use).Type;

    /// <summary>The overloads a name means where it is used; null if it means no function there.</summary>
    IReadOnlyList<FunctionSymbol>? LookupFunctions(string name, Location use) => ResolveName(name, use).Functions;

    /// <summary>
    /// How far a declaration of the name reaches that the file can't see but another file of its
    /// module has, so a miss says it is private there rather than that it doesn't exist.
    /// </summary>
    string? HiddenElsewhere(string name, SourceText from)
    {
        foreach (var file in from.Module.Files)
        {
            if (file == from || !fileNames.TryGetValue(file, out var table))
                continue;
            if (table.Types.TryGetValue(name, out var type))
                return $"'{name}' is visible {type.Reach.Describe()}";
            if (table.Functions.TryGetValue(name, out var functions))
                return $"function '{name}' is visible {functions[0].Reach.Describe()}";
        }
        return null;
    }

    /// <summary>
    /// Whether a declaration outside the standard library takes the name of a type the language is
    /// built on, which every file sees (§6.1, Imports), reporting it if so.
    /// </summary>
    bool IsLanguageName(Declaration declaration, string name, Location at)
    {
        if (declaration.Location.Source.Module.Package.IsStd || stdlib.LanguageType(name) is null)
            return false;
        Error(at, $"'{name}' is one of the types the language is built on, which every file sees, so no module may declare it");
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
    /// declares it at all, since that file would then see two meanings of it (§6.1). Functions of
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

    /// <summary>The program's entry point: the one <c>main</c> of the package's root module (§6.1, Entry point).</summary>
    FunctionSymbol? FindMain(Package package)
    {
        var root = package.Root;
        var candidates = root.Files.SelectMany(f => FileNames(f).Functions.GetValueOrDefault("main") ?? [])
            .Concat(ModuleNames(root).Functions.GetValueOrDefault("main") ?? [])
            .ToList();
        if (candidates.Count == 0)
        {
            if (root.Files.FirstOrDefault() is { } first)
                Error(new Location(first, new TextSpan(0, 0)), package.Name is { } name
                    ? $"package '{name}' has no 'main' function in its root module"
                    : "program has no 'main' function");
            return null;
        }

        FunctionSymbol? main = null;
        foreach (var candidate in candidates)
        {
            if (candidate.Parameters.Count != 0 || candidate.IsGeneric)
                Error(candidate.Declaration!.NameLocation, "'main' must not take parameters or type parameters");
            else if (candidate.ReturnType != WylType.Void && candidate.ReturnType != WylType.Int)
                Error(candidate.Declaration!.NameLocation, "'main' must return 'void' or 'int'");
            else if (main is not null)
                Error(candidate.Declaration!.NameLocation, $"the program already has a 'main', in {main.Declaration!.Location.Source.Name}");
            else
                main = candidate;
        }
        return main;
    }
}
