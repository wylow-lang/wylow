using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Vpl.Compiler;
using Vpl.Compiler.Build;
using Vpl.Fuzz;

// Makes near-valid programs by mutating tests/cases/**/*.vpl and checks them against the oracles:
//   tier 1  compile in-process, with and without --no-opt: no exception, the same errors, determinism
//   tier 2  build and run every compiler mode: the C++ compiles warning-free, the sanitizers are quiet,
//           and every mode prints the same output, exits the same, and panics the same
// A finding is reduced, then written to tests/fuzz/findings/ with the seed that reproduces it.
// Usage: Vpl.Fuzz [--seed N] [--seconds S] [--max N] [--tier 1|2] [--jobs N] [--filter text]
//                 [--errors] [--no-strict] [--keep] [--replay N] [--list] [--self-test]

int seed = 1;
double seconds = 120;
int max = 0;
int tier = 1;
int? jobs = null;
string? filter = null;
bool includeErrors = false;
bool strict = true;
bool keep = false;
int? replay = null;
bool list = false;
bool selfTest = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seed" when i + 1 < args.Length: seed = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--seconds" when i + 1 < args.Length: seconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--max" when i + 1 < args.Length: max = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--tier" when i + 1 < args.Length: tier = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--jobs" when i + 1 < args.Length: jobs = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--filter" when i + 1 < args.Length: filter = args[++i]; break;
        case "--replay" when i + 1 < args.Length: replay = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--errors": includeErrors = true; break;
        case "--no-strict": strict = false; break;
        case "--keep": keep = true; break;
        case "--list": list = true; break;
        case "--self-test": selfTest = true; break;
        default:
            Console.Error.WriteLine($"unknown option '{args[i]}'");
            return 1;
    }
}

if (tier is not (1 or 2))
{
    Console.Error.WriteLine("--tier must be 1 or 2");
    return 1;
}

if (list)
{
    foreach (var mutation in Mutations.All)
        Console.WriteLine($"{mutation.Name,-24} from {mutation.Kind}");
    return 0;
}

var root = FindRepositoryRoot();
var casesDirectory = Path.Combine(root, "tests", "cases");
var runDirectory = Path.Combine(root, "tests", ".out", "fuzz", $"seed{seed}-{Environment.ProcessId}");
var findingsDirectory = Path.Combine(root, "tests", "fuzz", "findings");
var corpus = Mutator.Load(casesDirectory, filter, includeErrors);
if (corpus.Count == 0)
{
    Console.Error.WriteLine("no test files match");
    return 1;
}

List<Mode> modes = [new("msvc", Toolchain.Msvc, new CompileOptions())];
if (tier == 2)
{
    if (AvailableModes() is not { } available)
        return 1;
    modes = available;
}

double runTimeout = 5;
int parallelism = jobs ?? Math.Max(1, Environment.ProcessorCount / 2);
Console.WriteLine($"seed {seed}, tier {tier}, {seconds:F0}s, {parallelism} jobs");
Console.WriteLine($"corpus {corpus.Count} tests, {Mutations.All.Length} mutations");
if (tier == 2)
    Console.WriteLine("modes: " + string.Join(", ", modes.Select(m => m.Name)) + (strict ? " (strict warnings)" : ""));
Directory.CreateDirectory(findingsDirectory);

if (selfTest)
{
    int failures = 0;
    foreach (var (name, failure) in FuzzSelfTests.Run(casesDirectory))
    {
        Console.WriteLine(failure is null ? $"ok {name}" : $"FAIL {name}: {failure}");
        failures += failure is null ? 0 : 1;
    }
    return failures == 0 ? 0 : 1;
}

var mutator = new Mutator(corpus, seed);
if (replay is { } replayIndex)
    return Replay(replayIndex);

var seen = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
var printLock = new object();
long checkedCount = 0, compiledCount = 0, findingCount = 0;
var stopwatch = Stopwatch.StartNew();
var lastReport = TimeSpan.Zero;
var budget = TimeSpan.FromSeconds(seconds);

while (stopwatch.Elapsed < budget && (max == 0 || mutator.Count < max))
{
    var batch = new List<Mutant>();
    for (int i = 0; i < 64 && (max == 0 || mutator.Count < max); i++)
    {
        var mutant = mutator.Next();
        if (mutant.Description != "none")
            batch.Add(mutant);
    }
    if (batch.Count == 0)
        break;

    var results = batch
        .AsParallel()
        .WithDegreeOfParallelism(parallelism)
        .Select(mutant => Work(mutant))
        .ToList();

    foreach (var result in results)
    {
        Interlocked.Increment(ref checkedCount);
        if (result)
            Interlocked.Increment(ref compiledCount);
    }

    Report();
}

Console.WriteLine($"{checkedCount} mutants, {compiledCount} compiled, {findingCount} findings ({checkedCount / Math.Max(stopwatch.Elapsed.TotalSeconds, 1):F0}/s, {findingCount * 3600 / Math.Max(stopwatch.Elapsed.TotalSeconds, 1):F1}/h)");
if (!keep)
    TryDelete(runDirectory);
return findingCount == 0 ? 0 : 1;

bool Work(Mutant mutant)
{
    var directory = Path.Combine(runDirectory, $"m{mutant.Index}");
    var checker = new Checker(directory, modes, runTimeout);
    var finding = checker.Check(mutant.Text, checker.Tier1, out bool compiled);
    if (finding is null && compiled && tier == 2)
        finding = checker.Check(mutant.Text, checker.Tier2(strict), out _, mutant.UnsafeCode);
    if (finding is null)
        return compiled;

    if (!seen.TryAdd(finding.Key, 0))
        return compiled;

    var reduced = finding.Scope is { } scope
        ? Reducer.Reduce(mutant.Text, ReduceCheck, finding.Signature, maxChecks: 120, TimeSpan.FromSeconds(90))
        : mutant.Text;
    WriteFinding(findingsDirectory, finding, mutant, reduced, seed);
    Interlocked.Increment(ref findingCount);
    lock (printLock)
    {
        Console.WriteLine($"FINDING {finding.Key}");
        Console.WriteLine($"        mutant {mutant.Index}: {mutant.Description}");
        foreach (var line in finding.Detail.Split('\n').Take(6))
            Console.WriteLine("        " + line);
    }
    return compiled;

    string? ReduceCheck(string candidate) => checker.Check(candidate, scope, out _, mutant.UnsafeCode)?.Signature;
}

void Report()
{
    if (stopwatch.Elapsed - lastReport < TimeSpan.FromSeconds(10))
        return;
    lastReport = stopwatch.Elapsed;
    Console.WriteLine($"{checkedCount} mutants, {compiledCount} compiled, {findingCount} findings ({checkedCount / Math.Max(stopwatch.Elapsed.TotalSeconds, 1):F0}/s, {findingCount * 3600 / Math.Max(stopwatch.Elapsed.TotalSeconds, 1):F1}/h)");
}

int Replay(int index)
{
    Mutant? target = null;
    for (int i = 0; i <= index; i++)
        target = mutator.Next();
    if (target is null)
    {
        Console.Error.WriteLine($"no mutant {index}");
        return 1;
    }
    Directory.CreateDirectory(runDirectory);
    var path = Path.Combine(runDirectory, $"replay-{index}.vpl");
    File.WriteAllText(path, target.Text);
    Console.WriteLine($"mutant {index} from {target.File}: {target.Description}");
    Console.WriteLine($"written to {path}");
    var checker = new Checker(Path.Combine(runDirectory, $"m{index}"), modes, runTimeout);
    var finding = checker.Check(target.Text, checker.Tier1, out bool compiled);
    if (finding is null && compiled && tier == 2)
        finding = checker.Check(target.Text, checker.Tier2(strict), out _, target.UnsafeCode);
    if (finding is null)
    {
        Console.WriteLine(compiled ? "no finding" : "does not compile");
        return 0;
    }
    Console.WriteLine($"FINDING {finding.Key}: {finding.Detail}");
    if (finding.Scope is { } scope)
    {
        var reduced = Reducer.Reduce(target.Text, candidate => checker.Check(candidate, scope, out _, target.UnsafeCode)?.Signature, finding.Signature, maxChecks: 120, TimeSpan.FromSeconds(90));
        var reducedPath = Path.Combine(runDirectory, $"replay-{index}-reduced.vpl");
        File.WriteAllText(reducedPath, reduced + "\n");
        Console.WriteLine($"reduced to {reducedPath}");
    }
    return 1;
}

List<Mode>? AvailableModes()
{
    var preferred = NativeCompiler.Find(Toolchain.Msvc, out _) is not null ? Toolchain.Msvc
        : NativeCompiler.Find(Toolchain.Gcc, out _) is not null ? Toolchain.Gcc
        : (Toolchain?)null;
    if (preferred is null)
    {
        Console.Error.WriteLine("tier 2 needs MSVC or g++; none was found");
        return null;
    }
    var name = preferred == Toolchain.Msvc ? "msvc" : "gcc";
    var result = new List<Mode> { new(name, preferred.Value, new CompileOptions()) };
    var other = preferred == Toolchain.Msvc ? Toolchain.Gcc : Toolchain.Msvc;
    if (NativeCompiler.Find(other, out _) is not null)
        result.Add(new(other == Toolchain.Msvc ? "msvc" : "gcc", other, new CompileOptions()));
    if (NativeCompiler.Find(Toolchain.ClangSanitized, out _) is not null)
        result.Add(new("asan", Toolchain.ClangSanitized, new CompileOptions()));
    result.Add(new(name + "-no-opt", preferred.Value, new CompileOptions(Nest: true, Optimize: false)));
    result.Add(new(name + "-no-nest", preferred.Value, new CompileOptions(Nest: false, Optimize: true)));
    return result;
}

static void WriteFinding(string directory, Finding finding, Mutant mutant, string reduced, int seed)
{
    var slug = Regex.Replace(finding.Key.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
    if (slug.Length > 60)
        slug = slug[..60].Trim('-');
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(finding.Key + "\n" + finding.Signature)))[..8].ToLowerInvariant();
    var path = Path.Combine(directory, $"{slug}-{hash}.vpl");
    var header = new StringBuilder();
    header.AppendLine("// fuzz finding: " + finding.Key);
    header.AppendLine($"// seed {seed}, mutant {mutant.Index}, mutation: {mutant.Description}");
    foreach (var line in finding.Detail.Split('\n').Take(20))
        header.AppendLine("// " + (line.Length > 300 ? line[..300] : line).TrimEnd());
    header.AppendLine("// Turn this into a test in tests/cases/ with // output:, // error:, or // panic: comments, then delete it.");
    header.AppendLine();
    File.WriteAllText(path, header + reduced.TrimEnd('\n') + "\n");
    Console.WriteLine($"        written to {path}");
}

static void TryDelete(string directory)
{
    try
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
}

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        if (Directory.Exists(Path.Combine(directory.FullName, "tests", "cases")))
            return directory.FullName;
    }
    throw new DirectoryNotFoundException("could not find tests/cases above " + AppContext.BaseDirectory);
}
