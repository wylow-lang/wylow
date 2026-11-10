using System.Diagnostics;
using Wyl.Compiler;
using Wyl.Compiler.Build;
using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Interpret;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

const string Usage = """
    usage: wyl <command> <file.wyl | package directory> [options] [-- <program arguments>]

    commands:
      run      build and run the program
      build    build an executable
      check    report errors without building
      emit     print the generated C++
      ir       print the mid-level IR of the program's functions
      interpret
               run the program on the IR interpreter, with no C++ toolchain

    options:
      --release        optimized build; integer overflow wraps instead of panicking
      --cc clang|msvc|gcc|asan|tsan
                       C++ compiler to use (default: clang-cl if installed, otherwise msvc,
                       otherwise gcc); asan is clang-cl with AddressSanitizer and UBSan, and
                       tsan builds with ThreadSanitizer where the platform supports it
      -o <path>        where to write the executable (build only)
      --no-nest        declare every temporary instead of nesting it into its use
      --no-opt         run no optimizations: keep every hold and copy lowering made, every object on the heap
      --               what follows is the program's arguments, for a main(String[] args) (run and interpret)
    """;

string[] commands = ["run", "build", "check", "emit", "ir", "interpret"];
if (args.Length < 2 || !commands.Contains(args[0]))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var command = args[0];
var file = args[1];
bool release = false;
Toolchain? toolchain = null;
string? output = null;
var options = new CompileOptions();
string[] programArguments = [];

for (int i = 2; i < args.Length; i++)
{
    if (args[i] == "--")
    {
        programArguments = args[(i + 1)..];
        break;
    }
    switch (args[i])
    {
        case "--release":
            release = true;
            break;
        case "--cc" when i + 1 < args.Length && args[i + 1] is "clang" or "msvc" or "gcc" or "asan" or "tsan":
            toolchain = args[++i] switch
            {
                "clang" => Toolchain.Clang,
                "msvc" => Toolchain.Msvc,
                "gcc" => Toolchain.Gcc,
                "tsan" => Toolchain.ThreadSanitized,
                _ => Toolchain.ClangSanitized,
            };
            break;
        case "-o" when i + 1 < args.Length:
            output = args[++i];
            break;
        case "--no-nest":
            options = options with { Nest = false };
            break;
        case "--no-opt":
            options = options with { Optimize = false };
            break;
        default:
            Console.Error.WriteLine($"wyl: unknown or incomplete option '{args[i]}'\n\n{Usage}");
            return 2;
    }
}

bool isPackage = PackageSources.IsPackage(file);
if (!isPackage && !File.Exists(file))
{
    Console.Error.WriteLine(Directory.Exists(file) ? $"wyl: {file} has no {Manifest.FileName}" : $"wyl: file not found: {file}");
    return 2;
}

var source = isPackage ? PackageSources.Load(file) : PackageSources.Of(SourceText.FromFile(file));
if (command == "interpret")
{
    var interpreted = Compilation.Interpret(source, options, new InterpreterOptions(Release: release, Arguments: programArguments));
    ReportDiagnostics(interpreted.Diagnostics);
    if (!interpreted.Success)
        return 1;
    var result = interpreted.Interpretation!;
    using var stdout = Console.OpenStandardOutput();
    stdout.Write(result.Output);
    Console.Error.Write(result.Error);
    return result.Status is InterpretationStatus.Completed or InterpretationStatus.Panicked ? result.ExitCode : 1;
}

if (command is "check" or "emit" or "ir")
{
    var compilation = Compilation.Compile(source, options);
    ReportDiagnostics(compilation.Diagnostics);
    if (!compilation.Success)
        return 1;
    if (command == "emit")
        Console.Write(compilation.Cpp);
    else if (command == "ir")
        Console.Write(compilation.IrText());
    return 0;
}

static void ReportDiagnostics(IReadOnlyList<Diagnostic> diagnostics) =>
    ReportErrors(diagnostics.Select(d => d.ToString()), diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error));

static void ReportErrors(IEnumerable<string> diagnostics, int errors)
{
    foreach (var diagnostic in diagnostics)
        Console.Error.WriteLine(diagnostic);
    if (errors > 0)
        Console.Error.WriteLine($"{errors} error{(errors == 1 ? "" : "s")}");
}

var name = source.Package.Name ?? Path.GetFileNameWithoutExtension(file);
// Beside a lone file, and in a package's own directory, outside its src/.
var buildDirectory = isPackage
    ? Path.Combine(Path.GetFullPath(file), ".wyl", name)
    : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, ".wyl", name);
var built = Driver.Build(source, options, name, buildDirectory, new BuildOptions(release, toolchain));
ReportErrors(built.Diagnostics, built.Errors);
if (built.Executable is not { } build)
    return 1;
if (!build.Success)
{
    Console.Error.WriteLine(build.Error);
    return 1;
}

if (command == "build")
{
    var destination = Path.GetFullPath(output ?? Path.GetFileName(build.ExePath!));
    File.Copy(build.ExePath!, destination, overwrite: true);
    Console.WriteLine($"built {destination}");
    return 0;
}

var start = new ProcessStartInfo(build.ExePath!) { UseShellExecute = false };
foreach (var argument in programArguments)
    start.ArgumentList.Add(argument);
using var process = Process.Start(start)!;
process.WaitForExit();
return process.ExitCode;
