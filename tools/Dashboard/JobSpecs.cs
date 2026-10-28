using System.Globalization;
using System.Text.RegularExpressions;

namespace Vpl.Dashboard;

public sealed record StepSpec(string FileName, IReadOnlyList<string> Arguments);

public enum ParamType { Text, Number, Bool, Select, Duration }

public sealed record ParamOption(string Value, string Label);

public sealed record ParamSpec(
    string Name,
    string Label,
    ParamType Type,
    string? Default = null,
    IReadOnlyList<ParamOption>? Options = null,
    string? Help = null,
    int? Min = null,
    int? Max = null);

public sealed record JobKindDto(
    string Kind,
    string Title,
    string Group,
    string Description,
    string? Hint,
    IReadOnlyList<ParamSpec> Parameters);

public sealed class JobInputException(string message) : Exception(message);

/// <summary>The validated parameter values a <see cref="JobSpec.Build"/> reads.</summary>
public sealed class JobValues(IReadOnlyDictionary<string, string> values)
{
    public string Text(string name) => values[name];
    public bool Bool(string name) => values[name] == "true";
    public int Int(string name) => int.Parse(values[name], CultureInfo.InvariantCulture);
}

public sealed class JobSpec
{
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public required string Group { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<ParamSpec> Parameters { get; init; }
    public required Func<JobValues, IReadOnlyList<StepSpec>> Build { get; init; }
    public Func<JobValues, bool>? Idle { get; init; }
    public Action<string, JobMetrics>? Observe { get; init; }
    /// <summary>Two running jobs with the same lock cannot run at once (the suite shares tests/.out).</summary>
    public string? Lock { get; init; }
    public string? Hint { get; init; }
    public bool AlwaysIdle { get; init; }
}

public static class JobSpecs
{
    public const string TestRunner = "tests/Vpl.Tests/bin/Debug/net10.0/Vpl.Tests.dll";
    public const string Fuzzer = "tests/Vpl.Fuzz/bin/Debug/net10.0/Vpl.Fuzz.dll";

    static readonly ParamOption[] Compilers =
    [
        new("msvc", "MSVC"),
        new("gcc", "g++"),
        new("asan", "clang-cl + ASan/UBSan"),
    ];

    static readonly ParamOption[] Tiers = [new("1", "Tier 1 — in-process"), new("2", "Tier 2 — build and run")];

    public static IReadOnlyList<JobSpec> All() =>
    [
        new()
        {
            Kind = "build",
            Title = "Build the compiler and harnesses",
            Group = "build",
            Description = "Runs dotnet build over the solution.",
            Parameters = [],
            Build = _ => [new("dotnet", ["build", "--nologo"])],
            AlwaysIdle = true,
            Hint = "A build overwrites the assemblies tests use, so it waits for running jobs.",
        },
        new()
        {
            Kind = "suite",
            Title = "Test suite",
            Group = "tests",
            Description = "Runs the test cases, interaction matrices, and in-process self-tests.",
            Parameters =
            [
                new("cc", "Compiler", ParamType.Select, "msvc", Compilers),
                new("filter", "Filter", ParamType.Text, "", Help: "A path substring, e.g. values or structs."),
                new("noOpt", "No optimizations", ParamType.Bool, "false"),
                new("noNest", "No nesting", ParamType.Bool, "false"),
                new("buildFirst", "Build first", ParamType.Bool, "true", Help: "Builds before running; waits for running jobs."),
            ],
            Build = values =>
            {
                var steps = new List<StepSpec>();
                if (values.Bool("buildFirst"))
                    steps.Add(new("dotnet", ["build", "--nologo", "-v", "minimal"]));
                var arguments = new List<string> { TestRunner, "--cc", values.Text("cc") };
                if (values.Bool("noOpt"))
                    arguments.Add("--no-opt");
                if (values.Bool("noNest"))
                    arguments.Add("--no-nest");
                if (values.Text("filter") is { Length: > 0 } filter)
                    arguments.Add(filter);
                steps.Add(new("dotnet", arguments));
                return steps;
            },
            Idle = values => values.Bool("buildFirst"),
            Observe = Observers.Suite,
            Lock = "suite",
            Hint = "Run all five modes before committing: MSVC, g++, --no-opt, --no-nest, and ASan.",
        },
        new()
        {
            Kind = "fuzz",
            Title = "Mutation fuzz",
            Group = "fuzz",
            Description = "Mutates the test corpus and checks every mutant against the oracles.",
            Parameters =
            [
                new("seed", "Seed", ParamType.Number, "1", Min: 0, Max: int.MaxValue),
                new("tier", "Tier", ParamType.Select, "1", Tiers),
                new("seconds", "Duration", ParamType.Duration, "120", Min: 1, Max: 86400, Help: "Choose seconds or minutes."),
                new("max", "Max mutants", ParamType.Number, "0", Min: 0, Max: int.MaxValue, Help: "0 means no limit."),
                new("jobs", "Jobs", ParamType.Number, "0", Min: 0, Max: 64, Help: "0 means half the CPU count."),
                new("filter", "Filter", ParamType.Text, "", Help: "Only mutate tests whose path contains this."),
                new("errors", "Include error tests", ParamType.Bool, "false"),
                new("noStrict", "No strict warnings", ParamType.Bool, "false"),
                new("keep", "Keep run directory", ParamType.Bool, "false"),
            ],
            Build = values =>
            {
                var arguments = new List<string>
                {
                    Fuzzer,
                    "--seed", values.Int("seed").ToString(CultureInfo.InvariantCulture),
                    "--tier", values.Text("tier"),
                    "--seconds", values.Int("seconds").ToString(CultureInfo.InvariantCulture),
                };
                var max = values.Int("max");
                if (max > 0)
                {
                    arguments.Add("--max");
                    arguments.Add(max.ToString(CultureInfo.InvariantCulture));
                }
                var jobs = values.Int("jobs");
                if (jobs > 0)
                {
                    arguments.Add("--jobs");
                    arguments.Add(jobs.ToString(CultureInfo.InvariantCulture));
                }
                if (values.Text("filter") is { Length: > 0 } filter)
                {
                    arguments.Add("--filter");
                    arguments.Add(filter);
                }
                if (values.Bool("errors"))
                    arguments.Add("--errors");
                if (values.Bool("noStrict"))
                    arguments.Add("--no-strict");
                if (values.Bool("keep"))
                    arguments.Add("--keep");
                return [new("dotnet", arguments)];
            },
            Observe = Observers.Fuzz,
            Hint = "Tier 2 builds and runs every mutant in five modes; keep long runs in the background. Another fuzz run can start before this one finishes.",
        },
        new()
        {
            Kind = "fuzz-replay",
            Title = "Replay a fuzz mutant",
            Group = "fuzz",
            Description = "Rebuilds and checks one mutant of a seed again.",
            Parameters =
            [
                new("seed", "Seed", ParamType.Number, "1", Min: 0, Max: int.MaxValue),
                new("index", "Mutant index", ParamType.Number, "0", Min: 0, Max: int.MaxValue),
                new("tier", "Tier", ParamType.Select, "2", Tiers),
                new("filter", "Filter", ParamType.Text, "", Help: "Must match the run that produced the finding."),
            ],
            Build = values =>
            {
                var arguments = new List<string>
                {
                    Fuzzer,
                    "--seed", values.Int("seed").ToString(CultureInfo.InvariantCulture),
                    "--tier", values.Text("tier"),
                    "--replay", values.Int("index").ToString(CultureInfo.InvariantCulture),
                };
                if (values.Text("filter") is { Length: > 0 } filter)
                {
                    arguments.Add("--filter");
                    arguments.Add(filter);
                }
                return [new("dotnet", arguments)];
            },
            Observe = Observers.Fuzz,
            Hint = "Use the seed, mutant index, and tier recorded by the finding.",
        },
        new()
        {
            Kind = "fuzz-self-test",
            Title = "Fuzz self-test",
            Group = "fuzz",
            Description = "Checks that the mutation catalog still applies and is deterministic.",
            Parameters = [],
            Build = _ => [new("dotnet", [Fuzzer, "--self-test"])],
        },
    ];

    public static Dictionary<string, string> Validate(JobSpec spec, IReadOnlyDictionary<string, string> input)
    {
        foreach (var name in input.Keys)
        {
            if (!spec.Parameters.Any(p => p.Name == name))
                throw new JobInputException($"unknown parameter '{name}'");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in spec.Parameters)
        {
            var raw = input.TryGetValue(parameter.Name, out var given) ? given : parameter.Default;
            if (raw is null)
                throw new JobInputException($"'{parameter.Name}' is required");
            raw = raw.Trim();
            if (raw.Any(char.IsControl))
                throw new JobInputException($"'{parameter.Name}' contains a control character");

            switch (parameter.Type)
            {
                case ParamType.Text:
                    if (raw.Length > 300)
                        throw new JobInputException($"'{parameter.Name}' is too long");
                    break;
                case ParamType.Number:
                case ParamType.Duration:
                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                        throw new JobInputException($"'{parameter.Name}' must be a whole number");
                    if (parameter.Min is { } min && number < min)
                        throw new JobInputException($"'{parameter.Name}' must be at least {min}");
                    if (parameter.Max is { } max && number > max)
                        throw new JobInputException($"'{parameter.Name}' must be at most {max}");
                    raw = number.ToString(CultureInfo.InvariantCulture);
                    break;
                case ParamType.Bool:
                    if (!bool.TryParse(raw, out var flag))
                        throw new JobInputException($"'{parameter.Name}' must be true or false");
                    raw = flag ? "true" : "false";
                    break;
                case ParamType.Select:
                    if (parameter.Options is not { } options || options.All(o => o.Value != raw))
                    {
                        var allowed = parameter.Options is { } list ? string.Join(", ", list.Select(o => o.Value)) : "nothing";
                        throw new JobInputException($"'{parameter.Name}' must be one of: {allowed}");
                    }
                    break;
            }
            result[parameter.Name] = raw;
        }
        return result;
    }

    public static string FormatCommand(StepSpec step) =>
        string.Join(' ', new[] { step.FileName }.Concat(step.Arguments.Select(Quote)));

    static string Quote(string value) =>
        value.Length > 0 && value.All(c => !char.IsWhiteSpace(c) && c != '"')
            ? value
            : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}

static class Observers
{
    static readonly Regex SuiteSummary = new(@"^(\d+) passed, (\d+) failed \(([\d.,]+)s\)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex SuiteFailure = new(@"^FAIL (.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex FuzzSummary = new(@"^(\d+) mutants, (\d+) compiled, (\d+) findings \((.*)\)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex FuzzFinding = new(@"^FINDING (.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void Suite(string line, JobMetrics metrics)
    {
        var summary = SuiteSummary.Match(line);
        if (summary.Success)
        {
            metrics.Passed = int.Parse(summary.Groups[1].Value, CultureInfo.InvariantCulture);
            metrics.Failed = int.Parse(summary.Groups[2].Value, CultureInfo.InvariantCulture);
            metrics.Seconds = double.Parse(summary.Groups[3].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        }
        var failure = SuiteFailure.Match(line);
        if (failure.Success)
        {
            metrics.Failures.Add(failure.Groups[1].Value.Trim());
            if (metrics.Failures.Count > 200)
                metrics.Failures.RemoveAt(0);
        }
    }

    public static void Fuzz(string line, JobMetrics metrics)
    {
        var summary = FuzzSummary.Match(line);
        if (summary.Success)
        {
            metrics.Mutants = int.Parse(summary.Groups[1].Value, CultureInfo.InvariantCulture);
            metrics.Compiled = int.Parse(summary.Groups[2].Value, CultureInfo.InvariantCulture);
            metrics.Findings = int.Parse(summary.Groups[3].Value, CultureInfo.InvariantCulture);
            metrics.Rate = summary.Groups[4].Value;
        }
        var finding = FuzzFinding.Match(line);
        if (finding.Success)
        {
            metrics.FindingKeys.Add(finding.Groups[1].Value.Trim());
            if (metrics.FindingKeys.Count > 50)
                metrics.FindingKeys.RemoveAt(0);
        }
    }
}
