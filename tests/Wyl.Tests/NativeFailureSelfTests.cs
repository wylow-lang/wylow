using System.Diagnostics;
using Wyl.Compiler;
using Wyl.Compiler.Build;

static class NativeFailureSelfTests
{
    public static List<TestResult> Run(string outputDirectory) =>
        [CompileFailure(outputDirectory), WarningOnly(outputDirectory), StaleObjects(outputDirectory)];

    static TestResult CompileFailure(string outputDirectory)
    {
        var directory = Prepare(outputDirectory, "compile-failure");
        Write(directory, "native/broken.cpp", "int broken( {\n");
        var build = Build(directory);
        var error = build.Executable?.Error;
        List<string> problems = [];
        if (build.Executable is not { Success: false })
            problems.Add("a package whose native source does not compile built anyway");
        if (error is null || !error.Contains("broken.cpp", StringComparison.Ordinal))
            problems.Add($"the failure does not name the source: {error}");
        if (error is null || !error.Contains("expected expression", StringComparison.Ordinal))
            problems.Add($"the failure does not hold the compiler's message: {error}");
        return new("native-build/failure", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    static TestResult WarningOnly(string outputDirectory)
    {
        var directory = Prepare(outputDirectory, "warning-only");
        Write(directory, "native/warn.cpp", "int warn_value() { int unused = 3; return 0; }\n");
        var build = Build(directory);
        List<string> problems = [];
        if (build.Executable is not { Success: true } executable)
            problems.Add($"a source that draws a warning failed the build: {build.Executable?.Error}");
        else
        {
            var run = ProcessRunner.Run(new ProcessStartInfo(executable.ExePath!), TimeSpan.FromSeconds(30));
            if (run.ExitCode != 0)
                problems.Add($"the program exited with {run.ExitCode}: {run.StandardError}");
        }
        if (build.Diagnostics.Count > 0)
            problems.Add($"a source that draws a warning reported {string.Join("; ", build.Diagnostics)}");
        return new("native-build/warning", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    static TestResult StaleObjects(string outputDirectory)
    {
        var directory = Prepare(outputDirectory, "stale-objects");
        Write(directory, "native/first.cpp", "int first_value() { return 1; }\n");
        Write(directory, "native/second.cpp", "int second_value() { return 2; }\n");
        var build = Build(directory);
        if (build.Executable is not { Success: true })
            return new("native-build/stale", $"the first build failed: {build.Executable?.Error}");
        var objects = Path.Combine(directory, ".build", "native-objects");
        List<string> problems = [];
        string[] Names() =>
            [.. Directory.EnumerateFiles(objects)
                .Select(Path.GetFileName)
                .Where(name => name is not null && !name.EndsWith(".key", StringComparison.OrdinalIgnoreCase))
                .Select(name => name!)
                .Order(StringComparer.Ordinal)];
        if (Names().Length != 2)
            problems.Add($"the first build left {Names().Length} objects instead of 2");
        File.Delete(Path.Combine(directory, "native", "second.cpp"));
        if (Build(directory).Executable is not { Success: true })
            return new("native-build/stale", "the second build failed");
        if (Names() is var remaining && remaining.Length != 1)
            problems.Add($"the build after a source was removed left {string.Join(", ", remaining)}");
        var keys = Directory.EnumerateFiles(objects, "*.key").Count();
        if (keys != 1)
            problems.Add($"the build after a source was removed left {keys} key files");
        return new("native-build/stale", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    static ProgramBuild Build(string directory) =>
        Driver.Build(PackageSources.Load(directory), new CompileOptions(), "nativebuild", Path.Combine(directory, ".build"), new BuildOptions());

    static string Prepare(string outputDirectory, string name)
    {
        var directory = Path.Combine(outputDirectory, "native-build", name);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Write(directory, "package.wylcfg", $"name = \"{name.Replace('-', '_')}\";\n");
        Write(directory, "src/main.wyl", "void main() { }\n");
        return directory;
    }

    static void Write(string directory, string path, string text)
    {
        var file = Path.Combine(directory, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }
}
