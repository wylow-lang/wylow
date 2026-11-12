using System.Text;

namespace Wyl.Compiler.Build;

/// <summary>
/// What a program's C++ build takes from its package besides the generated C++: the package's
/// <c>native/</c> directory (spec <c>interop.native</c>) and the lists of the manifest's <c>native</c>
/// block that apply on this platform (spec <c>programs.manifest</c>). Every path is absolute.
/// </summary>
public sealed record NativeInputs(
    string? Directory,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> LibraryPaths,
    IReadOnlyList<string> Link,
    IReadOnlyList<string> Copy,
    IReadOnlyList<string> Defines)
{
    public static readonly NativeInputs None = new(null, [], [], [], [], []);

    /// <summary>The platform that this compiler builds programs for, as a platform block of the manifest names it.</summary>
    public static string Platform =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>Every <c>.cpp</c> file under <see cref="Directory"/>, by full path, in a stable order.</summary>
    public IReadOnlyList<string> Sources => Directory is null
        ? []
        : [.. System.IO.Directory.GetFiles(Directory, "*.cpp", SearchOption.AllDirectories).Select(Path.GetFullPath).Order(StringComparer.Ordinal)];

    /// <summary>The directories on the include path besides the runtime's: <c>native/</c>, then each of <see cref="Include"/>.</summary>
    public IEnumerable<string> IncludeDirectories => Directory is null ? Include : Include.Prepend(Directory);

    /// <summary>The lists, as text that differs whenever one of them does.</summary>
    public string Text
    {
        get
        {
            var text = new StringBuilder();
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
    /// Every file that a build reads from the package besides its Wylow sources, with its bytes, in a
    /// stable order: each file under <c>native/</c> and under each directory of <see cref="Include"/>,
    /// each library that <see cref="Link"/> names and a directory of <see cref="LibraryPaths"/> holds,
    /// and each file of <see cref="Copy"/>.
    /// </summary>
    public IEnumerable<(string Path, byte[] Bytes)> Files()
    {
        foreach (var directory in IncludeDirectories.Where(System.IO.Directory.Exists))
        {
            var files = System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .Order(StringComparer.Ordinal);
            foreach (var file in files)
                yield return (file, File.ReadAllBytes(file));
        }
        var libraries = LibraryPaths.SelectMany(directory => Link.SelectMany(LibraryFileNames).Select(name => Path.Combine(directory, name)));
        foreach (var file in libraries.Concat(Copy).Where(File.Exists))
            yield return (file, File.ReadAllBytes(file));
    }

    /// <summary>The names of the files that a toolchain may link for the library <paramref name="name"/>.</summary>
    static IEnumerable<string> LibraryFileNames(string name) =>
        [$"{name}.lib", $"lib{name}.a", $"lib{name}.dll.a", $"lib{name}.so", $"lib{name}.dylib"];
}
