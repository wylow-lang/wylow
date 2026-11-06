using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Wyl.Compiler;
using Wyl.Compiler.Interpret;
using Wyl.Generate;
using Wyl.Harness;

// Generates well-typed programs from a seed and checks each against the oracles:
//   tier 1  compile in-process, with and without --no-opt: no exception, the same errors,
//           determinism, and the optimized IR interpreted the same as the meaning
//   tier 2  build and run every mode, the interpreter first: the C++ compiles warning-free, the
//           sanitizers are quiet, and every mode prints the same output, exits the same, and
//           panics the same, a --release build among them (generated programs never overflow)
// A program that does not bind is a GENERATOR BUG, never a compiler finding.
// Usage: Wyl.Generate [--seed N] [--seconds S] [--max N] [--tier 1|2] [--jobs N] [--stage 1]
//                     [--size N] [--no-strict] [--keep] [--replay N] [--self-test]

int seed = 1;
double seconds = 120;
int max = 0;
int tier = 1;
int? jobs = null;
int stage = 1;
int size = 40;
bool strict = true;
bool keep = false;
int? replay = null;
bool selfTest = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seed" when i + 1 < args.Length: seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--seconds" when i + 1 < args.Length: seconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--max" when i + 1 < args.Length: max = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--tier" when i + 1 < args.Length: tier = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--jobs" when i + 1 < args.Length: jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--stage" when i + 1 < args.Length: stage = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--size" when i + 1 < args.Length: size = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--replay" when i + 1 < args.Length: replay = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--no-strict": strict = false; break;
        case "--keep": keep = true; break;
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
if (stage != 1)
{
    Console.Error.WriteLine("only stage 1 exists");
    return 1;
}

var options = new GenerateOptions(Stage: stage, Size: size);
int selfFailures = 0;
foreach (var (name, failure) in GeneratorSelfTests.Run(options))
{
    if (selfTest)
        Console.WriteLine(failure is null ? $"ok {name}" : $"FAIL {name}: {failure}");
    selfFailures += failure is null ? 0 : 1;
}
if (selfFailures > 0)
{
    Console.Error.WriteLine($"self-check failed ({selfFailures})");
    return 1;
}
if (selfTest)
    return 0;
Console.WriteLine("self-check ok");

var root = Repository.Root();
var runDirectory = Path.Combine(root, "tests", ".out", "generate", $"seed{seed}-{Environment.ProcessId}");
var findingsDirectory = Path.Combine(root, "tests", "generate", "findings");
Directory.CreateDirectory(findingsDirectory);

IReadOnlyList<Mode> modes = [new("interpret", null, new CompileOptions(Nest: true, Optimize: false))];
if (tier == 2)
{
    // A generated program never overflows, so a release build must print exactly what the
    // interpreter does.
    if (!Modes.TryAvailable(out var available, release: true))
    {
        Console.Error.WriteLine("tier 2 needs clang-cl or g++; none was found");
        return 1;
    }
    modes = available;
}

double runTimeout = 5;
int parallelism = jobs ?? Math.Max(1, Environment.ProcessorCount / 2);
var generator = new ProgramGenerator(options);
Console.WriteLine($"seed {seed}, tier {tier}, {seconds:F0}s, {parallelism} jobs, stage {stage}, size {size}");
if (tier == 2)
    Console.WriteLine("modes: " + string.Join(", ", modes.Select(mode => mode.Name)) + (strict ? " (strict warnings)" : ""));

var seen = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
var printLock = new object();
long checkedCount = 0, boundCount = 0, findingCount = 0, generatorBugCount = 0;
var stopwatch = Stopwatch.StartNew();
var lastReport = TimeSpan.Zero;
var budget = TimeSpan.FromSeconds(seconds);
int nextIndex = 0;

if (replay is { } replayIndex)
    return Replay(replayIndex);

while (stopwatch.Elapsed < budget && (max == 0 || nextIndex < max))
{
    // Generation comes first and in index order: a program is a pure function of the seed and its
    // index, so only checking runs in parallel.
    var batch = new List<GeneratedProgram>();
    for (int i = 0; i < 32 && (max == 0 || nextIndex < max); i++)
        batch.Add(generator.Generate(seed, nextIndex++));
    if (batch.Count == 0)
        break;

    var results = batch
        .AsParallel()
        .WithDegreeOfParallelism(parallelism)
        .Select(Work)
        .ToList();

    foreach (var (bound, findings, bugs) in results)
    {
        Interlocked.Increment(ref checkedCount);
        if (bound)
            Interlocked.Increment(ref boundCount);
        Interlocked.Add(ref findingCount, findings);
        Interlocked.Add(ref generatorBugCount, bugs);
    }
    Report();
}

Console.WriteLine($"{checkedCount} programs, {boundCount} bound, {findingCount} findings ({checkedCount / Math.Max(stopwatch.Elapsed.TotalSeconds, 1):F0}/s)");
if (generatorBugCount > 0)
    Console.WriteLine($"{generatorBugCount} generator bugs");
if (!keep)
    TryDelete(runDirectory);
return generatorBugCount > 0 ? 2 : findingCount > 0 ? 1 : 0;

(bool Bound, int Findings, int Bugs) Work(GeneratedProgram program)
{
    var directory = Path.Combine(runDirectory, $"p{program.Index}");
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, $"p{program.Index}.wyl"), program.Text);
    var checker = new Checker(directory, modes, runTimeout, "<generate>");
    var finding = checker.Check(program.Text, checker.Tier1, out bool compiled);
    // A compiler that throws leaves the program uncompiled too, and that is the compiler's finding.
    if (finding is not null)
        return (compiled, ReportFinding(program, finding, checker), 0);
    if (!compiled)
        return (false, 0, ReportGeneratorBug(program, Unbound(program)));
    if (Trapped(checker) is { } trap)
        return (true, 0, ReportGeneratorBug(program, trap));
    if (tier == 2)
        finding = checker.Check(program.Text, checker.Tier2(strict), out _);
    return (true, finding is null ? 0 : ReportFinding(program, finding, checker), 0);
}

int ReportFinding(GeneratedProgram program, Finding finding, Checker checker)
{
    if (!seen.TryAdd(finding.Key, 0))
        return 0;
    var text = program.Text;
    if (finding.Scope is { } scope)
        text = Reducer.Reduce(program.Text, candidate => checker.Check(candidate, scope, out _, false)?.Signature, finding.Signature, maxChecks: 120, TimeSpan.FromSeconds(90));
    lock (printLock)
    {
        Console.WriteLine($"FINDING {finding.Key}");
        foreach (var line in finding.Detail.Split('\n').Take(6))
            Console.WriteLine("        " + line);
    }
    Write(program, finding.Key, finding.Signature, finding.Detail, text, "generate finding");
    return 1;
}

// A program that compiled with no finding yet does not bind is well-typed only in the generator's mind.
(string Key, string Detail) Unbound(GeneratedProgram program)
{
    var errors = generator.Validate(program.Text);
    var message = errors.Count == 0 ? "no diagnostics" : errors[0].Message;
    return (
        "generator/bind/" + (message.Length > 100 ? message[..100] : message),
        errors.Count == 0
            ? "the program did not compile without diagnostics"
            : string.Join("\n", errors.Select(error => $"{error.Line}:{error.Column}: {error.Message}")));
}

// Every generated program is trap-free by construction (its integer ranges, divisors, and loop
// bounds), so a meaning that panics or never finishes means a range fact or a guard is wrong.
static (string Key, string Detail)? Trapped(Checker checker)
{
    if (checker.Meaning is not { Status: not InterpretationStatus.Completed } run)
        return null;
    var first = run.Error.Trim().Split('\n')[0].Trim();
    return ($"generator/trap/{run.Status}/{(first.Length > 100 ? first[..100] : first)}", $"the program's meaning did not complete\n{run.Error.Trim()}");
}

int ReportGeneratorBug(GeneratedProgram program, (string Key, string Detail) bug)
{
    var (key, detail) = bug;
    if (!seen.TryAdd(key, 0))
        return 0;
    lock (printLock)
    {
        Console.WriteLine($"GENERATOR BUG {key}");
        foreach (var line in detail.Split('\n').Take(6))
            Console.WriteLine("        " + line);
    }
    Write(program, key, key, detail, program.Text, "generator bug");
    return 1;
}

void Write(GeneratedProgram program, string key, string signature, string detail, string text, string kind)
{
    List<string> header =
    [
        $"// {kind}: {key}",
        $"// seed {seed}, program {program.Index}, stage {options.Stage}, size {options.Size}",
        $"// replay: Wyl.Generate --seed {seed} --replay {program.Index} --tier {tier} --size {options.Size}",
    ];
    foreach (var line in detail.Split('\n').Take(20))
        header.Add("// " + (line.Length > 300 ? line[..300] : line).TrimEnd());
    header.Add("// Turn this into a test in tests/cases/ with // output:, // error[WYLnnnn]:, or // panic: comments, then delete it.");
    var path = Findings.Write(findingsDirectory, key, signature, text, header);
    lock (printLock)
        Console.WriteLine($"        written to {path}");
}

void Report()
{
    if (stopwatch.Elapsed - lastReport < TimeSpan.FromSeconds(10))
        return;
    lastReport = stopwatch.Elapsed;
    Console.WriteLine($"{checkedCount} programs, {boundCount} bound, {findingCount} findings ({checkedCount / Math.Max(stopwatch.Elapsed.TotalSeconds, 1):F0}/s)");
}

int Replay(int index)
{
    Directory.CreateDirectory(runDirectory);
    var program = generator.Generate(seed, index);
    var path = Path.Combine(runDirectory, $"replay-{index}.wyl");
    File.WriteAllText(path, program.Text);
    Console.WriteLine($"program {index} of seed {seed}, stage {options.Stage}, size {options.Size}");
    Console.WriteLine($"written to {path}");
    var checker = new Checker(Path.Combine(runDirectory, $"p{index}"), modes, runTimeout, "<generate>");
    var finding = checker.Check(program.Text, checker.Tier1, out bool compiled);
    if (finding is null && (!compiled ? Unbound(program) : Trapped(checker)) is { } bug)
    {
        ReportGeneratorBug(program, bug);
        return 2;
    }
    if (finding is null && tier == 2)
        finding = checker.Check(program.Text, checker.Tier2(strict), out _);
    if (finding is null)
    {
        Console.WriteLine("no finding");
        return 0;
    }
    ReportFinding(program, finding, checker);
    return 1;
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
