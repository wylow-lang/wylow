using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wyl.Compiler.Diagnostics;

namespace Wyl.Compiler.Build;

public sealed record BuildOptions(bool Release = false, Toolchain? Toolchain = null);

public sealed record ExecutableBuild(string? ExePath, string? Error)
{
    public bool Success => ExePath is not null;

    /// <summary>
    /// The errors of a build that failed because a header or a native source does not match the
    /// program's extern blocks, each at the declaration it is about.
    /// </summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];
}

/// <summary>
/// What building a program from its source produced: its diagnostics as <c>wyl</c> prints them, how
/// many of them are errors, and the executable, which is null if there were errors.
/// </summary>
public sealed record ProgramBuild(IReadOnlyList<string> Diagnostics, int Errors, ExecutableBuild? Executable);

public static class Driver
{
    /// <summary>
    /// Compiles a program and builds its executable in <paramref name="outputDirectory"/>. If the
    /// executable there was built from the same sources with the same options by the same compiler,
    /// the program is not compiled again, and the warnings of the build that made it are reported again.
    /// </summary>
    public static ProgramBuild Build(PackageSources sources, CompileOptions compileOptions, string name, string outputDirectory, BuildOptions options)
    {
        var compiler = NativeCompiler.Find(options.Toolchain, out var error);
        var exePath = ExePath(outputDirectory, name);
        var keyPath = Path.Combine(outputDirectory, name + ".source-key");
        var diagnosticsPath = Path.Combine(outputDirectory, name + ".diagnostics.json");
        var key = compiler is null ? null : SourceKey(compiler, sources, compileOptions, options.Release);
        if (key is not null && File.Exists(exePath) && ReadText(keyPath) == key && ReadDiagnostics(diagnosticsPath) is { } kept)
            return new ProgramBuild(kept, 0, new ExecutableBuild(exePath, null));

        var compilation = Compilation.Compile(sources, compileOptions);
        var diagnostics = compilation.Diagnostics.Select(d => d.ToString()).ToList();
        if (!compilation.Success)
            return new ProgramBuild(diagnostics, compilation.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error), null);
        if (compiler is null)
            return new ProgramBuild(diagnostics, 0, new ExecutableBuild(null, error));

        // A build that fails partway may have replaced the executable, so the old key goes first.
        Directory.CreateDirectory(outputDirectory);
        File.Delete(keyPath);
        var build = BuildExecutable(compilation, compiler, name, outputDirectory, options.Release);
        if (build.Diagnostics.Count > 0)
            return new ProgramBuild([.. diagnostics, .. build.Diagnostics.Select(d => d.ToString())], build.Diagnostics.Count, null);
        if (build.Success && key is not null)
        {
            File.WriteAllText(diagnosticsPath, JsonSerializer.Serialize(diagnostics));
            File.WriteAllText(keyPath, key);
        }
        return new ProgramBuild(diagnostics, 0, build);
    }

    public static ExecutableBuild BuildExecutable(CompilationResult compilation, string name, string outputDirectory, BuildOptions options)
    {
        if (compilation.Cpp is null)
            throw new ArgumentException("cannot build a compilation that has errors", nameof(compilation));

        var compiler = NativeCompiler.Find(options.Toolchain, out var error);
        if (compiler is null)
            return new ExecutableBuild(null, error);
        return BuildExecutable(compilation, compiler, name, outputDirectory, options.Release);
    }

    static ExecutableBuild BuildExecutable(CompilationResult compilation, NativeCompiler compiler, string name, string outputDirectory, bool release)
    {
        var cpp = compilation.Cpp!;
        Directory.CreateDirectory(outputDirectory);
        var cppPath = Path.Combine(outputDirectory, name + ".cpp");
        File.WriteAllText(cppPath, cpp);
        var exePath = ExePath(outputDirectory, name);
        var keyPath = Path.Combine(outputDirectory, name + ".key");
        var key = compiler.BuildKey(release, cpp, compilation.NativeDirectory);
        if (File.Exists(exePath) && ReadText(keyPath) == key)
            return new ExecutableBuild(exePath, null);

        File.Delete(keyPath);
        var result = compiler.Build(cppPath, exePath, release, nativeDirectory: compilation.NativeDirectory);
        if (result.Success)
            File.WriteAllText(keyPath, key);
        return result.Success
            ? new ExecutableBuild(exePath, null)
            : BuildFailure.Classify(result.Output, cppPath, compilation.Interop, compilation.NativeDirectory);
    }

    /// <summary>
    /// Everything a build from source depends on: the Wylow compiler, the options, every source file
    /// with the path and module it has in messages, and what <see cref="NativeCompiler.BuildKey"/>
    /// covers besides the C++. Equal keys mean that the executable a previous build left is the one
    /// this build would produce. Null if the compiler's own files cannot be found to read.
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
        Add(compiler.Identity(release));
        // A record's text names every one of its properties, so an option added later is in the key too.
        Add(compileOptions.ToString());
        Add(sources.Package.Name ?? "");
        foreach (var diagnostic in sources.Diagnostics)
            Add(diagnostic.ToString());
        foreach (var (path, bytes) in NativeCompiler.NativeFiles(sources.NativeDirectory))
        {
            Add(path);
            Add(Convert.ToBase64String(bytes));
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
        var directory = Path.GetDirectoryName(typeof(Driver).Assembly.Location);
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

    static string ExePath(string outputDirectory, string name) =>
        Path.Combine(outputDirectory, name + (OperatingSystem.IsWindows() ? ".exe" : ""));

    static string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    static List<string>? ReadDiagnostics(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
