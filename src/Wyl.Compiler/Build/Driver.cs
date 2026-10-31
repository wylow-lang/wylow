namespace Wyl.Compiler.Build;

public sealed record BuildOptions(bool Release = false, Toolchain? Toolchain = null);

public sealed record ExecutableBuild(string? ExePath, string? Error)
{
    public bool Success => ExePath is not null;
}

public static class Driver
{
    public static ExecutableBuild BuildExecutable(CompilationResult compilation, string name, string outputDirectory, BuildOptions options)
    {
        if (compilation.Cpp is null)
            throw new ArgumentException("cannot build a compilation that has errors", nameof(compilation));

        var compiler = NativeCompiler.Find(options.Toolchain, out var error);
        if (compiler is null)
            return new ExecutableBuild(null, error);

        Directory.CreateDirectory(outputDirectory);
        var cppPath = Path.Combine(outputDirectory, name + ".cpp");
        File.WriteAllText(cppPath, compilation.Cpp);
        var exePath = Path.Combine(outputDirectory, name + (OperatingSystem.IsWindows() ? ".exe" : ""));
        var keyPath = Path.Combine(outputDirectory, name + ".key");
        var key = compiler.BuildKey(options.Release, compilation.Cpp);
        if (File.Exists(exePath) && File.Exists(keyPath) && File.ReadAllText(keyPath) == key)
            return new ExecutableBuild(exePath, null);

        var result = compiler.Build(cppPath, exePath, options.Release);
        if (result.Success)
            File.WriteAllText(keyPath, key);
        // The program already passed every Wylow check, so a C++ failure is a compiler bug.
        return result.Success
            ? new ExecutableBuild(exePath, null)
            : new ExecutableBuild(null, $"internal compiler error: the generated C++ failed to compile ({cppPath})\n{result.Output}");
    }
}
