using Wyl.Compiler.Build;
using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler;

/// <summary>
/// The files of one package, each in its module (spec programs.modules), and the errors found
/// reading them: a package directory with its manifest and <c>src/</c>, or one file on its own.
/// </summary>
public sealed record PackageSources(Package Package, IReadOnlyList<SourceText> Files, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>The package's manifest, or null for a file compiled on its own.</summary>
    public Manifest? Manifest { get; init; }

    /// <summary>The package's <c>native/</c> directory and the manifest's <c>native</c> lists for this platform.</summary>
    public NativeInputs Native { get; init; } = NativeInputs.None;

    /// <summary>A file compiled on its own: the one file of its own unnamed package.</summary>
    public static PackageSources Of(SourceText file) => new(file.Module.Package, [file], []);

    /// <summary>Whether <paramref name="path"/> is a package: a directory with a manifest.</summary>
    public static bool IsPackage(string path) => File.Exists(Path.Combine(path, Manifest.FileName));

    /// <summary>
    /// Reads the package in <paramref name="directory"/>: its manifest, then every <c>.wyl</c> file
    /// under <c>src/</c>, each directory of them a module named by its path. Directories without any
    /// are no modules, and neither are those whose names start with '.', like a build's output.
    /// </summary>
    public static PackageSources Load(string directory)
    {
        var diagnostics = new DiagnosticBag();
        var manifestText = SourceText.FromFile(Path.Combine(directory, Manifest.FileName));
        if (Manifest.Read(manifestText, diagnostics) is not { } manifest)
            return new PackageSources(Package.Unnamed(), [], diagnostics.Sorted());

        var package = Package.Named(manifest.Name, manifest.Kind);
        var src = Path.Combine(directory, "src");
        if (!Directory.Exists(src))
        {
            diagnostics.Add(Catalog.MissingSourceDirectory(manifest.NameLocation));
            return new PackageSources(package, [], diagnostics.Sorted()) { Manifest = manifest };
        }
        var files = new List<SourceText>();
        AddModule(src, package.Root, files, diagnostics);
        if (files.Count == 0)
            diagnostics.Add(Catalog.NoSourceFiles(manifest.NameLocation));
        var native = ReadNative(directory, manifest, diagnostics);
        return new PackageSources(package, files, diagnostics.Sorted()) { Manifest = manifest, Native = native };

        void AddModule(string path, Module module, List<SourceText> into, DiagnosticBag bag)
        {
            foreach (var file in Directory.GetFiles(path, "*.wyl").Order(StringComparer.Ordinal))
            {
                var name = Path.GetRelativePath(src, file).Replace('\\', '/');
                into.Add(SourceText.FromFile(file, module, name));
            }
            foreach (var child in Directory.GetDirectories(path).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || Directory.GetFiles(child, "*.wyl", SearchOption.AllDirectories).Length == 0)
                    continue;
                int first = into.Count;
                AddModule(child, module.Child(name), into, bag);
                // Reported in the module's first file, since a directory has no text to point at.
                var at = new Location(into[first], new TextSpan(0, 0));
                if (!Lexer.IsIdentifier(name))
                    bag.Add(Catalog.DirectoryNotModuleName(at, name));
                // So that a path's first name has one meaning (spec programs.paths).
                else if (module.IsRoot && name == Package.Std.Name)
                    bag.Add(Catalog.StdModuleName(at));
            }
        }
    }

    /// <summary>
    /// The package's <c>native/</c> directory and the lists of its manifest's <c>native</c> block for
    /// this platform, with each path made absolute from the package's directory. A directory of
    /// <c>include</c> or <c>libraryPaths</c>, or a file of <c>copy</c>, that does not exist is an error.
    /// </summary>
    static NativeInputs ReadNative(string directory, Manifest manifest, DiagnosticBag diagnostics)
    {
        var lists = manifest.NativeFor(NativeInputs.Platform).ToList();
        List<string> Paths(string key, bool directories)
        {
            List<string> paths = [];
            foreach (var list in lists.Where(l => l.Key == key))
            {
                foreach (var value in list.Values)
                {
                    var path = Path.GetFullPath(Path.Combine(directory, value));
                    if (directories ? !Directory.Exists(path) : !File.Exists(path))
                        diagnostics.Add(Catalog.MissingNativePath(list.KeyLocation, key, value, directories ? "directory" : "file"));
                    paths.Add(path);
                }
            }
            return paths;
        }
        List<string> Values(string key) => [.. lists.Where(l => l.Key == key).SelectMany(l => l.Values)];

        var native = Path.GetFullPath(Path.Combine(directory, "native"));
        return new NativeInputs(
            Directory.Exists(native) ? native : null,
            Paths("include", directories: true),
            Paths("libraryPaths", directories: true),
            Values("link"),
            Paths("copy", directories: false),
            Values("defines"));
    }
}
