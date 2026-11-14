using System.Diagnostics;
using Wyl.Compiler;
using Wyl.Compiler.Build;
using Wyl.Harness;

static class CliSelfTests
{
    const string Program = """
        int main(String[] args) {
            std::print(args.length);
            return 3;
        }
        """;

    public static List<TestResult> Run(string root, string outputDirectory, Toolchain? toolchain, CompileOptions options)
    {
        var directory = Path.Combine(outputDirectory, "cli");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        var cli = new Cli(root, directory, toolchain, options, directory);
        return [Usage(cli), Check(cli), EmitAndIr(cli), Interpret(cli), RunProgram(cli), Build(cli), Fetch(cli), Package(cli)];
    }

    static TestResult Usage(Cli cli)
    {
        List<string> problems = [];
        void Expect(string what, ProcessResult result, int exit, string stderr)
        {
            if (result.ExitCode != exit)
                problems.Add($"{what} exited with {result.ExitCode} instead of {exit}");
            if (!result.StandardError.Contains(stderr, StringComparison.Ordinal))
                problems.Add($"{what} printed no '{stderr}'");
            if (result.StandardOutput.Length > 0)
                problems.Add($"{what} wrote to stdout");
        }
        Expect("no arguments", cli.Run(), 2, "usage: wyl");
        Expect("an unknown command", cli.Run("publish", cli.File("unused.wyl")), 2, "usage: wyl");
        Expect("an unknown option", cli.Run("check", cli.File("unused.wyl"), "--frobnicate"), 2, "unknown or incomplete option '--frobnicate'");
        Expect("a missing file", cli.Run("check", cli.File("absent.wyl")), 2, "file not found");
        var bare = cli.Directory("bare");
        Directory.CreateDirectory(bare);
        Expect("a directory without a manifest", cli.Run("check", bare), 2, "has no package.wylcfg");
        return new("cli/usage", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    static TestResult Check(Cli cli)
    {
        cli.Write("check/warning.wyl", """
            import std::print;

            void main() {
                print(1);
            }
            """);
        cli.Write("check/errors.wyl", """
            void main() {
                int first = "one";
                int second = "two";
            }
            """);
        cli.Write("check/lib/package.wylcfg", """
            name = "checked";
            kind = "library";
            version = "1.0.0";
            """);
        cli.Write("check/lib/src/checked.wyl", "pub int value() { return 1; }\n");

        List<string> problems = [];
        var warning = cli.Run("check", cli.File("check/warning.wyl"));
        if (warning.ExitCode != 0)
            problems.Add($"a program with a warning exited with {warning.ExitCode}");
        if (!warning.StandardError.Contains("warning[WYL0480]", StringComparison.Ordinal))
            problems.Add("a program with a warning printed no warning");
        if (warning.StandardOutput.Length > 0)
            problems.Add("check wrote to stdout");

        var errors = cli.Run("check", cli.File("check/errors.wyl"));
        if (errors.ExitCode != 1)
            problems.Add($"a program with errors exited with {errors.ExitCode} instead of 1");
        if (errors.StandardError.Split("error[WYL").Length - 1 != 2)
            problems.Add($"check reported {errors.StandardError.Split("error[WYL").Length - 1} errors instead of 2");
        if (!errors.StandardError.Contains("2 errors", StringComparison.Ordinal))
            problems.Add("check did not count the errors");

        var library = cli.Run("check", cli.Directory("check/lib"));
        if (library.ExitCode != 0)
            problems.Add($"a library exited with {library.ExitCode}: {library.StandardError}");

        var run = cli.Run("run", cli.Directory("check/lib"));
        if (run.ExitCode != 1 || !run.StandardError.Contains("a library has no entry point", StringComparison.Ordinal))
            problems.Add($"running a library exited with {run.ExitCode} and printed '{run.StandardError}'");
        return new("cli/check", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    static TestResult EmitAndIr(Cli cli)
    {
        cli.Write("emit/main.wyl", "void main() {\n    std::print(1);\n}\n");
        cli.Write("emit/broken.wyl", "void main() {\n    int value = \"text\";\n}\n");

        List<string> problems = [];
        var emitted = cli.Run("emit", cli.File("emit/main.wyl"));
        if (emitted.ExitCode != 0)
            problems.Add($"emit exited with {emitted.ExitCode}: {emitted.StandardError}");
        if (!emitted.StandardOutput.Contains("fn_main(", StringComparison.Ordinal))
            problems.Add("emit printed no generated function");

        var ir = cli.Run("ir", cli.File("emit/main.wyl"));
        if (ir.ExitCode != 0)
            problems.Add($"ir exited with {ir.ExitCode}: {ir.StandardError}");
        if (!ir.StandardOutput.Contains("fn main(", StringComparison.Ordinal))
            problems.Add($"ir printed '{ir.StandardOutput}'");

        var broken = cli.Run("emit", cli.File("emit/broken.wyl"));
        if (broken.ExitCode != 1)
            problems.Add($"emit of a broken program exited with {broken.ExitCode} instead of 1");
        if (broken.StandardOutput.Length > 0)
            problems.Add("emit of a broken program printed C++");
        if (!broken.StandardError.Contains("error[WYL0079]", StringComparison.Ordinal))
            problems.Add("emit of a broken program printed no error");
        return new("cli/emit-ir", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    static TestResult Interpret(Cli cli)
    {
        cli.Write("interpret/main.wyl", Program);
        cli.Write("interpret/overflow.wyl", "void main() {\n    int value = 2147483647;\n    std::print(value + 1);\n}\n");

        List<string> problems = [];
        var run = cli.Build("interpret", cli.File("interpret/main.wyl"), "--", "alpha", "beta");
        if (run.ExitCode != 3)
            problems.Add($"interpret returned the program's status {run.ExitCode} instead of 3");
        if (run.StandardOutput.Trim() != "2")
            problems.Add($"interpret printed '{run.StandardOutput}' for two arguments");

        var debug = cli.Build("interpret", cli.File("interpret/overflow.wyl"));
        if (!debug.StandardError.Contains("panic: integer overflow", StringComparison.Ordinal))
            problems.Add($"an overflowing interpretation printed '{debug.StandardError}'");

        var release = cli.Build("interpret", cli.File("interpret/overflow.wyl"), "--release");
        if (release.ExitCode != 0 || release.StandardOutput.Trim() != "-2147483648")
            problems.Add($"a release interpretation exited with {release.ExitCode} and printed '{release.StandardOutput}'");
        return new("cli/interpret", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    static TestResult RunProgram(Cli cli)
    {
        cli.Write("run/main.wyl", Program);

        List<string> problems = [];
        var run = cli.Build("run", cli.File("run/main.wyl"), "--", "one");
        if (run.ExitCode != 3)
            problems.Add($"run returned the program's status {run.ExitCode} instead of 3");
        if (run.StandardOutput.Trim() != "1")
            problems.Add($"run printed '{run.StandardOutput}' for one argument");

        var exe = cli.Exe(cli.File("run"), "main");
        if (!File.Exists(exe))
            problems.Add($"run left no executable beside the program at '{exe}'");

        var plain = cli.Build("run", cli.File("run/main.wyl"), "--no-opt", "--", "one", "two");
        if (plain.ExitCode != 3 || plain.StandardOutput.Trim() != "2")
            problems.Add($"run --no-opt exited with {plain.ExitCode} and printed '{plain.StandardOutput}'");
        return new("cli/run", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    static TestResult Build(Cli cli)
    {
        cli.Write("build/main.wyl", "void main() {\n    std::print(\"built\");\n}\n");

        List<string> problems = [];
        var destination = cli.File("build/program");
        var build = cli.Build("build", cli.File("build/main.wyl"), "-o", destination);
        if (build.ExitCode != 0)
            problems.Add($"build exited with {build.ExitCode}: {build.StandardError}");
        var expected = "built " + Path.GetFullPath(destination);
        if (build.StandardOutput.Trim() != expected)
            problems.Add($"build printed '{build.StandardOutput.Trim()}' instead of '{expected}'");
        var executable = new[] { destination, destination + ".exe" }.FirstOrDefault(File.Exists);
        if (executable is null)
        {
            problems.Add($"build wrote no executable at '{destination}'");
            return new("cli/build", string.Join('\n', problems));
        }
        var run = ProcessRunner.Run(new ProcessStartInfo(executable), TimeSpan.FromSeconds(30));
        if (run.StandardOutput.Trim() != "built")
            problems.Add($"the built program printed '{run.StandardOutput}'");
        return new("cli/build", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    static TestResult Fetch(Cli cli)
    {
        cli.Write("fetch/plain/package.wylcfg", "name = \"plain\";\n");
        cli.Write("fetch/plain/src/main.wyl", "void main() {}\n");
        cli.Write("fetch/bad/package.wylcfg", """
            name = "bad";
            native {
                fetch {
                    url = "https://files.test/a.zip";
                    sha256 = "abc";
                    files = ["a.h"];
                }
            }
            """);
        cli.Write("fetch/bad/src/main.wyl", "void main() {}\n");
        cli.Write("fetch/lone.wyl", "void main() {}\n");

        List<string> problems = [];
        var plain = cli.Run("fetch", cli.Directory("fetch/plain"));
        if (plain.ExitCode != 0)
            problems.Add($"fetch of a package without a download exited with {plain.ExitCode}: {plain.StandardError}");
        if (plain.StandardOutput.Length > 0 || plain.StandardError.Length > 0)
            problems.Add($"fetch of a package without a download printed '{plain.StandardOutput}{plain.StandardError}'");

        var lone = cli.Run("fetch", cli.File("fetch/lone.wyl"));
        if (lone.ExitCode != 0)
            problems.Add($"fetch of a lone file exited with {lone.ExitCode}: {lone.StandardError}");

        var bad = cli.Run("fetch", cli.Directory("fetch/bad"));
        if (bad.ExitCode != 1 || !bad.StandardError.Contains("error[WYL0553]", StringComparison.Ordinal))
            problems.Add($"fetch of a malformed hash exited with {bad.ExitCode} and printed '{bad.StandardError}'");
        return new("cli/fetch", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    static TestResult Package(Cli cli)
    {
        cli.Write("package/demo/package.wylcfg", """
            name = "demo";
            dependencies {
                codec { path = "vendor/codec"; }
            }
            native {
                copy = ["data/notes.txt"];
            }
            """);
        cli.Write("package/demo/src/main.wyl", Program);
        cli.Write("package/demo/data/notes.txt", "notes");
        cli.Write("package/demo/vendor/codec/package.wylcfg", """
            name = "codec";
            kind = "library";
            version = "1.0.0";
            native {
                copy = ["data/codec.txt"];
            }
            """);
        cli.Write("package/demo/vendor/codec/src/codec.wyl", "pub int width() { return 1; }\n");
        cli.Write("package/demo/vendor/codec/data/codec.txt", "codec");

        List<string> problems = [];
        var run = cli.Build("run", cli.Directory("package/demo"), "--", "a", "b", "c");
        if (run.ExitCode != 3 || run.StandardOutput.Trim() != "3")
            problems.Add($"a package run exited with {run.ExitCode} and printed '{run.StandardOutput}'");

        var exe = cli.Exe(cli.Directory("package/demo"), "demo");
        if (!File.Exists(exe))
            problems.Add($"the package run left no executable at '{exe}'");

        var output = Path.GetDirectoryName(exe)!;
        var notes = Path.Combine(output, "notes.txt");
        var codec = Path.Combine(output, "codec.txt");
        if (!File.Exists(notes))
            problems.Add($"the package's 'copy' file is not beside the executable at '{notes}'");
        if (!File.Exists(codec))
            problems.Add($"the dependency's 'copy' file is not beside the executable at '{codec}'");
        File.Delete(notes);
        File.Delete(codec);

        var destination = cli.File("package/program");
        var build = cli.Build("build", cli.Directory("package/demo"), "-o", destination);
        if (build.ExitCode != 0)
            problems.Add($"a package build exited with {build.ExitCode}: {build.StandardError}");
        if (!File.Exists(destination) && !File.Exists(destination + ".exe"))
            problems.Add($"a package build wrote no executable at '{destination}'");
        if (!File.Exists(notes) || !File.Exists(codec))
            problems.Add("a build whose executable was reused did not copy the deleted files again");
        return new("cli/package", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    sealed class Cli(string root, string directory, Toolchain? toolchain, CompileOptions options, string workingDirectory)
    {
        readonly string assembly = Path.Combine(root, "src", "Wyl.Cli", "bin", Configuration(), Tfm(), "wyl.dll");
        readonly string[] mode = ModeArguments(toolchain, options);

        public ProcessResult Run(params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("dotnet") { WorkingDirectory = workingDirectory };
            startInfo.ArgumentList.Add(assembly);
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            return ProcessRunner.Run(startInfo, TimeSpan.FromMinutes(2));
        }

        public ProcessResult Build(params string[] arguments)
        {
            var separator = Array.IndexOf(arguments, "--");
            return Run(separator < 0
                ? [.. arguments, .. mode]
                : [.. arguments[..separator], .. mode, .. arguments[separator..]]);
        }

        public string File(string name) => Path.Combine(directory, name);

        public string Directory(string name) => Path.Combine(directory, name);

        public string Exe(string near, string name) =>
            Path.Combine(near, ".wyl", name, OperatingSystem.IsWindows() ? name + ".exe" : name);

        public void Write(string name, string text)
        {
            var path = File(name);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, text);
        }

        static string[] ModeArguments(Toolchain? toolchain, CompileOptions options)
        {
            List<string> arguments = [];
            if (toolchain is { } selected)
                arguments.AddRange(["--cc", selected switch
                {
                    Toolchain.Clang => "clang",
                    Toolchain.Gcc => "gcc",
                    Toolchain.Msvc => "msvc",
                    Toolchain.ClangSanitized => "asan",
                    _ => "tsan",
                }]);
            if (!options.Nest)
                arguments.Add("--no-nest");
            if (!options.Optimize)
                arguments.Add("--no-opt");
            return [.. arguments];
        }

        static string Configuration() => new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;

        static string Tfm() => Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
    }
}
