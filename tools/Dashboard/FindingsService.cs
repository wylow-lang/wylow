using System.Globalization;
using System.Text.RegularExpressions;

namespace Wyl.Dashboard;

public sealed record FindingDto(
    string Path,
    string FileName,
    string Kind,
    string Source,
    string? Finding,
    int? Seed,
    int? Tier,
    int? Mutant,
    int? ProgramSize,
    string? Mutation,
    string? Summary,
    long Size,
    DateTimeOffset Modified);

/// <summary>The findings the fuzzer and the generator write, read from both directories.</summary>
public sealed class FindingsService(ServerOptions options)
{
    const long MaxRead = 2_000_000;
    static readonly Regex FuzzHeader = new(@"^//\s*fuzz finding:\s*(.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex GenerateHeader = new(@"^//\s*(?:generate finding|generator bug):\s*(.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex GeneratorBugHeader = new(@"^//\s*generator bug:", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex FuzzSeedHeader = new(@"^//\s*seed (\d+), mutant (\d+), mutation:\s*(.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex GenerateSeedHeader = new(@"^//\s*seed (\d+), program (\d+), stage (\d+), size (\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex CommandSeed = new(@"(?:^|\s)--seed\s+(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex CommandTier = new(@"(?:^|\s)--tier\s+(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex FindingLine = new(@"^FINDING (.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    readonly (string Source, string Directory)[] sources =
    [
        ("fuzz", Path.Combine(options.Root, "tests", "fuzz", "findings")),
        ("generate", Path.Combine(options.Root, "tests", "generate", "findings")),
    ];
    readonly string root = options.Root;

    public IReadOnlyList<FindingDto> List()
    {
        List<FindingDto> result = [];
        foreach (var (source, directory) in sources)
        {
            foreach (var file in Files(directory))
            {
                try
                {
                    result.Add(Describe(file, source));
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        return [.. result.OrderByDescending(finding => finding.Modified).Take(500)];
    }

    public (int Total, int Wyl, int Logs) Counts()
    {
        int total = 0, wyl = 0, logs = 0;
        foreach (var (_, directory) in sources)
        {
            foreach (var file in Files(directory))
            {
                switch (Path.GetExtension(file).ToLowerInvariant())
                {
                    case ".wyl": wyl++; total++; break;
                    case ".txt": logs++; total++; break;
                }
            }
        }
        return (total, wyl, logs);
    }

    public (string? Text, string? Error) Read(string relative)
    {
        if (!TryResolve(relative, out var full, out var error))
            return (null, error);
        if (!File.Exists(full))
            return (null, "file not found");
        if (new FileInfo(full).Length > MaxRead)
            return (null, "file is too large to show");
        try
        {
            return (File.ReadAllText(full), null);
        }
        catch (IOException ex)
        {
            return (null, ex.Message);
        }
    }

    FindingDto Describe(string file, string source)
    {
        var extension = Path.GetExtension(file).ToLowerInvariant();
        var isLog = extension == ".txt";
        var head = ReadHead(file, isLog ? 400 : 40);
        string? finding = null;
        var kind = isLog ? "log" : "finding";
        int? seed = null, tier = null, mutant = null, programSize = null;
        string? mutation = null;
        var findingLines = 0;

        for (int i = 0; i < head.Count; i++)
        {
            var line = head[i];
            if (finding is null)
            {
                var fuzz = FuzzHeader.Match(line);
                var generate = GenerateHeader.Match(line);
                if (fuzz.Success)
                    finding = fuzz.Groups[1].Value.Trim();
                else if (generate.Success)
                {
                    finding = generate.Groups[1].Value.Trim();
                    if (!isLog && GeneratorBugHeader.IsMatch(line))
                        kind = "generator bug";
                }
            }
            var fuzzSeed = FuzzSeedHeader.Match(line);
            if (fuzzSeed.Success)
            {
                seed = Parse(fuzzSeed.Groups[1].Value);
                mutant = Parse(fuzzSeed.Groups[2].Value);
                mutation = fuzzSeed.Groups[3].Value.Trim();
            }
            var generateSeed = GenerateSeedHeader.Match(line);
            if (generateSeed.Success)
            {
                seed = Parse(generateSeed.Groups[1].Value);
                mutant = Parse(generateSeed.Groups[2].Value);
                programSize = Parse(generateSeed.Groups[4].Value);
            }
            // A generate finding's header names the tier that produced it, after --replay.
            if (source == "generate" && line.StartsWith("// replay:", StringComparison.Ordinal))
            {
                var replayTier = CommandTier.Match(line);
                if (replayTier.Success)
                    tier = Parse(replayTier.Groups[1].Value);
            }
            if (isLog)
            {
                if (i == 0)
                {
                    var commandSeed = CommandSeed.Match(line);
                    if (commandSeed.Success)
                        seed = Parse(commandSeed.Groups[1].Value);
                    var commandTier = CommandTier.Match(line);
                    if (commandTier.Success)
                        tier = Parse(commandTier.Groups[1].Value);
                }
                var findingLine = FindingLine.Match(line);
                if (findingLine.Success)
                {
                    finding ??= findingLine.Groups[1].Value.Trim();
                    findingLines++;
                }
            }
        }

        return new FindingDto(
            Path.GetRelativePath(root, file).Replace('\\', '/'),
            Path.GetFileName(file),
            kind,
            source,
            finding,
            seed,
            tier,
            mutant,
            programSize,
            mutation,
            isLog ? $"{findingLines} finding{(findingLines == 1 ? "" : "s")} in the log" : null,
            new FileInfo(file).Length,
            new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero).ToLocalTime());
    }

    bool TryResolve(string relative, out string full, out string? error)
    {
        full = "";
        error = null;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            error = "invalid path";
            return false;
        }
        full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var target = full;
        if (!sources.Any(source =>
                target.StartsWith(Path.GetFullPath(source.Directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            error = "path is outside the findings directories";
            return false;
        }
        if (Path.GetExtension(full).ToLowerInvariant() is not (".wyl" or ".txt"))
        {
            error = "only findings and logs can be shown here";
            return false;
        }
        return true;
    }

    static List<string> Files(string directory)
    {
        if (!Directory.Exists(directory))
            return [];
        try
        {
            return [.. Directory.EnumerateFiles(directory, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                })
                .Where(file => Path.GetExtension(file).ToLowerInvariant() is ".wyl" or ".txt")
                .OrderByDescending(File.GetLastWriteTimeUtc)];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    static List<string> ReadHead(string file, int count)
    {
        var lines = new List<string>();
        using var reader = new StreamReader(file);
        while (lines.Count < count && reader.ReadLine() is { } line)
            lines.Add(line);
        return lines;
    }

    static int? Parse(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
}
