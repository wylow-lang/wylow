using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

/// <summary>
/// Top-level names (spec 06-syntax.md §6.1): each module's, which its files make visible beyond
/// themselves, and each file's file-private ones, which are kept out of the module's so that two
/// files may each have a private helper of one name. A file sees its own names, its module's, and
/// the standard library's, and every lookup of a top-level name goes through <see cref="Visible"/>.
/// </summary>
public sealed partial class Binder
{
    sealed class NameTable
    {
        public readonly Dictionary<string, TypeSymbol> Types = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<FunctionSymbol>> Functions = new(StringComparer.Ordinal);

        public bool Declares(string name) => Types.ContainsKey(name) || Functions.ContainsKey(name);
    }

    readonly Dictionary<Module, NameTable> moduleNames = [];
    readonly Dictionary<SourceText, NameTable> fileNames = [];
    readonly Dictionary<string, List<FunctionSymbol>> builtins = new(StringComparer.Ordinal);

    NameTable ModuleNames(Module module) => moduleNames.TryGetValue(module, out var table) ? table : moduleNames[module] = new();

    NameTable FileNames(SourceText file) => fileNames.TryGetValue(file, out var table) ? table : fileNames[file] = new();

    /// <summary>Where a top-level declaration's name goes: its file's names if it is file-private, else its module's.</summary>
    NameTable TableOf(Declaration declaration) => declaration.Visibility == Visibility.File
        ? FileNames(declaration.Location.Source)
        : ModuleNames(declaration.Location.Source.Module);

    /// <summary>The names a file sees, nearest first: its own, its module's, then the standard library's.</summary>
    IEnumerable<NameTable> Visible(SourceText from)
    {
        yield return FileNames(from);
        yield return ModuleNames(from.Module);
        if (from.Module != Package.Std.Root)
            yield return ModuleNames(Package.Std.Root);
    }

    TypeSymbol? LookupType(string name, SourceText from)
    {
        foreach (var table in Visible(from))
        {
            if (table.Types.TryGetValue(name, out var type))
                return type;
        }
        return null;
    }

    /// <summary>Every function of the name the file sees, and the built-in ones; null if there is none.</summary>
    List<FunctionSymbol>? LookupFunctions(string name, SourceText from)
    {
        List<FunctionSymbol> found = [.. Visible(from).SelectMany(t => t.Functions.GetValueOrDefault(name) ?? []), .. builtins.GetValueOrDefault(name) ?? []];
        return found.Count > 0 ? found : null;
    }

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

    void AddBuiltin(FunctionSymbol function)
    {
        if (!builtins.TryGetValue(function.Name, out var overloads))
            builtins[function.Name] = overloads = [];
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
