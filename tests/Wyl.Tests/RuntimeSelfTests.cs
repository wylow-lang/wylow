using System.Diagnostics;
using Wyl.Compiler.Build;

// Tests of runtime behavior that no Wylow program can observe, written in C++ against the headers.
static class RuntimeSelfTests
{
    public const string PoolsName = "runtime/pools";
    public const string FunctionsName = "runtime/functions";
    public const string IsolatedName = "runtime/isolated";

    public static TestResult Pools(string root, string outputDirectory, Toolchain? toolchain) =>
        Run(root, outputDirectory, toolchain, "pools.cpp", PoolsName);

    public static TestResult Functions(string root, string outputDirectory, Toolchain? toolchain) =>
        Run(root, outputDirectory, toolchain, "functions.cpp", FunctionsName);

    public static TestResult Isolated(string root, string outputDirectory, Toolchain? toolchain) =>
        Run(root, outputDirectory, toolchain, "isolated.cpp", IsolatedName);

    static TestResult Run(string root, string outputDirectory, Toolchain? toolchain, string source, string name)
    {
        var compiler = Toolchains.Find(toolchain, out var error);
        if (compiler is null)
            return new(name, error);
        var directory = Path.Combine(outputDirectory, "runtime");
        Directory.CreateDirectory(directory);
        var program = Path.GetFileNameWithoutExtension(source);
        var exe = Path.Combine(directory, OperatingSystem.IsWindows() ? program + ".exe" : program);
        var build = compiler.Build(Path.Combine(root, "tests", "runtime", source), exe, release: false, precompiledHeader: false);
        if (!build.Success)
            return new(name, build.Output);
        var run = ProcessRunner.Run(new ProcessStartInfo(exe), TimeSpan.FromSeconds(120));
        if (run.TimedOut)
            return new(name, "timed out");
        if (Toolchains.SanitizerReport(run.StandardError) is { } report)
            return new(name, report);
        return new(name, run.ExitCode == 0 ? null : run.StandardError.Trim());
    }
}
