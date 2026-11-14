using Wyl.Compiler.Build;
using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler;

/// <summary>
/// The files of a package and of every library it depends on, each in its module (spec
/// programs.modules), and the errors found reading them: a package directory with its manifest and
/// <c>src/</c>, or one file on its own. <see cref="Package"/> is the package that was asked for.
/// </summary>
public sealed record PackageSources(Package Package, IReadOnlyList<SourceText> Files, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>The package's manifest, or null for a file compiled on its own.</summary>
    public Manifest? Manifest { get; init; }

    /// <summary>The manifest of every package that was read, the package's own among them.</summary>
    public IReadOnlyList<SourceText> Manifests { get; init; } = [];

    /// <summary>Each package's <c>native/</c> directory and its manifest's <c>native</c> lists for this platform.</summary>
    public NativeInputs Native { get; init; } = NativeInputs.None;

    /// <summary>A file compiled on its own: the one file of its own unnamed package.</summary>
    public static PackageSources Of(SourceText file) => new(file.Module.Package, [file], []);

    /// <summary>Whether <paramref name="path"/> is a package: a directory with a manifest.</summary>
    public static bool IsPackage(string path) => File.Exists(Path.Combine(path, Manifest.FileName));

    /// <summary>
    /// Reads the package in <paramref name="directory"/> and every library it depends on: each one's
    /// manifest, then every <c>.wyl</c> file under its <c>src/</c>, each directory of them a module
    /// named by its path. Directories without any are no modules, and neither are those whose names
    /// start with '.', like a build's output.
    /// </summary>
    public static PackageSources Load(string directory)
    {
        var loader = new Loader();
        if (loader.LoadRoot(directory) is not var (package, manifest))
            return new PackageSources(Package.Unnamed(), [], loader.Diagnostics.Sorted()) { Manifests = loader.Manifests };
        var native = loader.ReadNative(package);
        return new PackageSources(package, loader.Files, loader.Diagnostics.Sorted()) { Manifest = manifest, Manifests = loader.Manifests, Native = native };
    }

    /// <summary>
    /// Reads a package and its dependencies, each directory once, and checks the graph of
    /// dependencies (spec programs.manifest.dependencies). A package is identified by its directory's
    /// full path. A dependency that breaks a rule is reported and left out of the graph.
    /// </summary>
    sealed class Loader
    {
        static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        public readonly DiagnosticBag Diagnostics = new();
        public readonly List<SourceText> Files = [];
        public readonly List<SourceText> Manifests = [];
        readonly Dictionary<string, Manifest?> manifests = new(PathComparer);
        readonly Dictionary<string, Package> loaded = new(PathComparer);
        readonly Dictionary<string, (string Directory, Location At)> named = new(StringComparer.Ordinal);
        // The packages whose dependencies are being read, from the program down, to find a cycle.
        readonly List<(string Directory, string Name)> loading = [];
        // Every package that was read, each after its dependencies.
        readonly List<(string Directory, Manifest Manifest, Package Package)> packages = [];

        static string Identity(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

        public (Package, Manifest)? LoadRoot(string directory)
        {
            if (ReadManifest(directory) is not { } manifest)
                return null;
            named[manifest.Name] = (Identity(directory), manifest.NameLocation);
            return (Load(directory, manifest, root: true), manifest);
        }

        Manifest? ReadManifest(string directory)
        {
            var identity = Identity(directory);
            if (!manifests.TryGetValue(identity, out var manifest))
            {
                var text = SourceText.FromFile(Path.Combine(directory, Manifest.FileName));
                Manifests.Add(text);
                manifests[identity] = manifest = Manifest.Read(text, Diagnostics);
            }
            return manifest;
        }

        Package Load(string directory, Manifest manifest, bool root)
        {
            loading.Add((Identity(directory), manifest.Name));
            var dependencies = new Dictionary<string, Package>(StringComparer.Ordinal);
            foreach (var dependency in manifest.Dependencies)
            {
                if (Resolve(directory, dependency) is { } library)
                    dependencies[dependency.Name] = library;
            }
            loading.RemoveAt(loading.Count - 1);

            var package = Package.Named(manifest.Name, manifest.Kind, dependencies);
            loaded[Identity(directory)] = package;
            packages.Add((directory, manifest, package));
            ReadFiles(directory, package, manifest, root ? "" : manifest.Name + "::");
            // So that a path's first name has one meaning (spec programs.paths).
            foreach (var dependency in manifest.Dependencies.Where(d => package.Root.FindChild(d.Name) is not null))
                Diagnostics.Add(Catalog.DependencyModuleName(dependency.KeyLocation, dependency.Name));
            return package;
        }

        /// <summary>The library that a dependency entry names, or null after reporting why it is not one.</summary>
        Package? Resolve(string from, Manifest.Dependency dependency)
        {
            var identity = Identity(Path.Combine(from, dependency.Path));
            // Messages name a dependency's files as the package's own: relative if the package's directory was.
            var directory = Path.IsPathRooted(from) ? identity : Path.GetRelativePath(".", identity);
            if (!IsPackage(directory))
            {
                Diagnostics.Add(Catalog.DependencyNotPackage(dependency.PathLocation, dependency.Path));
                return null;
            }
            if (ReadManifest(directory) is not { } manifest)
                return null;
            if (manifest.Name != dependency.Name)
            {
                Diagnostics.Add(Catalog.DependencyNameMismatch(dependency.KeyLocation, dependency.Name, manifest.Name));
                return null;
            }
            if (manifest.Kind != PackageKind.Library)
            {
                Diagnostics.Add(Catalog.DependencyNotLibrary(dependency.KeyLocation, dependency.Name, manifest.NameLocation));
                return null;
            }
            if (dependency.Version is { } stated && manifest.Version is { } version && !version.Satisfies(stated.Version))
                Diagnostics.Add(Catalog.UnsatisfiedVersion(stated.Location, manifest.Name, version, stated.Text, manifest.VersionLocation!.Value));
            if (loading.FindIndex(l => PathComparer.Equals(l.Directory, identity)) is >= 0 and var start)
            {
                Diagnostics.Add(Catalog.DependencyCycle(dependency.KeyLocation, [.. loading.Skip(start).Select(l => l.Name), manifest.Name]));
                return null;
            }
            if (named.TryGetValue(manifest.Name, out var other) && !PathComparer.Equals(other.Directory, identity))
            {
                Diagnostics.Add(Catalog.DuplicatePackageName(dependency.KeyLocation, manifest.Name, other.At));
                return null;
            }
            named[manifest.Name] = (identity, manifest.NameLocation);
            return loaded.TryGetValue(identity, out var package) ? package : Load(directory, manifest, root: false);
        }

        /// <summary>Reads the package's files, each named by its path under <c>src/</c> after <paramref name="prefix"/>, which is a library's name and <c>::</c>.</summary>
        void ReadFiles(string directory, Package package, Manifest manifest, string prefix)
        {
            var src = Path.Combine(directory, "src");
            if (!Directory.Exists(src))
            {
                Diagnostics.Add(Catalog.MissingSourceDirectory(manifest.NameLocation));
                return;
            }
            int first = Files.Count;
            AddModule(src, src, prefix, package.Root, Files, Diagnostics);
            if (Files.Count == first)
                Diagnostics.Add(Catalog.NoSourceFiles(manifest.NameLocation));
        }

        /// <summary>
        /// The native settings of every package that has files, <paramref name="program"/>'s first
        /// (spec programs.manifest.dependencies). Two packages that define one name with different
        /// values are an error, and so are two files of <c>copy</c> with one file name. Each is
        /// reported at the list of the package that comes later in an order where every package
        /// comes after its dependencies.
        /// </summary>
        public NativeInputs ReadNative(Package program)
        {
            var defined = new Dictionary<string, (string Value, Package Package, Location At)>(StringComparer.Ordinal);
            var copied = new Dictionary<string, (string Path, Package Package, Location At)>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            List<PackageNative> natives = [];
            foreach (var (directory, manifest, package) in packages.Where(p => Files.Any(f => f.Module.Package == p.Package)))
            {
                var native = ReadNative(directory, manifest, package);
                natives.Add(native);
                foreach (var list in manifest.NativeFor(NativeInputs.Platform).Where(l => l.Key == "defines"))
                {
                    foreach (var define in list.Values)
                    {
                        var (name, value) = SplitDefine(define);
                        if (!defined.TryGetValue(name, out var other))
                            defined[name] = (value, package, list.KeyLocation);
                        else if (other.Package != package && other.Value != value)
                            Diagnostics.Add(Catalog.ConflictingDefine(list.KeyLocation, name, value, other.Value, other.Package.Name!, other.At));
                    }
                }
                foreach (var list in manifest.NativeFor(NativeInputs.Platform).Where(l => l.Key == "copy"))
                {
                    foreach (var value in list.Values)
                    {
                        var path = Path.GetFullPath(Path.Combine(directory, value));
                        var name = Path.GetFileName(path);
                        if (!copied.TryGetValue(name, out var other))
                            copied[name] = (path, package, list.KeyLocation);
                        else if (!PathComparer.Equals(other.Path, path))
                            Diagnostics.Add(Catalog.ConflictingCopy(list.KeyLocation, name, other.Package.Name!, other.Package == package ? null : other.At));
                    }
                }
            }
            return new NativeInputs([.. natives.Where(n => n.Package == program), .. natives.Where(n => n.Package != program)]);
        }

        /// <summary>A definition's name and value: the text before and after its '=', where a definition without one has the value <c>1</c>, as a C compiler gives it.</summary>
        static (string Name, string Value) SplitDefine(string define) =>
            define.IndexOf('=', StringComparison.Ordinal) is >= 0 and var at ? (define[..at], define[(at + 1)..]) : (define, "1");

        /// <summary>
        /// The package's <c>native/</c> directory and the lists of its manifest's <c>native</c> block for
        /// this platform, with each path made absolute from the package's directory. A directory of
        /// <c>include</c> or <c>libraryPaths</c>, or a file of <c>copy</c>, that does not exist is an error.
        /// </summary>
        PackageNative ReadNative(string directory, Manifest manifest, Package package)
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
                            Diagnostics.Add(Catalog.MissingNativePath(list.KeyLocation, key, value, directories ? "directory" : "file"));
                        paths.Add(path);
                    }
                }
                return paths;
            }
            List<string> Values(string key) => [.. lists.Where(l => l.Key == key).SelectMany(l => l.Values)];

            var native = Path.GetFullPath(Path.Combine(directory, "native"));
            var fetched = manifest.Fetch is { } fetch ? Downloads.Ensure(fetch, Diagnostics) : null;
            return new PackageNative(
                package,
                Path.GetFullPath(directory),
                Directory.Exists(native) ? native : null,
                Paths("include", directories: true),
                Paths("libraryPaths", directories: true),
                Values("link"),
                Paths("copy", directories: false),
                Values("defines"))
            {
                Fetch = manifest.Fetch,
                FetchedFiles = fetched?.Files ?? [],
                FetchedIncludeDirectories = fetched?.IncludeDirectories ?? [],
                FetchedSources = fetched?.Sources ?? [],
            };
        }

        static void AddModule(string src, string path, string prefix, Module module, List<SourceText> into, DiagnosticBag bag)
        {
            foreach (var file in Directory.GetFiles(path, "*.wyl").Order(StringComparer.Ordinal))
            {
                var name = prefix + Path.GetRelativePath(src, file).Replace('\\', '/');
                into.Add(SourceText.FromFile(file, module, name));
            }
            foreach (var child in Directory.GetDirectories(path).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || Directory.GetFiles(child, "*.wyl", SearchOption.AllDirectories).Length == 0)
                    continue;
                int first = into.Count;
                AddModule(src, child, prefix, module.Child(name), into, bag);
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
}
