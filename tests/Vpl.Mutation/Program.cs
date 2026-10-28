using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Vpl.Mutation;

// Mutation testing of the compiler: change one operator,
// constant, pattern, condition, or statement at a time, rebuild the file's project into a private
// copy, and rerun the covering tests. A mutant no test catches is a **survivor**: a missing test or
// a line nothing needs. The results are written to tests/mutation/results for the dashboard.
// Usage: Vpl.Mutation --target <id> | --file <path> [--filter <text>]... [--max N] [--seconds S]
//                     [--seed N] [--timeout S] [--list] [--list-targets] [--build]
// The target list and its default test filters are tests/mutation/targets.json.

string? targetId = null;
string? file = null;
List<string> filters = [];
int max = 0;
double seconds = 0;
int seed = 0;
double timeout = 600;
int? only = null;
bool list = false;
bool listTargets = false;
bool build = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--target" when i + 1 < args.Length: targetId = args[++i]; break;
        case "--file" when i + 1 < args.Length: file = args[++i]; break;
        case "--filter" when i + 1 < args.Length: filters.Add(args[++i]); break;
        case "--max" when i + 1 < args.Length: max = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--seconds" when i + 1 < args.Length: seconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--seed" when i + 1 < args.Length: seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--timeout" when i + 1 < args.Length: timeout = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--only" when i + 1 < args.Length: only = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--list": list = true; break;
        case "--list-targets": listTargets = true; break;
        case "--build": build = true; break;
        default:
            Console.Error.WriteLine($"unknown option '{args[i]}'");
            return 1;
    }
}

var root = FindRepositoryRoot();
var targets = Targets.Load(root);

if (listTargets)
{
    foreach (var candidate in targets)
        Console.WriteLine($"{candidate.Id,-14} {candidate.Path,-48} {candidate.Title}  filters: {string.Join(", ", candidate.Filters)}");
    return 0;
}

if ((targetId is null) == (file is null))
{
    Console.Error.WriteLine("give exactly one of --target and --file");
    return 1;
}

MutationTarget resolved;
string sourceFile;
string projectPath;
if (targetId is not null)
{
    resolved = targets.SingleOrDefault(candidate => candidate.Id == targetId)
        ?? throw new ArgumentException($"unknown target '{targetId}' (try --list-targets)");
    sourceFile = Path.Combine(root, resolved.Path.Replace('/', Path.DirectorySeparatorChar));
    projectPath = FindProject(sourceFile);
    if (filters.Count == 0)
        filters = [.. resolved.Filters];
}
else
{
    sourceFile = Path.GetFullPath(Path.Combine(root, file!.Replace('/', Path.DirectorySeparatorChar)));
    if (!File.Exists(sourceFile))
        throw new ArgumentException($"no file at {file}");
    projectPath = FindProject(sourceFile);
    // The prefix keeps a one-off run from overwriting a named target's report on a case-insensitive
    // file system, where Conversions.json and conversions.json are the same file.
    var id = "file-" + Path.GetFileNameWithoutExtension(sourceFile);
    resolved = new MutationTarget(id, id, Path.GetRelativePath(root, sourceFile).Replace('\\', '/'), [], null);
}
if (filters.Count == 0)
    filters = [""];

var source = File.ReadAllText(sourceFile);
var sites = SiteFinder.Find(source).ToList();
if (seed != 0)
{
    var random = new Random(seed);
    for (int i = sites.Count - 1; i > 0; i--)
    {
        int other = random.Next(i + 1);
        (sites[i], sites[other]) = (sites[other], sites[i]);
    }
}

var displayPath = Path.GetRelativePath(root, sourceFile).Replace('\\', '/');
var displayProject = Path.GetRelativePath(root, projectPath).Replace('\\', '/');
var filterText = string.Join(", ", filters.Select(f => f.Length == 0 ? "(all)" : f));

Console.WriteLine($"target: {resolved.Id} - {displayPath}");
Console.WriteLine($"project: {displayProject}");
Console.WriteLine($"filters: {filterText}");
Console.WriteLine($"sites: {sites.Count}" + (seed != 0 ? $" (shuffled with seed {seed})" : ""));

if (list)
{
    foreach (var site in sites)
        Console.WriteLine($"{displayPath}:{site.Line}:{site.Column}  {site.Kind,-9} {site.Description}  `{site.Original}`");
    return 0;
}
if (sites.Count == 0)
{
    Console.Error.WriteLine("no mutation sites");
    return 1;
}

var started = DateTimeOffset.Now;
var runner = new MutationRunner(root, projectPath, sourceFile, filters, TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(timeout));
int killed = 0, survived = 0, invalid = 0, timedOut = 0, ran = 0;
List<MutationItem> items = [];
// --only retests one site (the index --list prints) with another filter, which is how a survivor
// is triaged against a broader set of tests.
var chosen = only is { } single
    ? (single >= 1 && single <= sites.Count
        ? new[] { single - 1 }
        : throw new ArgumentException($"--only must be from 1 to {sites.Count}"))
    : Enumerable.Range(0, max > 0 ? Math.Min(max, sites.Count) : sites.Count).ToArray();
var total = chosen.Length;
var stopwatch = Stopwatch.StartNew();
var lastProgress = TimeSpan.Zero;

// Recorded after every mutant, so a run the budget or a cancellation stops still shows the score of
// what it saw. A triage run is not the target's score.
void Save()
{
    if (only is not null)
        return;
    var directory = Path.Combine(root, "tests", "mutation", "results");
    Directory.CreateDirectory(directory);
    var report = new MutationReport(resolved.Id, displayPath, displayProject, [.. filters], started,
        stopwatch.Elapsed.TotalSeconds, sites.Count, ran < sites.Count, ran, killed, survived, invalid, timedOut,
        Score(killed, survived, timedOut), [.. items]);
    File.WriteAllText(Path.Combine(directory, resolved.Id + ".json"), JsonSerializer.Serialize(report, Targets.JsonOptions));
}

try
{
    runner.Prepare(build);
    runner.Baseline();

    for (int index = 0; index < total; index++)
    {
        if (seconds > 0 && stopwatch.Elapsed >= TimeSpan.FromSeconds(seconds))
            break;
        var site = sites[chosen[index]];
        var mutated = source[..site.Position] + site.Replacement + source[(site.Position + site.Length)..];
        var outcome = runner.RunMutant(mutated);
        ran++;
        switch (outcome.State)
        {
            case MutantState.Killed: killed++; break;
            case MutantState.Survived: survived++; break;
            case MutantState.Invalid: invalid++; break;
            case MutantState.TimedOut: timedOut++; break;
        }

        var state = outcome.State switch
        {
            MutantState.Killed => "killed",
            MutantState.Survived => "SURVIVED",
            MutantState.Invalid => "invalid",
            _ => "timeout",
        };
        Console.WriteLine($"[{index + 1}/{total}] site {chosen[index] + 1} {state,-8} {displayPath}:{site.Line}:{site.Column} {site.Description} ({outcome.Seconds:F1}s)");
        if (outcome.State == MutantState.Invalid)
            Console.WriteLine($"         {outcome.Detail}");
        else if (outcome.State == MutantState.Killed)
            Console.WriteLine($"         {outcome.Detail}");

        items.Add(new MutationItem(chosen[index] + 1, site.Line, site.Column, site.Kind, site.Description, site.Original,
            state.ToLowerInvariant(), outcome.Detail.Length > 300 ? outcome.Detail[..300] : outcome.Detail));
        Save();

        if (stopwatch.Elapsed - lastProgress >= TimeSpan.FromSeconds(15) && index + 1 < total)
        {
            lastProgress = stopwatch.Elapsed;
            Console.WriteLine($"progress {index + 1}/{total}: {killed} killed, {survived} survived, {invalid} invalid" +
                (timedOut > 0 ? $", {timedOut} timeout" : "") + $" - {Score(killed, survived, timedOut):F2}% mutation score");
        }
    }
}
finally
{
    runner.Restore();
}

var elapsed = stopwatch.Elapsed.TotalSeconds;
var score = Score(killed, survived, timedOut);
if (ran < sites.Count)
    Console.WriteLine($"sampled: {ran} of {sites.Count} sites");
Console.WriteLine($"{ran} mutants: {killed} killed, {survived} survived, {invalid} invalid" +
    (timedOut > 0 ? $", {timedOut} timeout" : "") + $" - {score:F2}% mutation score ({elapsed:F1}s)");
if (survived > 0)
{
    Console.WriteLine("survivors:");
    foreach (var item in items.Where(item => item.Result == "survived"))
        Console.WriteLine($"  site {item.Site}: {displayPath}:{item.Line}:{item.Column} {item.Description}  `{item.Original}`");
}

if (only is not null)
{
    Console.WriteLine("triage run: not recorded as the target's score");
    return survived > 0 ? 1 : 0;
}

Save();
var reportPath = Path.Combine(root, "tests", "mutation", "results", resolved.Id + ".json");
Console.WriteLine($"results: {Path.GetRelativePath(root, reportPath).Replace('\\', '/')}");
return survived > 0 ? 1 : 0;

static double Score(int killed, int survived, int timedOut) =>
    killed + survived + timedOut == 0 ? 0 : 100.0 * (killed + timedOut) / (killed + survived + timedOut);

static string FindProject(string file)
{
    for (var directory = new DirectoryInfo(Path.GetDirectoryName(file)!); directory is not null; directory = directory.Parent)
    {
        var projects = Directory.GetFiles(directory.FullName, "*.csproj");
        if (projects.Length == 1)
            return projects[0];
        if (projects.Length > 1)
            throw new InvalidOperationException($"{file} is under a directory with more than one project");
    }
    throw new InvalidOperationException($"could not find the project of {file}");
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

sealed record MutationItem(int Site, int Line, int Column, string Kind, string Description, string Original, string Result, string Detail);

sealed record MutationReport(
    string Target,
    string Path,
    string Project,
    string[] Filters,
    DateTimeOffset StartedAt,
    double Seconds,
    int Sites,
    bool Partial,
    int Mutants,
    int Killed,
    int Survived,
    int Invalid,
    int TimedOut,
    double Score,
    IReadOnlyList<MutationItem> Items);
