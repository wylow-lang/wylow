using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Vpl.Compiler;
using Vpl.Compiler.Build;
using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Ir;
using Vpl.Compiler.Text;
using Vpl.Fuzz;

// Runs every tests/cases/**/*.vpl file. Expectations are comments in the file:
//   // output: <line>     expected stdout line (in order of appearance)
//   // error: <text>      an error on this line whose message contains <text>
//   // panic: <text>      the program panics with a message starting with <text>
//   // panic-here: <text> the same, reported at this line
//   // warning: <text>    the program reports a performance warning containing <text>; any other fails
//   // mode: release      build with --release
//   // mode: optimized    skip under --no-opt: the test observes an optimization, through diagnostics
//   // stack: <n>         <n> objects made on this line are on the stack; a file using this checks every line
//   // in-place: <n>      <n> loop elements declared on this line refer to their elements in place; likewise
// Usage: Vpl.Tests [--cc msvc|gcc|asan|interpret] [--interpret] [--no-nest] [--no-opt] [--all-modes]
//                  [--out <dir>] [--jobs <n>] [--update-snapshots] [filter]
// With --cc asan, programs are built with AddressSanitizer and UBSan, and any report fails the test.
// --interpret runs the IR interpreter instead of a native build; it is opt-in until step 5.
// --all-modes runs the five modes one per process at once, each writing under <out>/<mode>.
// --update-snapshots rewrites tests/matrix/*.snapshot with what the matrix cells do now.

Toolchain? toolchain = null;
string? filter = null;
bool updateSnapshots = false;
bool allModes = false;
bool interpret = false;
string? outputArgument = null;
int jobs = 0;
var options = new CompileOptions();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--cc" && i + 1 < args.Length)
    {
        toolchain = args[++i] switch
        {
            "gcc" => Toolchain.Gcc,
            "asan" => Toolchain.ClangSanitized,
            "msvc" => Toolchain.Msvc,
            "interpret" => null,
            var other => throw new ArgumentException($"unknown compiler '{other}'; expected msvc, gcc, asan, or interpret"),
        };
        if (args[i] == "interpret")
            interpret = true;
    }
    else if (args[i] == "--interpret")
        interpret = true;
    else if (args[i] == "--no-nest")
        options = options with { Nest = false };
    else if (args[i] == "--no-opt")
        options = options with { Optimize = false };
    else if (args[i] == "--update-snapshots")
        updateSnapshots = true;
    else if (args[i] == "--all-modes")
        allModes = true;
    else if (args[i] == "--out" && i + 1 < args.Length)
        outputArgument = args[++i];
    else if (args[i] == "--jobs" && i + 1 < args.Length)
        jobs = int.Parse(args[++i]);
    else
        filter = args[i];
}

var root = FindRepositoryRoot();
var casesDirectory = Path.Combine(root, "tests", "cases");
var outputDirectory = outputArgument is null ? Path.Combine(root, "tests", ".out") : Path.GetFullPath(outputArgument);
var parallelism = jobs > 0 ? jobs : Math.Max(1, Environment.ProcessorCount / 2);

if (allModes)
{
    if (toolchain is not null || interpret || !options.Nest || !options.Optimize || updateSnapshots)
    {
        Console.Error.WriteLine("--all-modes runs each mode itself, so it takes no --cc, --interpret, --no-nest, --no-opt, or --update-snapshots");
        return 1;
    }
    return RunAllModes(outputDirectory, filter, parallelism);
}

if (!interpret && NativeCompiler.Find(toolchain, out var toolchainError) is null)
{
    Console.Error.WriteLine(toolchainError);
    return 1;
}

var files = Directory.GetFiles(casesDirectory, "*.vpl", SearchOption.AllDirectories)
    .Where(f => filter is null || f.Contains(filter, StringComparison.OrdinalIgnoreCase))
    .Order(StringComparer.Ordinal)
    .ToList();

var stopwatch = Stopwatch.StartNew();
var results = files
    .AsParallel()
    .AsOrdered()
    .WithDegreeOfParallelism(parallelism)
    .Select(file => interpret ? TestCase.RunInterpreted(file, casesDirectory) : TestCase.Run(file, casesDirectory, outputDirectory, toolchain, options))
    .ToList();
results.AddRange(IrSelfTests.Run().Concat(TypeSelfTests.Run()).Concat(DispatchSelfTests.Run()).Concat(InterpreterSelfTests.Run()).Where(r => filter is null || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)));
results.AddRange(FuzzSelfTests.Run(casesDirectory)
    .Select(r => new TestResult("fuzz/" + r.Name, r.Failure))
    .Where(r => filter is null || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)));
// The build cache and the matrices need a C++ toolchain; the interpret mode must not, and step 6
// runs the matrices interpreted.
if (!interpret)
{
    results.AddRange(BuildCacheSelfTests.Run().Where(r => filter is null || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    results.AddRange(MatrixTests.Run(root, outputDirectory, toolchain, options, filter, updateSnapshots, parallelism));
}
// Every program passing proves nothing if the sanitizers have silently stopped working.
if (toolchain == Toolchain.ClangSanitized)
    results.Add(SanitizersLive(outputDirectory));

foreach (var result in results.Where(r => r.Failure is not null))
{
    Console.WriteLine($"FAIL {result.Name}");
    foreach (var line in result.Failure!.Split('\n'))
        Console.WriteLine($"     {line}");
}

int failed = results.Count(r => r.Failure is not null);
Console.WriteLine($"{results.Count - failed} passed, {failed} failed ({stopwatch.Elapsed.TotalSeconds:F1}s)");
return failed == 0 ? 0 : 1;

/// <summary>
/// The five modes, each in its own process and output directory, so the machine's cores fill instead
/// of one mode at a time using half of them.
/// </summary>
static int RunAllModes(string outputDirectory, string? filter, int jobs)
{
    (string Key, string[] Arguments)[] modes =
    [
        ("msvc", []),
        ("gcc", ["--cc", "gcc"]),
        ("no-opt", ["--no-opt"]),
        ("no-nest", ["--no-nest"]),
        ("asan", ["--cc", "asan"]),
    ];
    var assembly = typeof(MatrixTests).Assembly.Location;
    var stopwatch = Stopwatch.StartNew();
    var runs = modes.Select(mode => Task.Run(() =>
    {
        var startInfo = new ProcessStartInfo("dotnet");
        startInfo.ArgumentList.Add(assembly);
        foreach (var argument in mode.Arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var argument in new[] { "--out", Path.Combine(outputDirectory, mode.Key), "--jobs", jobs.ToString(CultureInfo.InvariantCulture) })
            startInfo.ArgumentList.Add(argument);
        if (filter is not null)
            startInfo.ArgumentList.Add(filter);
        return (mode.Key, Result: ProcessRunner.Run(startInfo, TimeSpan.FromHours(2)));
    })).ToList();
    Task.WhenAll(runs).GetAwaiter().GetResult();

    var summary = new Regex(@"^(\d+) passed, (\d+) failed", RegexOptions.CultureInvariant);
    int passed = 0, failed = 0;
    foreach (var task in runs)
    {
        var (key, result) = task.Result;
        Console.WriteLine($"=== {key}");
        bool reported = false;
        foreach (var line in result.StandardOutput.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Console.WriteLine(line.StartsWith("FAIL ", StringComparison.Ordinal) ? $"FAIL {key}/{line["FAIL ".Length..]}" : line);
            var match = summary.Match(line);
            if (match.Success)
            {
                passed += int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                failed += int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                reported = true;
            }
        }
        if (result.StandardError.Trim() is { Length: > 0 } error)
            Console.Error.WriteLine(error);
        if (!reported)
        {
            failed++;
            Console.WriteLine($"FAIL {key}: the mode did not report a result (exit {result.ExitCode}{(result.TimedOut ? ", timed out" : "")})");
        }
    }
    Console.WriteLine($"{passed} passed, {failed} failed ({stopwatch.Elapsed.TotalSeconds:F1}s)");
    return failed == 0 ? 0 : 1;
}

static TestResult SanitizersLive(string outputDirectory)
{
    const string name = "sanitizers/live";
    var directory = Path.Combine(outputDirectory, "sanitizers");
    Directory.CreateDirectory(directory);
    var cpp = Path.Combine(directory, "live.cpp");
    File.WriteAllText(cpp, """
        #include <climits>
        #include <cstdio>
        int main(int argc, char**) {
            int largest = INT_MAX - 1 + argc;
            std::printf("%d\n", largest + argc);
            int* freed = new int(1);
            delete freed;
            return *freed;
        }
        """);
    var compiler = NativeCompiler.Find(Toolchain.ClangSanitized, out _)!;
    var build = compiler.Build(cpp, Path.ChangeExtension(cpp, ".exe"), release: false, precompiledHeader: false);
    if (!build.Success)
        return new(name, build.Output);
    var run = ProcessRunner.Run(new ProcessStartInfo(Path.ChangeExtension(cpp, ".exe")), TimeSpan.FromSeconds(30));
    var missing = new[] { "runtime error: signed integer overflow", "heap-use-after-free" }.Where(r => !run.StandardError.Contains(r, StringComparison.Ordinal)).ToList();
    if (NativeCompiler.SanitizerReport(run.StandardError) is null)
        missing.Add("anything SanitizerReport finds");
    return new(name, missing.Count == 0 ? null : $"no report of: {string.Join(", ", missing)}; stderr: {run.StandardError.Trim()}");
}

static string FindRepositoryRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "tests", "cases")))
            return dir.FullName;
    }
    throw new DirectoryNotFoundException("could not find tests/cases above " + AppContext.BaseDirectory);
}

sealed record TestResult(string Name, string? Failure);

sealed record Expectations(
    List<string> Output, List<(int Line, string Text)> Errors, List<string> Warnings, string? Panic, int? PanicLine, bool Release,
    Dictionary<int, int> Stack, Dictionary<int, int> InPlace, bool Optimized = false);

static partial class TestCase
{
    // A line may hold several, as two errors reported at one location need.
    [GeneratedRegex(@"//\s*(output|error|warning|panic|panic-here|mode|stack|in-place):(.*?)(?=//\s*(?:output|error|warning|panic|panic-here|mode|stack|in-place):|$)")]
    private static partial Regex DirectivePattern();

    public static TestResult Run(string file, string casesDirectory, string outputDirectory, Toolchain? toolchain, CompileOptions options)
    {
        var name = Path.GetRelativePath(casesDirectory, file).Replace('\\', '/');
        try
        {
            return new TestResult(name, Check(file, name, outputDirectory, toolchain, options));
        }
        catch (Exception e)
        {
            return new TestResult(name, $"exception: {e}");
        }
    }

    static Expectations Parse(string file)
    {
        var expectations = new Expectations([], [], [], null, null, false, [], []);
        var lines = File.ReadAllLines(file);
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Match match in DirectivePattern().Matches(lines[i]))
                Add(match, i);
        }
        return expectations;

        void Add(Match match, int i)
        {
            var value = match.Groups[2].Value;
            value = (value.StartsWith(' ') ? value[1..] : value).TrimEnd();
            switch (match.Groups[1].Value)
            {
                case "output": expectations.Output.Add(value); break;
                case "error": expectations.Errors.Add((i + 1, value)); break;
                case "warning": expectations.Warnings.Add(value); break;
                case "panic": expectations = expectations with { Panic = value }; break;
                case "panic-here": expectations = expectations with { Panic = value, PanicLine = i + 1 }; break;
                case "mode" when value == "optimized": expectations = expectations with { Optimized = true }; break;
                case "mode": expectations = expectations with { Release = expectations.Release || value == "release" }; break;
                case "stack": expectations.Stack[i + 1] = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "in-place": expectations.InPlace[i + 1] = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            }
        }
    }

    static string? Check(string file, string name, string outputDirectory, Toolchain? toolchain, CompileOptions options)
    {
        var expected = Parse(file);
        if (expected.Optimized && !options.Optimize)
            return null;
        var source = SourceText.FromFile(file);
        var compilation = Compilation.Compile(source, options);
        var errors = compilation.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

        if (expected.Errors.Count > 0 || errors.Count > 0)
            return CompareErrors(expected.Errors, errors);

        var directory = Path.Combine(outputDirectory, Path.ChangeExtension(name, null));
        var build = Driver.BuildExecutable(
            compilation, Path.GetFileNameWithoutExtension(file), directory, new BuildOptions(expected.Release, toolchain));
        var irFile = Path.Combine(directory, Path.GetFileNameWithoutExtension(file) + ".ir");
        if (compilation.IrText() is { } ir)
            File.WriteAllText(irFile, ir);
        else
            File.Delete(irFile);
        if (!build.Success)
            return build.Error;

        var run = ProcessRunner.Run(
            new ProcessStartInfo(build.ExePath!)
            {
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
            TimeSpan.FromSeconds(10));
        if (run.TimedOut)
            return "timed out";

        var problems = new List<string>();
        // The markers count optimizations, which --no-opt turns off.
        if (expected.Stack.Count > 0 && options.Optimize)
            problems.AddRange(CompareCounts("objects on the stack", expected.Stack, compilation.StackObjects.Where(l => l.Source == source)));
        if (expected.InPlace.Count > 0 && options.Optimize)
            problems.AddRange(CompareCounts("loop elements in place", expected.InPlace, compilation.InPlaceElements.Where(l => l.Source == source)));
        var actualOutput = run.StandardOutput.Replace("\r\n", "\n").Split('\n').ToList();
        if (actualOutput[^1] == "")
            actualOutput.RemoveAt(actualOutput.Count - 1);
        if (!actualOutput.SequenceEqual(expected.Output))
        {
            problems.Add("output mismatch");
            problems.Add("  expected: " + string.Join(" | ", expected.Output));
            problems.Add("  actual:   " + string.Join(" | ", actualOutput));
        }

        var stderr = run.StandardError.Trim().Replace("\r\n", "\n");
        if (NativeCompiler.SanitizerReport(stderr) is { } report)
            problems.Add($"sanitizer: {report}\n{stderr}");
        if (expected.Panic is not null)
        {
            var at = expected.PanicLine is { } line ? $"\n    at {Path.GetFileName(file)}:{line}:" : "";
            if (run.ExitCode == 0 || !stderr.Contains("panic: " + expected.Panic) || !stderr.Contains(at))
                problems.Add($"expected panic '{expected.Panic}'{(at.Length > 0 ? $" at line {expected.PanicLine}" : "")}, got exit code {run.ExitCode} and stderr: {stderr}");
        }
        else if (run.ExitCode != 0)
        {
            problems.Add($"exit code {run.ExitCode}; stderr: {stderr}");
        }

        const string warningPrefix = "performance warning: ";
        var warnings = stderr.Split('\n').Where(l => l.StartsWith(warningPrefix)).Select(l => l[warningPrefix.Length..]).ToList();
        foreach (var warning in expected.Warnings)
        {
            int index = warnings.FindIndex(w => w.Contains(warning, StringComparison.Ordinal));
            if (index >= 0)
                warnings.RemoveAt(index);
            else
                problems.Add($"missing warning: {warning}");
        }
        problems.AddRange(warnings.Select(w => $"unexpected warning: {w}"));

        return problems.Count == 0 ? null : string.Join('\n', problems);
    }

    static IEnumerable<string> CompareCounts(string what, Dictionary<int, int> expected, IEnumerable<Location> actual)
    {
        var counts = actual.GroupBy(l => l.LineColumn.Line).ToDictionary(g => g.Key, g => g.Count());
        return expected.Keys.Union(counts.Keys).Order()
            .Where(line => expected.GetValueOrDefault(line) != counts.GetValueOrDefault(line))
            .Select(line => $"line {line}: expected {expected.GetValueOrDefault(line)} {what}, got {counts.GetValueOrDefault(line)}");
    }

    static string? CompareErrors(List<(int Line, string Text)> expected, List<Diagnostic> actual)
    {
        var unmatched = new List<(int Line, string Text)>(expected);
        var problems = new List<string>();
        foreach (var diagnostic in actual)
        {
            int index = unmatched.FindIndex(e => e.Line == diagnostic.Line && diagnostic.Message.Contains(e.Text, StringComparison.Ordinal));
            if (index >= 0)
                unmatched.RemoveAt(index);
            else
                problems.Add($"unexpected error: {diagnostic}");
        }
        foreach (var (line, text) in unmatched)
            problems.Add($"missing error on line {line}: {text}");
        return problems.Count == 0 ? null : string.Join('\n', problems);
    }
}
