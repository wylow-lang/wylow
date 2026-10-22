using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Vpl.Compiler;
using Vpl.Compiler.Build;
using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Text;

// Runs every tests/cases/**/*.vpl file. Expectations are comments in the file:
//   // output: <line>     expected stdout line (in order of appearance)
//   // error: <text>      an error on this line whose message contains <text>
//   // panic: <text>      the program panics with a message starting with <text>
//   // panic-here: <text> the same, reported at this line
//   // warning: <text>    the program reports a performance warning containing <text>; any other fails
//   // mode: release      build with --release
//   // stack: <n>         <n> objects made on this line are on the stack; a file using this checks every line
// Usage: Vpl.Tests [--cc msvc|gcc] [filter]

Toolchain? toolchain = null;
string? filter = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--cc" && i + 1 < args.Length)
        toolchain = args[++i] == "gcc" ? Toolchain.Gcc : Toolchain.Msvc;
    else
        filter = args[i];
}

var root = FindRepositoryRoot();
var casesDirectory = Path.Combine(root, "tests", "cases");
var outputDirectory = Path.Combine(root, "tests", ".out");

if (NativeCompiler.Find(toolchain, out var toolchainError) is null)
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
    .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount / 2))
    .Select(file => TestCase.Run(file, casesDirectory, outputDirectory, toolchain))
    .ToList();

foreach (var result in results.Where(r => r.Failure is not null))
{
    Console.WriteLine($"FAIL {result.Name}");
    foreach (var line in result.Failure!.Split('\n'))
        Console.WriteLine($"     {line}");
}

int failed = results.Count(r => r.Failure is not null);
Console.WriteLine($"{results.Count - failed} passed, {failed} failed ({stopwatch.Elapsed.TotalSeconds:F1}s)");
return failed == 0 ? 0 : 1;

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
    List<string> Output, List<(int Line, string Text)> Errors, List<string> Warnings, string? Panic, int? PanicLine, bool Release, Dictionary<int, int> Stack);

static partial class TestCase
{
    [GeneratedRegex(@"//\s*(output|error|warning|panic|panic-here|mode|stack):(.*)$")]
    private static partial Regex DirectivePattern();

    public static TestResult Run(string file, string casesDirectory, string outputDirectory, Toolchain? toolchain)
    {
        var name = Path.GetRelativePath(casesDirectory, file).Replace('\\', '/');
        try
        {
            return new TestResult(name, Check(file, name, outputDirectory, toolchain));
        }
        catch (Exception e)
        {
            return new TestResult(name, $"exception: {e}");
        }
    }

    static Expectations Parse(string file)
    {
        var expectations = new Expectations([], [], [], null, null, false, []);
        var lines = File.ReadAllLines(file);
        for (int i = 0; i < lines.Length; i++)
        {
            var match = DirectivePattern().Match(lines[i]);
            if (!match.Success)
                continue;
            var value = match.Groups[2].Value;
            value = (value.StartsWith(' ') ? value[1..] : value).TrimEnd();
            switch (match.Groups[1].Value)
            {
                case "output": expectations.Output.Add(value); break;
                case "error": expectations.Errors.Add((i + 1, value)); break;
                case "warning": expectations.Warnings.Add(value); break;
                case "panic": expectations = expectations with { Panic = value }; break;
                case "panic-here": expectations = expectations with { Panic = value, PanicLine = i + 1 }; break;
                case "mode": expectations = expectations with { Release = value == "release" }; break;
                case "stack": expectations.Stack[i + 1] = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            }
        }
        return expectations;
    }

    static string? Check(string file, string name, string outputDirectory, Toolchain? toolchain)
    {
        var expected = Parse(file);
        var source = SourceText.FromFile(file);
        var compilation = Compilation.Compile(source);
        var errors = compilation.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

        if (expected.Errors.Count > 0 || errors.Count > 0)
            return CompareErrors(expected.Errors, errors);

        var directory = Path.Combine(outputDirectory, Path.ChangeExtension(name, null));
        var build = Driver.BuildExecutable(
            compilation, Path.GetFileNameWithoutExtension(file), directory, new BuildOptions(expected.Release, toolchain));
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
        if (expected.Stack.Count > 0)
            problems.AddRange(CompareStack(expected.Stack, compilation.StackObjects.Where(l => l.Source == source)));
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

    static IEnumerable<string> CompareStack(Dictionary<int, int> expected, IEnumerable<Location> actual)
    {
        var counts = actual.GroupBy(l => l.LineColumn.Line).ToDictionary(g => g.Key, g => g.Count());
        return expected.Keys.Union(counts.Keys).Order()
            .Where(line => expected.GetValueOrDefault(line) != counts.GetValueOrDefault(line))
            .Select(line => $"line {line}: expected {expected.GetValueOrDefault(line)} objects on the stack, got {counts.GetValueOrDefault(line)}");
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
