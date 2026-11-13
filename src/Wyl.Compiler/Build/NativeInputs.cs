using System.Text;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Build;

/// <summary>
/// What one package gives a program's C++ build: the package's <c>native/</c> directory (spec
/// <c>interop.native</c>) and the lists of its manifest's <c>native</c> block that apply on this
/// platform (spec <c>programs.manifest</c>). Every path is absolute.
/// </summary>
public sealed record PackageNative(
    Package Package,
    string? Directory,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> LibraryPaths,
    IReadOnlyList<string> Link,
    IReadOnlyList<string> Copy,
    IReadOnlyList<string> Defines)
{
    /// <summary>Every <c>.cpp</c> and <c>.c</c> file under <see cref="Directory"/>, by full path, in a stable order.</summary>
    public IReadOnlyList<string> Sources => Directory is null
        ? []
        : [.. System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f) is ".cpp" or ".c")
            .Select(Path.GetFullPath)
            .Order(StringComparer.Ordinal)];

    /// <summary>The package's own include path: <c>native/</c>, then each of <see cref="Include"/>.</summary>
    public IEnumerable<string> IncludeDirectories => Directory is null ? Include : Include.Prepend(Directory);

    /// <summary>
    /// The full path of the header <paramref name="header"/> in the package's own include path, or
    /// null if none of its directories holds it (spec <c>interop.native</c>).
    /// </summary>
    public string? FindHeader(string header) =>
        IncludeDirectories.Select(d => Path.GetFullPath(Path.Combine(d, header))).FirstOrDefault(File.Exists);

    /// <summary>The package's name and lists, as text that differs whenever one of them does.</summary>
    public string Text
    {
        get
        {
            var text = new StringBuilder();
            text.Append("package:").Append(Package.Name).Append('\n');
            text.Append("native:").Append(Directory).Append('\n');
            foreach (var (key, list) in new[] { ("include", Include), ("libraryPaths", LibraryPaths), ("link", Link), ("copy", Copy), ("defines", Defines) })
            {
                text.Append(key).Append(':').Append(list.Count).Append('\n');
                foreach (var item in list)
                    text.Append(item.Length).Append(':').Append(item).Append('\n');
            }
            return text.ToString();
        }
    }

    /// <summary>
    /// Every file that one of the package's native sources can include besides the runtime's and the
    /// toolchain's, with its bytes, in a stable order: each file under <c>native/</c> and under each
    /// directory of <see cref="Include"/>. The native sources are among them.
    /// </summary>
    public IEnumerable<(string Path, byte[] Bytes)> IncludableFiles()
    {
        foreach (var directory in IncludeDirectories.Where(System.IO.Directory.Exists))
        {
            var files = System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .Order(StringComparer.Ordinal);
            foreach (var file in files)
                yield return (file, File.ReadAllBytes(file));
        }
    }
}

/// <summary>
/// What a program's C++ build takes from its packages besides the generated C++: the
/// <see cref="PackageNative"/> of each package of the program that has files, the program's own first.
/// </summary>
public sealed record NativeInputs(IReadOnlyList<PackageNative> Packages)
{
    public static readonly NativeInputs None = new([]);

    /// <summary>The platform that this compiler builds programs for, as a platform block of the manifest names it.</summary>
    public static string Platform =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>Whether a native source is C, which compiles as C17, rather than C++, which compiles as C++20 (spec <c>interop.native</c>).</summary>
    public static bool IsC(string source) => Path.GetExtension(source) == ".c";

    /// <summary>Every native source of every package, with its package, in a stable order.</summary>
    public IReadOnlyList<(PackageNative Package, string Path)> Sources =>
        [.. Packages.SelectMany(p => p.Sources.Select(s => (p, s)))];

    /// <summary>
    /// The include path of the generated program besides the runtime's: each package's own include
    /// path, in package order. The program includes an extern block's header by its full path, so
    /// this path serves only the headers that those headers include.
    /// </summary>
    public IReadOnlyList<string> IncludeDirectories => Distinct(Packages.SelectMany(p => p.IncludeDirectories));

    /// <summary>The definitions of every package, which the generated program compiles with.</summary>
    public IReadOnlyList<string> Defines => Distinct(Packages.SelectMany(p => p.Defines));

    /// <summary>The libraries that every package names in <c>link</c>, which the program links.</summary>
    public IReadOnlyList<string> Link => Distinct(Packages.SelectMany(p => p.Link));

    /// <summary>The directories of every package's <c>libraryPaths</c>, where the linker looks for <see cref="Link"/>.</summary>
    public IReadOnlyList<string> LibraryPaths => Distinct(Packages.SelectMany(p => p.LibraryPaths));

    /// <summary>The files of every package's <c>copy</c>, which go next to the executable.</summary>
    public IReadOnlyList<string> Copy => Distinct(Packages.SelectMany(p => p.Copy));

    /// <summary>
    /// The path that the generated program includes an extern block's header by: its full path in
    /// the include path of the block's own package, or the path as written if that package does not
    /// hold it, as for the standard library's headers.
    /// </summary>
    public string HeaderPath(Package package, string header) =>
        Packages.FirstOrDefault(p => p.Package == package)?.FindHeader(header)?.Replace('\\', '/') ?? header;

    /// <summary>Every package's lists, as text that differs whenever one of them does.</summary>
    public string Text => string.Concat(Packages.Select(p => p.Text));

    /// <summary>
    /// Every file that a build reads from the packages besides their Wylow sources, with its bytes, in
    /// a stable order: each package's <see cref="PackageNative.IncludableFiles"/>, each library that
    /// <see cref="Link"/> names and a directory of <see cref="LibraryPaths"/> holds, and each file of
    /// <see cref="Copy"/>.
    /// </summary>
    public IEnumerable<(string Path, byte[] Bytes)> Files()
    {
        foreach (var file in Packages.SelectMany(p => p.IncludableFiles()))
            yield return file;
        var libraries = LibraryPaths.SelectMany(directory => Link.SelectMany(LibraryFileNames).Select(name => Path.Combine(directory, name)));
        foreach (var file in libraries.Concat(Copy).Where(File.Exists))
            yield return (file, File.ReadAllBytes(file));
    }

    static List<string> Distinct(IEnumerable<string> items) => [.. items.Distinct(StringComparer.Ordinal)];

    /// <summary>The names of the files that a toolchain may link for the library <paramref name="name"/>.</summary>
    static IEnumerable<string> LibraryFileNames(string name) =>
        [$"{name}.lib", $"lib{name}.a", $"lib{name}.dll.a", $"lib{name}.so", $"lib{name}.dylib"];
}
