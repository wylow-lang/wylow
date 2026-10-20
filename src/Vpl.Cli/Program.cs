using System.Diagnostics;
using Vpl.Compiler;
using Vpl.Compiler.Build;
using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Text;

const string Usage = """
    usage: vpl <command> <file.vpl> [options]

    commands:
      run      build and run the program
      build    build an executable
      check    report errors without building
      emit     print the generated C++

    options:
      --release        optimized build; integer overflow wraps instead of panicking
      --cc msvc|gcc    C++ compiler to use (default: msvc if installed, otherwise gcc)
      -o <path>        where to write the executable (build only)
    """;

string[] commands = ["run", "build", "check", "emit"];
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

for (int i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--release":
            release = true;
            break;
        case "--cc" when i + 1 < args.Length && args[i + 1] is "msvc" or "gcc":
            toolchain = args[++i] == "msvc" ? Toolchain.Msvc : Toolchain.Gcc;
            break;
        case "-o" when i + 1 < args.Length:
            output = args[++i];
            break;
        default:
            Console.Error.WriteLine($"vpl: unknown or incomplete option '{args[i]}'\n\n{Usage}");
            return 2;
    }
}

if (!File.Exists(file))
{
    Console.Error.WriteLine($"vpl: file not found: {file}");
    return 2;
}

var compilation = Compilation.Compile(SourceText.FromFile(file));
foreach (var diagnostic in compilation.Diagnostics)
    Console.Error.WriteLine(diagnostic);
if (!compilation.Success)
{
    int errors = compilation.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
    Console.Error.WriteLine($"{errors} error{(errors == 1 ? "" : "s")}");
    return 1;
}

switch (command)
{
    case "check":
        return 0;
    case "emit":
        Console.Write(compilation.Cpp);
        return 0;
}

var name = Path.GetFileNameWithoutExtension(file);
var buildDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, ".vpl", name);
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
