// Summarizes Cobertura coverage files of the test suite by compiler layer and file, and lists
// the sites no test reached: user errors (Error, Violation), the verifier's own checks, and
// InvalidOperationExceptions that should be unreachable. Several files are merged (one per mode),
// so a line counts as reached when any mode reached it.
// Usage: dotnet run tools/coverage-report.cs -- [--uncovered] [--partial] <coverage.xml>... <repository root>

using System.Text.RegularExpressions;
using System.Xml.Linq;

var inputs = args.Where(a => a.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList();
var root = Path.GetFullPath(args.Single(a => !a.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !a.StartsWith("--", StringComparison.Ordinal)));
var src = Path.Combine(root, "src");
bool listUncovered = args.Contains("--uncovered"), listPartial = args.Contains("--partial");

// file -> line -> (hits, covered branches, branches, method)
var files = new Dictionary<string, Dictionary<int, LineCoverage>>(StringComparer.OrdinalIgnoreCase);
foreach (var @class in inputs.SelectMany(input => XDocument.Load(input).Descendants("class")))
{
    var file = Path.GetFullPath((string)@class.Attribute("filename")!);
    if (!file.StartsWith(src, StringComparison.OrdinalIgnoreCase) || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
        continue;
    var lines = files.TryGetValue(file, out var found) ? found : files[file] = [];
    foreach (var method in @class.Descendants("method"))
    {
        var name = (string)method.Attribute("name")!;
        foreach (var line in method.Descendants("line"))
        {
            int number = (int)line.Attribute("number")!;
            int hits = (int)line.Attribute("hits")!;
            var (covered, total) = Branches((string?)line.Attribute("condition-coverage"));
            var entry = new LineCoverage(hits, covered, total, name);
            lines[number] = lines.TryGetValue(number, out var old) ? old.Merge(entry) : entry;
        }
    }
}

var byLayer = files.GroupBy(f => Layer(f.Key)).OrderBy(g => g.Key);
Console.WriteLine("Coverage by layer (lines, branches)");
foreach (var layer in byLayer)
    Console.WriteLine($"  {layer.Key,-12} {Rates(layer.SelectMany(f => f.Value.Values))}");
Console.WriteLine($"  {"all",-12} {Rates(files.Values.SelectMany(f => f.Values))}");

Console.WriteLine();
Console.WriteLine("Coverage by file, least covered first (lines, branches)");
foreach (var (file, lines) in files.OrderBy(f => LineRate(f.Value.Values)).ThenBy(f => f.Key))
    Console.WriteLine($"  {Path.GetRelativePath(src, file).Replace('\\', '/'),-58} {Rates(lines.Values)}");

if (listUncovered)
    Lines("Lines no test ran, by file", entry => entry.Hits == 0, entry => "");
if (listPartial)
    Lines("Lines whose branch didn't all run, by file", entry => entry.Hits > 0 && entry.CoveredBranches < entry.Branches,
        entry => $"  ({entry.CoveredBranches}/{entry.Branches} branches)");

void Lines(string title, Func<LineCoverage, bool> predicate, Func<LineCoverage, string> note)
{
    Console.WriteLine();
    Console.WriteLine(title);
    foreach (var (file, lines) in files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
    {
        var listed = lines.Where(l => predicate(l.Value)).OrderBy(l => l.Key).ToList();
        if (listed.Count == 0)
            continue;
        Console.WriteLine($"  {Path.GetRelativePath(src, file).Replace('\\', '/')} ({listed.Count})");
        var text = File.ReadAllLines(file);
        foreach (var (number, entry) in listed)
        {
            var line = number <= text.Length ? text[number - 1].Trim() : "";
            Console.WriteLine($"    {number}: {(line.Length > 110 ? line[..110] + "..." : line)}{note(entry)}");
        }
    }
}

var sites = files.SelectMany(f => Sites(f.Key, f.Value)).ToList();
Console.WriteLine();
Console.WriteLine("Sites (reached / all; 'partial' means the line ran but some branch on it didn't, so the site may not have)");
foreach (var kind in Enum.GetValues<SiteKind>())
{
    var ofKind = sites.Where(s => s.Kind == kind).ToList();
    Console.WriteLine($"  {kind,-12} {ofKind.Count(s => s.State == SiteState.Reached)} / {ofKind.Count}, " +
        $"{ofKind.Count(s => s.State == SiteState.Partial)} partial, {ofKind.Count(s => s.State == SiteState.Unmapped)} not in the coverage data");
}
// A reached Unreachable site needs no list: the runner fails a test that throws, so in a passing
// suite only the self-tests that throw on purpose reach one.
foreach (var kind in Enum.GetValues<SiteKind>())
{
    Console.WriteLine();
    Console.WriteLine($"Unreached {kind} sites");
    foreach (var site in sites.Where(s => s.Kind == kind && s.State != SiteState.Reached).OrderBy(s => s.File).ThenBy(s => s.Line))
        Console.WriteLine($"  {(site.State == SiteState.Unreached ? "" : site.State.ToString().ToLowerInvariant() + " ")}{Path.GetRelativePath(root, site.File).Replace('\\', '/')}:{site.Line}: {site.Text}");
}

static IEnumerable<Site> Sites(string file, Dictionary<int, LineCoverage> lines)
{
    var text = File.ReadAllLines(file);
    bool verifier = Path.GetFileName(file).StartsWith("IrVerifier", StringComparison.Ordinal);
    for (int i = 0; i < text.Length; i++)
    {
        var line = text[i];
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || Definition().IsMatch(line))
            continue;
        SiteKind? kind =
            line.Contains("throw new InvalidOperationException", StringComparison.Ordinal) ? SiteKind.Unreachable
            : Violation().IsMatch(line) ? SiteKind.Error
            : ErrorCall().IsMatch(line) ? (verifier ? SiteKind.Check : SiteKind.Error)
            : null;
        if (kind is null)
            continue;
        // A site inside the reporting helpers themselves is not a site.
        if (lines.TryGetValue(i + 1, out var own) && Helper().IsMatch(own.Method))
            continue;
        yield return new Site(kind.Value, file, i + 1, trimmed.Length > 110 ? trimmed[..110] + "..." : trimmed, State(lines, i + 1));
    }
}

// A statement spread over lines is mapped to its first line, so a site without an entry takes the
// nearest earlier line that has one.
static SiteState State(Dictionary<int, LineCoverage> lines, int line)
{
    for (int at = line; at > line - 4 && at > 0; at--)
    {
        if (lines.TryGetValue(at, out var entry))
            return entry.Hits == 0 ? SiteState.Unreached : entry.CoveredBranches < entry.Branches ? SiteState.Partial : SiteState.Reached;
    }
    return SiteState.Unmapped;
}

static string Layer(string file)
{
    var project = file.Split(Path.DirectorySeparatorChar).First(p => p.StartsWith("Vpl.", StringComparison.Ordinal));
    return project == "Vpl.Compiler" ? "Driver" : project["Vpl.Compiler.".Length..];
}

static (int Covered, int Total) Branches(string? conditionCoverage)
{
    if (conditionCoverage is null)
        return (0, 0);
    var match = Regex.Match(conditionCoverage, @"\((\d+)/(\d+)\)");
    return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
}

static double LineRate(IEnumerable<LineCoverage> lines)
{
    var list = lines.ToList();
    return list.Count == 0 ? 1 : (double)list.Count(l => l.Hits > 0) / list.Count;
}

static string Rates(IEnumerable<LineCoverage> lines)
{
    var list = lines.ToList();
    int covered = list.Count(l => l.Hits > 0), branches = list.Sum(l => l.Branches), coveredBranches = list.Sum(l => l.CoveredBranches);
    string Percent(int part, int all) => all == 0 ? "   -  " : $"{100.0 * part / all,5:F1}%";
    return $"{Percent(covered, list.Count)} of {list.Count,5}   {Percent(coveredBranches, branches)} of {branches,5}   ({list.Count - covered} lines uncovered)";
}

enum SiteKind { Error, Check, Unreachable }
enum SiteState { Reached, Partial, Unreached, Unmapped }

sealed record Site(SiteKind Kind, string File, int Line, string Text, SiteState State);

sealed record LineCoverage(int Hits, int CoveredBranches, int Branches, string Method)
{
    public LineCoverage Merge(LineCoverage other) => new(
        Math.Max(Hits, other.Hits), Math.Max(CoveredBranches, other.CoveredBranches), Math.Max(Branches, other.Branches),
        Hits >= other.Hits ? Method : other.Method);
}

static partial class Program
{
    [GeneratedRegex(@"\b(void|bool|BoundReturn)\s+\w+\(")]
    private static partial Regex Definition();
    [GeneratedRegex(@"\bViolation\(")]
    private static partial Regex Violation();
    [GeneratedRegex(@"(?<![\w.])Error\(|diagnostics\.Error\(")]
    private static partial Regex ErrorCall();
    [GeneratedRegex(@"^(Error|Violation|Report)$")]
    private static partial Regex Helper();
}
