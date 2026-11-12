using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Wyl.Compiler;
using Wyl.Compiler.Build;
using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;
using Wyl.Fuzz;
using Wyl.Harness;

// Runs every tests/cases/**/*.wyl file, and every package directory there (one with a package.wylcfg,
// spec programs.packages) as one program. Expectations are comments in the file; in a package,
// `// error[WYLnnnn]:` and `// warning[WYLnnnn]:` may be in any of its files, the manifest too, and the others
// only in the file with main:
//   // output: <line>     expected stdout line (in order of appearance)
//   // error[WYL0001]: <text>  an error on this line with this code and exactly this message
//   // warning[WYL0480]: <text>  a compile-time warning, likewise; any warning not listed fails
//   // panic: <text>      the program panics with a message starting with <text>
//   // panic-here: <text> the same, reported at this line
//   // warning: <text>    the program reports a performance warning containing <text>; any other fails
//   // args: <a> <b>      run with these arguments, split at spaces
//   // exit: <n>          the program exits with status <n>; without one, 0
//   // mode: release      build with --release
//   // mode: optimized    skip under --no-opt: the test observes an optimization, through diagnostics
//   // stack: <n>         <n> objects made on this line are on the stack; a file using this checks every line
//   // in-place: <n>      <n> loop elements declared on this line refer to their elements in place; likewise
//   // native             the program calls C++ code, so the interpreted mode skips it
// Usage: Wyl.Tests [--cc clang|msvc|gcc|asan|interpret] [--interpret] [--no-nest] [--no-opt] [--all-modes]
//                  [--out <dir>] [--jobs <n>] [--update-snapshots] [filter]
// With --cc asan, programs are built with AddressSanitizer and UBSan, and any report fails the test.
// --interpret runs the IR interpreter instead of a native build.
// --all-modes runs CI's seven modes one per process at once, each writing under <out>/<mode>.
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
            "clang" => Toolchain.Clang,
            "gcc" => Toolchain.Gcc,
            "asan" => Toolchain.ClangSanitized,
            "tsan" => Toolchain.ThreadSanitized,
            "msvc" => Toolchain.Msvc,
            "interpret" => null,
            var other => throw new ArgumentException($"unknown compiler '{other}'; expected clang, msvc, gcc, asan, tsan, or interpret"),
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

var files = TestCases.All(casesDirectory)
    .Where(f => filter is null || f.Contains(filter, StringComparison.OrdinalIgnoreCase))
    .ToList();

var stopwatch = Stopwatch.StartNew();

// Each section's results are named with its prefix, so a filter runs a section only if the filter is
// part of the prefix ("catalog") or starts with it ("ir/borrows"); the matrices filter their own names.
List<(string? Prefix, Func<IEnumerable<TestResult>> Run)> sections =
[
    ("ir/", IrSelfTests.Run), ("analysis/", AnalysisSelfTests.Run), ("types/", TypeSelfTests.Run),
    ("dispatch/", DispatchSelfTests.Run), ("interpreter/", InterpreterSelfTests.Run), ("source-text/", SourceTextSelfTests.Run),
    ("static-ensures/", StaticEnsureSelfTests.Run), ("sanitizer/", SanitizerSelfTests.Run),
    ("catalog/", () => CatalogSelfTests.Run(root)), ("spec/", () => SpecExampleSelfTests.Run(root)), ("docs/", () => DocSelfTests.Run(root)),
    ("fuzz/", () => FuzzSelfTests.Run(casesDirectory).Select(r => new TestResult("fuzz/" + r.Name, r.Failure))),
];
// The build cache needs a C++ toolchain; the matrices run through the mode's own oracle, which the
// interpret mode supplies without one.
if (!interpret)
{
    sections.Add(("build-cache/", () => BuildCacheSelfTests.Run(outputDirectory)));
    sections.Add((RuntimeSelfTests.PoolsName, () => [RuntimeSelfTests.Pools(root, outputDirectory, toolchain)]));
}
sections.Add((null, () => MatrixTests.Run(root, outputDirectory, toolchain, options, filter, updateSnapshots, parallelism, interpret)));

// The sections run beside the test cases and each other, each on a thread of its own, since most
// wait on parallel work of their own; their results keep the order above.
var running = sections
    .Where(section => filter is null || section.Prefix is null
        || section.Prefix.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || filter.StartsWith(section.Prefix, StringComparison.OrdinalIgnoreCase))
    .Select(section => Task.Factory.StartNew(
        () => section.Run().Where(r => filter is null || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList(),
        TaskCreationOptions.LongRunning))
    .ToList();
var results = files
    .AsParallel()
    .AsOrdered()
    .WithDegreeOfParallelism(parallelism)
    .Select(file => interpret ? TestCase.RunInterpreted(file, casesDirectory) : TestCase.Run(file, casesDirectory, outputDirectory, toolchain, options))
    .ToList();
foreach (var section in running)
    results.AddRange(section.Result);
// Every program passing proves nothing if the sanitizers have silently stopped working.
if (toolchain == Toolchain.ClangSanitized)
    results.Add(SanitizersLive(outputDirectory));
if (toolchain == Toolchain.ThreadSanitized)
    results.Add(ThreadSanitizersLive(outputDirectory));

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
/// CI's seven modes, each in its own process and output directory, so the machine's cores fill instead
/// of one mode at a time using half of them.
/// </summary>
static int RunAllModes(string outputDirectory, string? filter, int jobs)
{
    (string Key, string[] Arguments)[] modes =
    [
        ("clang", ["--cc", "clang"]),
        ("gcc", ["--cc", "gcc"]),
        ("no-opt", ["--no-opt"]),
        ("no-nest", ["--no-nest"]),
        ("asan", ["--cc", "asan"]),
        ("asan-no-nest", ["--cc", "asan", "--no-nest"]),
        ("interpret", ["--interpret"]),
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

static TestResult ThreadSanitizersLive(string outputDirectory)
{
    const string name = "sanitizers/live-tsan";
    var directory = Path.Combine(outputDirectory, "sanitizers", "tsan");
    Directory.CreateDirectory(directory);
    var cpp = Path.Combine(directory, "race.cpp");
    File.WriteAllText(cpp, """
        #include <thread>
        int counter = 0;
        int main() {
            std::thread a([] { for (int i = 0; i < 100000; i++) counter++; });
            std::thread b([] { for (int i = 0; i < 100000; i++) counter++; });
            a.join();
            b.join();
            return counter == 0;
        }
        """);
    var compiler = NativeCompiler.Find(Toolchain.ThreadSanitized, out _)!;
    var exe = Path.ChangeExtension(cpp, OperatingSystem.IsWindows() ? ".exe" : null);
    var build = compiler.Build(cpp, exe, release: false, precompiledHeader: false);
    if (!build.Success)
        return new(name, build.Output);
    var run = ProcessRunner.Run(new ProcessStartInfo(exe), TimeSpan.FromSeconds(60));
    return NativeCompiler.SanitizerReport(run.StandardError) is null
        ? new(name, $"no race report; stderr: {run.StandardError.Trim()}")
        : new(name, null);
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
    List<string> Output, List<(string File, int Line, string Code, string Text)> Errors, List<(string File, int Line, string Code, string Text)> CompileWarnings,
    List<string> Warnings, string? Panic, int? PanicLine, bool Release,
    Dictionary<int, int> Stack, Dictionary<int, int> InPlace, bool Optimized = false, string[]? Arguments = null, int Exit = 0);

static partial class TestCase
{
    // A line may hold several, as two errors reported at one location need.
    [GeneratedRegex(@"//\s*(output|error(?:\[WYL\d{4}\])?|warning(?:\[WYL\d{4}\])?|panic|panic-here|mode|stack|in-place|args|exit):(.*?)(?=//\s*(?:output|error(?:\[WYL\d{4}\])?|warning(?:\[WYL\d{4}\])?|panic|panic-here|mode|stack|in-place|args|exit):|$)")]
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
        var expectations = new Expectations([], [], [], [], null, null, false, [], []);
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
            var directive = match.Groups[1].Value;
            if (directive.StartsWith("error[", StringComparison.Ordinal))
            {
                expectations.Errors.Add((Path.GetFullPath(file), i + 1, directive["error[".Length..^1], value));
                return;
            }
            if (directive.StartsWith("warning[", StringComparison.Ordinal))
            {
                expectations.CompileWarnings.Add((Path.GetFullPath(file), i + 1, directive["warning[".Length..^1], value));
                return;
            }
            switch (directive)
            {
                case "output": expectations.Output.Add(value); break;
                case "error": throw new InvalidOperationException($"{file}:{i + 1}: an error marker carries its code: '// error[WYLnnnn]: <message>'");
                case "warning": expectations.Warnings.Add(value); break;
                case "panic": expectations = expectations with { Panic = value }; break;
                case "panic-here": expectations = expectations with { Panic = value, PanicLine = i + 1 }; break;
                case "mode" when value == "optimized": expectations = expectations with { Optimized = true }; break;
                case "mode": expectations = expectations with { Release = expectations.Release || value == "release" }; break;
                case "args": expectations = expectations with { Arguments = value.Split(' ', StringSplitOptions.RemoveEmptyEntries) }; break;
                case "exit": expectations = expectations with { Exit = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) }; break;
                case "stack": expectations.Stack[i + 1] = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "in-place": expectations.InPlace[i + 1] = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            }
        }
    }

    /// <summary>A case's sources, what its comments expect, and the file with every marker but errors.</summary>
    sealed record Case(PackageSources Sources, Expectations Expected, string MainFile, string MainName, string Text, string? Problem);

    /// <summary>
    /// A file on its own, or a package directory, whose <c>// error:</c> comments come from all of its
    /// files and whose other markers come from the one file that has any, which must be the file with
    /// <c>main</c>.
    /// </summary>
    static Case Load(string path)
    {
        if (!Directory.Exists(path))
        {
            var file = Path.GetFullPath(path);
            return new Case(PackageSources.Of(SourceText.FromFile(file)), Parse(file), file, Path.GetFileName(file), File.ReadAllText(file), null);
        }
        var sources = PackageSources.Load(path);
        var files = Directory.GetFiles(path, "*.wyl", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Prepend(Path.Combine(path, Manifest.FileName))
            .Select(Path.GetFullPath)
            .ToList();
        var parsed = files.Select(f => (File: f, Expected: Parse(f))).ToList();
        var marked = parsed.Where(p => p.Expected is { Output.Count: > 0 } or { Panic: not null } or { Warnings.Count: > 0 } or { Release: true }
            or { Optimized: true } or { Stack.Count: > 0 } or { InPlace.Count: > 0 }).ToList();
        string? problem = marked.Count > 1
            ? $"only the file with 'main' may have markers other than // error:, but {string.Join(" and ", marked.Select(m => Path.GetRelativePath(path, m.File)))} do"
            : null;
        var (mainFile, main) = marked.Count > 0 ? marked[0] : (files[0], parsed[0].Expected);
        var mainName = sources.Files.FirstOrDefault(f => Path.GetFullPath(f.Path) == mainFile)?.Name ?? Path.GetFileName(mainFile);
        return new Case(sources, main with { Errors = [.. parsed.SelectMany(p => p.Expected.Errors)], CompileWarnings = [.. parsed.SelectMany(p => p.Expected.CompileWarnings)] }, mainFile, mainName,
            string.Join("\n", files.Select(File.ReadAllText)), problem);
    }

    static string? Check(string file, string name, string outputDirectory, Toolchain? toolchain, CompileOptions options)
    {
        var test = Load(file);
        if (test.Problem is not null)
            return test.Problem;
        var expected = test.Expected;
        if (expected.Optimized && !options.Optimize)
            return null;
        if (toolchain == Toolchain.ThreadSanitized && !test.Text.Contains("// tsan", StringComparison.Ordinal))
            return null;
        var compilation = Compilation.Compile(test.Sources, options);
        if (CompareDiagnostics(expected, compilation.Diagnostics) is { } diagnosticProblems)
            return diagnosticProblems;
        if (expected.Errors.Count > 0)
            return null;
        if (test.Sources.Files.Count > 1 && compilation.Program?.Main?.Declaration?.Location.Source is { } mainSource
            && Path.GetFullPath(mainSource.Path) != test.MainFile && (expected.Output.Count > 0 || expected.Panic is not null))
            return $"the markers are in {test.MainName}, but 'main' is in {mainSource.Name}";

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
            WithArguments(new ProcessStartInfo(build.ExePath!)
            {
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            }, expected.Arguments),
            TimeSpan.FromSeconds(10));
        if (run.TimedOut)
            return "timed out";

        var problems = new List<string>();
        // The markers count optimizations, which --no-opt turns off.
        if (expected.Stack.Count > 0 && options.Optimize)
            problems.AddRange(CompareCounts("objects on the stack", expected.Stack, compilation.StackObjects.Where(l => Path.GetFullPath(l.Source.Path) == test.MainFile)));
        if (expected.InPlace.Count > 0 && options.Optimize)
            problems.AddRange(CompareCounts("loop elements in place", expected.InPlace, compilation.InPlaceElements.Where(l => Path.GetFullPath(l.Source.Path) == test.MainFile)));
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
            var at = expected.PanicLine is { } line ? $"\n    at {test.MainName}:{line}:" : "";
            if (run.ExitCode == 0 || !stderr.Contains("panic: " + expected.Panic) || !stderr.Contains(at))
                problems.Add($"expected panic '{expected.Panic}'{(at.Length > 0 ? $" at line {expected.PanicLine}" : "")}, got exit code {run.ExitCode} and stderr: {stderr}");
        }
        else if (run.ExitCode != expected.Exit)
        {
            problems.Add($"exit code {run.ExitCode}, expected {expected.Exit}; stderr: {stderr}");
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

    static ProcessStartInfo WithArguments(ProcessStartInfo start, string[]? arguments)
    {
        foreach (var argument in arguments ?? [])
            start.ArgumentList.Add(argument);
        return start;
    }

    static IEnumerable<string> CompareCounts(string what, Dictionary<int, int> expected, IEnumerable<Location> actual)
    {
        var counts = actual.GroupBy(l => l.LineColumn.Line).ToDictionary(g => g.Key, g => g.Count());
        return expected.Keys.Union(counts.Keys).Order()
            .Where(line => expected.GetValueOrDefault(line) != counts.GetValueOrDefault(line))
            .Select(line => $"line {line}: expected {expected.GetValueOrDefault(line)} {what}, got {counts.GetValueOrDefault(line)}");
    }

    /// <summary>
    /// The compile's errors and warnings against the markers, each kind on its own: a program with
    /// errors is not run, and one with only warnings is.
    /// </summary>
    static string? CompareDiagnostics(Expectations expected, IEnumerable<Diagnostic> diagnostics)
    {
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        var warnings = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();
        var problems = new[] { CompareMarked("error", expected.Errors, errors), CompareMarked("warning", expected.CompileWarnings, warnings) }.OfType<string>().ToList();
        return problems.Count > 0 ? string.Join('\n', problems) : null;
    }

    static string? CompareMarked(string kind, List<(string File, int Line, string Code, string Text)> expected, List<Diagnostic> actual)
    {
        var unmatched = new List<(string File, int Line, string Code, string Text)>(expected);
        var problems = new List<string>();
        foreach (var diagnostic in actual)
        {
            var file = Path.GetFullPath(diagnostic.Location.Source.Path);
            int index = unmatched.FindIndex(e => e.File == file && e.Line == diagnostic.Line
                && e.Code == diagnostic.Code && e.Text == diagnostic.Message);
            if (index >= 0)
                unmatched.RemoveAt(index);
            else
                problems.Add($"unexpected {kind}: {diagnostic.Primary}");
        }
        bool files = expected.Select(e => e.File).Distinct().Count() > 1;
        foreach (var (file, line, code, text) in unmatched)
            problems.Add($"missing {kind} on line {line}{(files ? $" of {Path.GetFileName(file)}" : "")}: [{code}] {text}");
        return problems.Count == 0 ? null : string.Join('\n', problems);
    }
}
