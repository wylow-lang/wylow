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
        var compiler = Toolchains.Find(options.Toolchain, out var error);
        var exePath = ExePath(outputDirectory, name);
        var keyPath = Path.Combine(outputDirectory, name + ".source-key");
        var diagnosticsPath = Path.Combine(outputDirectory, name + ".diagnostics.json");
        var key = compiler is null ? null : BuildKeys.SourceKey(compiler, sources, compileOptions, options.Release);
        if (key is not null && File.Exists(exePath) && ReadText(keyPath) == key && ReadDiagnostics(diagnosticsPath) is { } kept)
            return new ProgramBuild(kept, 0, CopyNextTo(exePath, sources.Native));

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

        var compiler = Toolchains.Find(options.Toolchain, out var error);
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
        var key = BuildKeys.BuildKey(compiler, release, cpp, compilation.Native);
        if (File.Exists(exePath) && ReadText(keyPath) == key)
            return CopyNextTo(exePath, compilation.Native);

        File.Delete(keyPath);
        var result = compiler.Build(cppPath, exePath, release, native: compilation.Native);
        if (!result.Success)
            return BuildFailure.Classify(result.Output, cppPath, compilation.Interop, compilation.Native);
        File.WriteAllText(keyPath, key);
        return CopyNextTo(exePath, compilation.Native);
    }

    /// <summary>
    /// Copies each file that the manifest's <c>copy</c> names next to the executable. A reused
    /// executable gets them too, since the files next to it may have been removed or changed.
    /// </summary>
    static ExecutableBuild CopyNextTo(string exePath, NativeInputs native)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
        foreach (var file in native.Copy)
        {
            try
            {
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new ExecutableBuild(null, $"error: cannot copy '{file}' next to the program: {exception.Message}");
            }
        }
        return new ExecutableBuild(exePath, null);
    }

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
