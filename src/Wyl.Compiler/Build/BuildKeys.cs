using System.Security.Cryptography;
using System.Text;

namespace Wyl.Compiler.Build;

/// <summary>
/// What a build reads besides the generated C++. Equal keys mean the executable or object that an
/// earlier build left is the one this build would produce.
/// </summary>
public static class BuildKeys
{
    /// <summary>
    /// Everything a build depends on besides the program's own directory: the generated C++, the flags,
    /// the runtime headers, the C++ compiler itself, the <c>native</c> lists of every package's
    /// manifest, and the files that <see cref="NativeInputs.Files"/> gives. Equal keys mean the
    /// executable a previous build left is the one this build would produce.
    /// </summary>
    public static string BuildKey(NativeCompiler compiler, bool release, string cpp, NativeInputs? native = null)
    {
        native ??= NativeInputs.None;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{Identity(compiler, release)}\n{cpp}\n{native.Text}"));
        foreach (var (path, bytes) in native.Files())
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"\n{path}\n{bytes.Length}\n"));
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>What a build depends on besides the C++ it compiles: the toolchain, the mode, the C++ compiler itself, and the runtime headers.</summary>
    internal static string Identity(NativeCompiler compiler, bool release)
    {
        var executable = new FileInfo(compiler.CompilerPath);
        return $"{compiler.Toolchain}|{release}|{compiler.CompilerPath}|{executable.Length}|{executable.LastWriteTimeUtc.Ticks}|{RuntimeContentHash.Value}";
    }

    static readonly Lazy<string> RuntimeContentHash = new(() =>
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(NativeCompiler.RuntimeDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(NativeCompiler.RuntimeDirectory, file)));
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    });

    /// <summary>
    /// Everything a build from source depends on: the Wylow compiler, the options, every source file
    /// with the path and module it has in messages, and what <see cref="BuildKey"/> covers besides the
    /// C++. Equal keys mean that the executable a previous build left is the one this build would
    /// produce. Null if the compiler's own files cannot be found to read.
    /// </summary>
    public static string? SourceKey(NativeCompiler compiler, PackageSources sources, CompileOptions compileOptions, bool release)
    {
        if (WylowCompilerHash.Value is not { } wylow)
            return null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        Add(wylow);
        Add(Identity(compiler, release));
        // A record's text names every one of its properties, so an option added later is in the key too.
        Add(compileOptions.ToString());
        Add(sources.Package.Name ?? "");
        foreach (var diagnostic in sources.Diagnostics)
            Add(diagnostic.ToString());
        Add(sources.Native.Text);
        foreach (var (path, bytes) in sources.Native.Files())
        {
            Add(path);
            Add(Convert.ToBase64String(bytes));
        }
        foreach (var manifest in sources.Manifests)
        {
            Add(manifest.Path);
            Add(manifest.Text);
        }
        foreach (var file in sources.Files)
        {
            Add(file.Path);
            Add(file.Name);
            Add(file.Module.ToString());
            Add(file.InvalidUtf8At?.ToString() ?? "");
            Add(file.Text);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>A hash of the compiler's assemblies, the standard library among them, or null where they aren't files.</summary>
    static readonly Lazy<string?> WylowCompilerHash = new(() =>
    {
        var directory = Path.GetDirectoryName(typeof(BuildKeys).Assembly.Location);
        if (string.IsNullOrEmpty(directory))
            return null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(directory, "Wyl.Compiler*.dll").Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file)));
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    });
}
