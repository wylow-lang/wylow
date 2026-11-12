using Wyl.Compiler;
using Wyl.Compiler.Build;
using Wyl.Compiler.Text;

// The build caches return the executable a previous build left when nothing a build depends on has
// changed. A key that ignores one of those inputs is a stale executable, which no test can see: it
// would run the old program and pass.
static class BuildCacheSelfTests
{
    const string Program = """
        #include "wyl.h"
        int main() { return 0; }
        """;

    const string Source = """
        import std::print;

        void main() {
            print(1);
        }
        """;

    public static List<TestResult> Run(string outputDirectory)
    {
        var compiler = NativeCompiler.Find(null, out var error);
        if (compiler is null)
            return [new("build-cache/key", error)];
        return [CppKey(compiler), SourceKey(compiler), Replay(outputDirectory), Native(compiler, outputDirectory), NativeRebuild(outputDirectory)];
    }

    static TestResult CppKey(NativeCompiler compiler)
    {
        var key = compiler.BuildKey(release: false, Program);
        List<string> problems = [];
        if (compiler.BuildKey(release: false, Program) != key)
            problems.Add("the same inputs give different keys");
        if (compiler.BuildKey(release: false, Program + "\n") == key)
            problems.Add("the key ignores the generated C++");
        if (compiler.BuildKey(release: true, Program) == key)
            problems.Add("the key ignores the build flags");
        return new("build-cache/key", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    static TestResult SourceKey(NativeCompiler compiler)
    {
        string? Key(string path, string text, CompileOptions? options = null, bool release = false) =>
            Driver.SourceKey(compiler, PackageSources.Of(new SourceText(path, text)), options ?? new CompileOptions(), release);

        var key = Key("a/main.wyl", Source);
        List<string> problems = [];
        if (key is null)
            return new("build-cache/source-key", "the compiler's own files were not found");
        if (Key("a/main.wyl", Source) != key)
            problems.Add("the same inputs give different keys");
        if (Key("a/main.wyl", Source + "\n") == key)
            problems.Add("the key ignores the source");
        if (Key("b/main.wyl", Source) == key)
            problems.Add("the key ignores the file's path, which messages print");
        if (Key("a/main.wyl", Source, new CompileOptions(Nest: false)) == key)
            problems.Add("the key ignores --no-nest");
        if (Key("a/main.wyl", Source, new CompileOptions(Optimize: false)) == key)
            problems.Add("the key ignores --no-opt");
        if (Key("a/main.wyl", Source, release: true) == key)
            problems.Add("the key ignores --release");
        return new("build-cache/source-key", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    /// <summary>
    /// Both keys cover the manifest's <c>native</c> lists and every file that the C++ build reads from
    /// the package: each file of <c>native/</c> and of an <c>include</c> directory, each library that
    /// <c>link</c> names in a <c>libraryPaths</c> directory, and each file of <c>copy</c>. A key that
    /// ignores one of them reuses the executable built from the old file.
    /// </summary>
    static TestResult Native(NativeCompiler compiler, string outputDirectory)
    {
        var directory = Path.Combine(outputDirectory, "build-cache", "native");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Write(directory, "src/main.wyl", Source);
        Write(directory, "native/detail/shim.cpp", "int shim() { return 1; }\n");
        Write(directory, "include/base.h", "#define BASE 1\n");
        Write(directory, "lib/shapes.lib", "one");
        Write(directory, "lib/libshapes.a", "one");
        Write(directory, "data/notes.txt", "one");
        string Manifest(string defines) => $$"""
            name = "native";
            native {
                include = ["include"];
                libraryPaths = ["lib"];
                link = ["shapes"];
                copy = ["data/notes.txt"];
                defines = [{{defines}}];
            }
            """;
        Write(directory, "package.wylcfg", Manifest("\"BASE_ON\""));
        string? Key() => Driver.SourceKey(compiler, PackageSources.Load(directory), new CompileOptions(), release: false);
        string BuildKey() => compiler.BuildKey(release: false, Program, PackageSources.Load(directory).Native);

        List<string> problems = [];
        if (PackageSources.Load(directory).Diagnostics is { Count: > 0 } diagnostics)
            return new("build-cache/native", $"the package does not load: {string.Join('\n', diagnostics)}");
        var (key, buildKey) = (Key(), BuildKey());
        if (Key() != key || BuildKey() != buildKey)
            problems.Add("the same native files give different keys");
        void Changed(string what, Action change)
        {
            (key, buildKey) = (Key(), BuildKey());
            change();
            if (Key() == key)
                problems.Add($"the source key ignores {what}");
            if (BuildKey() == buildKey)
                problems.Add($"the build key ignores {what}");
        }
        Changed("a changed native source", () => Write(directory, "native/detail/shim.cpp", "int shim() { return 2; }\n"));
        Changed("a native header that was added", () => Write(directory, "native/shim.h", "#pragma once\n"));
        Changed("a changed header of an 'include' directory", () => Write(directory, "include/base.h", "#define BASE 2\n"));
        Changed("a changed library that 'link' names", () => Write(directory, $"lib/{(OperatingSystem.IsWindows() ? "shapes.lib" : "libshapes.a")}", "two"));
        Changed("a changed file of 'copy'", () => Write(directory, "data/notes.txt", "two"));
        Changed("a changed list of the manifest", () => Write(directory, "package.wylcfg", Manifest("\"BASE_ON=2\"")));
        return new("build-cache/native", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    /// <summary>
    /// A package built again after a header of its <c>include</c> directory, a definition of its
    /// manifest, or a file of <c>copy</c> changed runs the new program, with the new file next to it.
    /// </summary>
    static TestResult NativeRebuild(string outputDirectory)
    {
        var directory = Path.Combine(outputDirectory, "build-cache", "native-rebuild");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Write(directory, "src/main.wyl", """
            @Include("shim.h")
            extern {
                @Safe int shim_value();
            }

            void main() {
                std::print(shim_value());
            }
            """);
        Write(directory, "native/shim.h", "#pragma once\nint shim_value();\n");
        Write(directory, "native/shim.cpp", "#include \"shim.h\"\n#include \"offset.h\"\nint shim_value() { return SHIM_BASE + SHIM_OFFSET; }\n");
        Write(directory, "include/offset.h", "#define SHIM_OFFSET 2\n");
        Write(directory, "data/notes.txt", "one");
        string Manifest(int value) => $$"""
            name = "rebuild";
            native {
                include = ["include"];
                copy = ["data/notes.txt"];
                defines = ["SHIM_BASE={{value}}"];
            }
            """;
        Write(directory, "package.wylcfg", Manifest(40));
        var output = Path.Combine(directory, ".build");
        var notes = Path.Combine(output, "notes.txt");
        string Run()
        {
            var build = Driver.Build(PackageSources.Load(directory), new CompileOptions(), "rebuild", output, new BuildOptions());
            if (build.Executable is not { Success: true } executable)
                return $"the build failed: {build.Executable?.Error ?? string.Join('\n', build.Diagnostics)}";
            var run = ProcessRunner.Run(new System.Diagnostics.ProcessStartInfo(executable.ExePath!), TimeSpan.FromSeconds(10));
            return $"{run.StandardOutput.Trim()} {(File.Exists(notes) ? File.ReadAllText(notes) : "(no copy)")}";
        }

        List<string> problems = [];
        void Expect(string expected, string after)
        {
            if (Run() is var actual && actual != expected)
                problems.Add($"after {after}, the program printed and copied '{actual}' instead of '{expected}'");
        }
        Expect("42 one", "the first build");
        Write(directory, "include/offset.h", "#define SHIM_OFFSET 3\n");
        Expect("43 one", "a header of an 'include' directory changed");
        Write(directory, "package.wylcfg", Manifest(50));
        Write(directory, "data/notes.txt", "two");
        Expect("53 two", "a definition and a file of 'copy' changed");
        File.Delete(notes);
        Expect("53 two", "the copied file was deleted");
        return new("build-cache/native-rebuild", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    static void Write(string directory, string path, string text)
    {
        var file = Path.Combine(directory, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    /// <summary>A build that reuses an executable reports the warnings of the build that made it.</summary>
    static TestResult Replay(string outputDirectory)
    {
        var directory = Path.Combine(outputDirectory, "build-cache", "replay");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        var path = Path.Combine(directory, "main.wyl");
        var sources = PackageSources.Of(new SourceText(path, Source));
        var first = Driver.Build(sources, new CompileOptions(), "main", directory, new BuildOptions());
        if (first.Executable is not { Success: true })
            return new("build-cache/replay", $"the first build failed: {first.Executable?.Error ?? string.Join('\n', first.Diagnostics)}");
        var keyWritten = File.GetLastWriteTimeUtc(Path.Combine(directory, "main.source-key"));
        var second = Driver.Build(PackageSources.Of(new SourceText(path, Source)), new CompileOptions(), "main", directory, new BuildOptions());
        List<string> problems = [];
        if (first.Diagnostics.Count == 0)
            problems.Add("the program drew no warning to replay");
        if (!second.Diagnostics.SequenceEqual(first.Diagnostics))
            problems.Add($"the second build reported\n{string.Join('\n', second.Diagnostics)}\ninstead of\n{string.Join('\n', first.Diagnostics)}");
        if (second.Executable?.ExePath != first.Executable.ExePath)
            problems.Add("the second build did not reuse the executable");
        if (File.GetLastWriteTimeUtc(Path.Combine(directory, "main.source-key")) != keyWritten)
            problems.Add("the second build compiled the program again");
        return new("build-cache/replay", problems.Count == 0 ? null : string.Join('\n', problems));
    }
}
