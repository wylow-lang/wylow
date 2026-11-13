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
        return [CppKey(compiler), SourceKey(compiler), Dependencies(compiler, outputDirectory), Replay(outputDirectory), Native(compiler, outputDirectory), NativeRebuild(outputDirectory), NativeObjects(outputDirectory)];
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
    /// The source key covers every package of the program: each library's sources, and each
    /// package's manifest, whose kind and versions decide whether the program builds at all.
    /// </summary>
    static TestResult Dependencies(NativeCompiler compiler, string outputDirectory)
    {
        var directory = Path.Combine(outputDirectory, "build-cache", "dependencies");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Write(directory, "package.wylcfg", """
            name = "dependencies";
            dependencies {
                parser { path = "vendor/parser"; }
            }
            """);
        Write(directory, "src/main.wyl", "void main() { std::print(parser::depth()); }\n");
        Write(directory, "vendor/parser/package.wylcfg", "name = \"parser\"; kind = \"library\"; version = \"1.0.0\";\n");
        Write(directory, "vendor/parser/src/parser.wyl", "pub int depth() { return 1; }\n");
        string? Key() => Driver.SourceKey(compiler, PackageSources.Load(directory), new CompileOptions(), release: false);

        if (PackageSources.Load(directory).Diagnostics is { Count: > 0 } diagnostics)
            return new("build-cache/dependencies", $"the package does not load: {string.Join('\n', diagnostics)}");
        List<string> problems = [];
        void Changed(string what, Action change)
        {
            var key = Key();
            change();
            if (Key() == key)
                problems.Add($"the source key ignores {what}");
        }
        Changed("a changed source of a library", () => Write(directory, "vendor/parser/src/parser.wyl", "pub int depth() { return 2; }\n"));
        Changed("a changed manifest of a library", () => Write(directory, "vendor/parser/package.wylcfg", "name = \"parser\"; kind = \"library\"; version = \"1.1.0\";\n"));
        Changed("a changed manifest of the program", () => Write(directory, "package.wylcfg", """
            name = "dependencies";
            kind = "library";
            version = "1.0.0";
            dependencies {
                parser { path = "vendor/parser"; }
            }
            """));
        return new("build-cache/dependencies", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    /// <summary>
    /// Both keys cover the manifest's <c>native</c> lists and every file that the C++ build reads from
    /// the package: each file of <c>native/</c> and of an <c>include</c> directory, each library that
    /// <c>link</c> names in a <c>libraryPaths</c> directory, and each file of <c>copy</c>. They cover
    /// the same lists and files of a dependency. A key that ignores one of them reuses the executable
    /// built from the old file.
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
        Write(directory, "vendor/codec/src/codec.wyl", "pub int width() { return 1; }\n");
        Write(directory, "vendor/codec/native/codec.cpp", "int codec() { return 1; }\n");
        Write(directory, "vendor/codec/include/codec_base.h", "#define CODEC_BASE 1\n");
        Write(directory, "vendor/codec/data/codec.txt", "one");
        string Library(string defines) => $$"""
            name = "codec";
            kind = "library";
            version = "1.0.0";
            native {
                include = ["include"];
                copy = ["data/codec.txt"];
                defines = [{{defines}}];
            }
            """;
        Write(directory, "vendor/codec/package.wylcfg", Library("\"CODEC_ON\""));
        string Manifest(string defines) => $$"""
            name = "native";
            dependencies {
                codec { path = "vendor/codec"; }
            }
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
        Changed("a changed native source of a dependency", () => Write(directory, "vendor/codec/native/codec.cpp", "int codec() { return 2; }\n"));
        Changed("a changed header of a dependency's 'include' directory", () => Write(directory, "vendor/codec/include/codec_base.h", "#define CODEC_BASE 2\n"));
        Changed("a changed file of a dependency's 'copy'", () => Write(directory, "vendor/codec/data/codec.txt", "two"));
        Changed("a changed list of a dependency's manifest", () => Write(directory, "vendor/codec/package.wylcfg", Library("\"CODEC_ON=2\"")));
        return new("build-cache/native", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    /// <summary>
    /// A package built again after a header of its <c>include</c> directory, a definition of its
    /// manifest, or a file of <c>copy</c> changed runs the new program, with the new file next to it.
    /// A file that a dependency's <c>copy</c> names goes next to the program too.
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
        Write(directory, "vendor/codec/package.wylcfg", """
            name = "codec";
            kind = "library";
            version = "1.0.0";
            native {
                copy = ["codec.txt"];
            }
            """);
        Write(directory, "vendor/codec/src/codec.wyl", "pub int width() { return 1; }\n");
        Write(directory, "vendor/codec/codec.txt", "codec");
        string Manifest(int value) => $$"""
            name = "rebuild";
            dependencies {
                codec { path = "vendor/codec"; }
            }
            native {
                include = ["include"];
                copy = ["data/notes.txt"];
                defines = ["SHIM_BASE={{value}}"];
            }
            """;
        Write(directory, "package.wylcfg", Manifest(40));
        var output = Path.Combine(directory, ".build");
        var notes = Path.Combine(output, "notes.txt");
        var codec = Path.Combine(output, "codec.txt");
        string Run()
        {
            var build = Driver.Build(PackageSources.Load(directory), new CompileOptions(), "rebuild", output, new BuildOptions());
            if (build.Executable is not { Success: true } executable)
                return $"the build failed: {build.Executable?.Error ?? string.Join('\n', build.Diagnostics)}";
            var run = ProcessRunner.Run(new System.Diagnostics.ProcessStartInfo(executable.ExePath!), TimeSpan.FromSeconds(10));
            return $"{run.StandardOutput.Trim()} {Copied(notes)} {Copied(codec)}";
        }

        static string Copied(string path) => File.Exists(path) ? File.ReadAllText(path) : "(no copy)";

        List<string> problems = [];
        void Expect(string expected, string after)
        {
            if (Run() is var actual && actual != expected)
                problems.Add($"after {after}, the program printed and copied '{actual}' instead of '{expected}'");
        }
        Expect("42 one codec", "the first build");
        Write(directory, "include/offset.h", "#define SHIM_OFFSET 3\n");
        Expect("43 one codec", "a header of an 'include' directory changed");
        Write(directory, "package.wylcfg", Manifest(50));
        Write(directory, "data/notes.txt", "two");
        Expect("53 two codec", "a definition and a file of 'copy' changed");
        File.Delete(notes);
        File.Delete(codec);
        Expect("53 two codec", "the copied files were deleted");
        return new("build-cache/native-rebuild", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    /// <summary>
    /// A native object is compiled again only if a file that its source can include changed, so a
    /// change to the Wylow sources alone keeps it. A changed C source or header gives the new program.
    /// A source of a dependency can include only its own package's files, so a change to them
    /// compiles that source again and keeps the program's.
    /// </summary>
    static TestResult NativeObjects(string outputDirectory)
    {
        var directory = Path.Combine(outputDirectory, "build-cache", "native-objects");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        string Main(int added) => $$"""
            @Include("shim.h")
            extern {
                @Safe int shim_value();
            }

            void main() {
                std::print(shim_value() + scale::value() + {{added}});
            }
            """;
        Write(directory, "src/main.wyl", Main(0));
        string Shim(int value) => $$"""
            #include "shim.h"
            #include "offset.h"
            int shim_value(void) { return {{value}} + SHIM_OFFSET; }

            """;
        const string Header = """
            #ifdef __cplusplus
            extern "C" {
            #endif
            int {0}_value(void);
            #ifdef __cplusplus
            }
            #endif

            """;
        Write(directory, "native/shim.h", Header.Replace("{0}", "shim", StringComparison.Ordinal));
        Write(directory, "native/shim.c", Shim(40));
        Write(directory, "include/offset.h", "#define SHIM_OFFSET 2\n");
        Write(directory, "package.wylcfg", """
            name = "objects";
            dependencies {
                scale { path = "vendor/scale"; }
            }
            native {
                include = ["include"];
            }
            """);
        Write(directory, "vendor/scale/package.wylcfg", """
            name = "scale";
            kind = "library";
            version = "1.0.0";
            """);
        Write(directory, "vendor/scale/src/scale.wyl", """
            @Include("scale.h")
            extern {
                @Safe int scale_value();
            }

            pub int value() {
                return scale_value();
            }
            """);
        Write(directory, "vendor/scale/native/scale.h", Header.Replace("{0}", "scale", StringComparison.Ordinal));
        Write(directory, "vendor/scale/native/scale.c", "#include \"scale.h\"\n#include \"factor.h\"\nint scale_value(void) { return FACTOR; }\n");
        Write(directory, "vendor/scale/native/factor.h", "#define FACTOR 100\n");
        var output = Path.Combine(directory, ".build");
        string? Run()
        {
            var build = Driver.Build(PackageSources.Load(directory), new CompileOptions(), "objects", output, new BuildOptions());
            if (build.Executable is not { Success: true } executable)
                return null;
            return ProcessRunner.Run(new System.Diagnostics.ProcessStartInfo(executable.ExePath!), TimeSpan.FromSeconds(10)).StandardOutput.Trim();
        }
        // The program's source comes before its dependency's, so its object is the first.
        DateTime Compiled(string obj) =>
            Directory.EnumerateFiles(Path.Combine(output, "native-objects"), obj + ".*").Where(f => !f.EndsWith(".key", StringComparison.Ordinal))
                .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
        string[] objects = ["0_shim", "1_scale"];

        List<string> problems = [];
        void Expect(string expected, string after, params string[] compiled)
        {
            var before = objects.Select(o => Directory.Exists(Path.Combine(output, "native-objects")) ? Compiled(o) : default).ToList();
            var actual = Run();
            if (actual != expected)
                problems.Add($"after {after}, the program printed '{actual ?? "(the build failed)"}' instead of '{expected}'");
            for (int i = 0; i < objects.Length; i++)
            {
                bool compiles = compiled.Contains(objects[i]);
                if ((Compiled(objects[i]) != before[i]) != compiles)
                    problems.Add($"after {after}, the build {(compiles ? "kept" : "compiled")} the object '{objects[i]}'");
            }
        }
        Expect("142", "the first build", "0_shim", "1_scale");
        Write(directory, "src/main.wyl", Main(1));
        Expect("143", "a Wylow source changed");
        Write(directory, "native/shim.c", Shim(50));
        Expect("153", "the C source changed", "0_shim");
        Write(directory, "include/offset.h", "#define SHIM_OFFSET 3\n");
        Expect("154", "a header of an 'include' directory changed", "0_shim");
        Write(directory, "vendor/scale/native/factor.h", "#define FACTOR 200\n");
        Expect("254", "a header of the dependency changed", "1_scale");
        return new("build-cache/native-objects", problems.Count == 0 ? null : string.Join('\n', problems));
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
