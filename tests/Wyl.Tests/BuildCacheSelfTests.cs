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
        return [CppKey(compiler), SourceKey(compiler), Replay(outputDirectory), Native(compiler, outputDirectory)];
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
    /// Both keys cover every file of a package's <c>native/</c> directory, so that a changed native
    /// source or header does not reuse the executable built from the old one.
    /// </summary>
    static TestResult Native(NativeCompiler compiler, string outputDirectory)
    {
        var directory = Path.Combine(outputDirectory, "build-cache", "native");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        var native = Path.Combine(directory, "native");
        Directory.CreateDirectory(Path.Combine(native, "detail"));
        var sources = PackageSources.Of(new SourceText(Path.Combine(directory, "main.wyl"), Source)) with { NativeDirectory = native };
        string? Key() => Driver.SourceKey(compiler, sources, new CompileOptions(), release: false);
        string BuildKey() => compiler.BuildKey(release: false, Program, native);

        File.WriteAllText(Path.Combine(native, "detail", "shim.cpp"), "int shim() { return 1; }\n");
        var (key, buildKey) = (Key(), BuildKey());
        List<string> problems = [];
        if (Key() != key || BuildKey() != buildKey)
            problems.Add("the same native files give different keys");
        File.WriteAllText(Path.Combine(native, "detail", "shim.cpp"), "int shim() { return 2; }\n");
        if (Key() == key)
            problems.Add("the source key ignores a changed native source");
        if (BuildKey() == buildKey)
            problems.Add("the build key ignores a changed native source");
        (key, buildKey) = (Key(), BuildKey());
        File.WriteAllText(Path.Combine(native, "shim.h"), "#pragma once\n");
        if (Key() == key || BuildKey() == buildKey)
            problems.Add("a key ignores a native header that was added");
        return new("build-cache/native", problems.Count == 0 ? null : string.Join('\n', problems));
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
