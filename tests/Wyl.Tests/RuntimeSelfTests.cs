using System.Diagnostics;
using Wyl.Compiler.Build;

// Tests of runtime behavior that no Wylow program can observe, written in C++ against the headers.
static class RuntimeSelfTests
{
    public const string PoolsName = "runtime/pools";

    public static TestResult Pools(string root, string outputDirectory, Toolchain? toolchain)
    {
        var compiler = NativeCompiler.Find(toolchain, out var error);
        if (compiler is null)
            return new(PoolsName, error);
        var directory = Path.Combine(outputDirectory, "runtime");
        Directory.CreateDirectory(directory);
        var exe = Path.Combine(directory, OperatingSystem.IsWindows() ? "pools.exe" : "pools");
        var build = compiler.Build(Path.Combine(root, "tests", "runtime", "pools.cpp"), exe, release: false, precompiledHeader: false);
        if (!build.Success)
            return new(PoolsName, build.Output);
        var run = ProcessRunner.Run(new ProcessStartInfo(exe), TimeSpan.FromSeconds(120));
        if (run.TimedOut)
            return new(PoolsName, "timed out");
        if (NativeCompiler.SanitizerReport(run.StandardError) is { } report)
            return new(PoolsName, report);
        return new(PoolsName, run.ExitCode == 0 ? null : run.StandardError.Trim());
    }
}
