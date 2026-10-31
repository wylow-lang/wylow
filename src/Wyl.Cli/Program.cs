using System.Diagnostics;
using Wyl.Compiler;
using Wyl.Compiler.Build;
using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Interpret;
using Wyl.Compiler.Text;

const string Usage = """
    usage: wyl <command> <file.wyl> [options]

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
      --cc msvc|gcc|asan
                       C++ compiler to use (default: msvc if installed, otherwise gcc);
                       asan is clang-cl with AddressSanitizer and UBSan
      -o <path>        where to write the executable (build only)
      --no-nest        declare every temporary instead of nesting it into its use
      --no-opt         run no optimizations: keep every hold and copy lowering made, every object on the heap
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

for (int i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--release":
            release = true;
            break;
        case "--cc" when i + 1 < args.Length && args[i + 1] is "msvc" or "gcc" or "asan":
            toolchain = args[++i] switch { "msvc" => Toolchain.Msvc, "gcc" => Toolchain.Gcc, _ => Toolchain.ClangSanitized };
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

if (!File.Exists(file))
{
    Console.Error.WriteLine($"wyl: file not found: {file}");
    return 2;
}

var source = SourceText.FromFile(file);
if (command == "interpret")
{
    var interpreted = Compilation.Interpret(source, options, new InterpreterOptions(Release: release));
    ReportErrors(interpreted.Diagnostics);
    if (!interpreted.Success)
        return 1;
    var result = interpreted.Interpretation!;
    using var stdout = Console.OpenStandardOutput();
    stdout.Write(result.Output);
    Console.Error.Write(result.Error);
    return result.Status is InterpretationStatus.Completed or InterpretationStatus.Panicked ? result.ExitCode : 1;
}

var compilation = Compilation.Compile(source, options);
ReportErrors(compilation.Diagnostics);
if (!compilation.Success)
    return 1;

switch (command)
{
    case "check":
        return 0;
    case "emit":
        Console.Write(compilation.Cpp);
        return 0;
    case "ir":
        Console.Write(compilation.IrText());
        return 0;
}

static void ReportErrors(IReadOnlyList<Diagnostic> diagnostics)
{
    foreach (var diagnostic in diagnostics)
        Console.Error.WriteLine(diagnostic);
    int errors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
    if (errors > 0)
        Console.Error.WriteLine($"{errors} error{(errors == 1 ? "" : "s")}");
}

var name = Path.GetFileNameWithoutExtension(file);
var buildDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, ".wyl", name);
var build = Driver.BuildExecutable(compilation, name, buildDirectory, new BuildOptions(release, toolchain));
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

using var process = Process.Start(new ProcessStartInfo(build.ExePath!) { UseShellExecute = false })!;
process.WaitForExit();
return process.ExitCode;
