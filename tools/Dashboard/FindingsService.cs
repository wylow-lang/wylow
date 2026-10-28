using System.Globalization;
using System.Text.RegularExpressions;

namespace Vpl.Dashboard;

public sealed record FindingDto(
    string Path,
    string FileName,
    string Kind,
    string? Finding,
    int? Seed,
    int? Tier,
    int? Mutant,
    string? Mutation,
    string? Summary,
    long Size,
    DateTimeOffset Modified);

public sealed class FindingsService(ServerOptions options)
{
    const long MaxRead = 2_000_000;
    static readonly Regex FindingHeader = new(@"^//\s*fuzz finding:\s*(.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex SeedHeader = new(@"^//\s*seed (\d+), mutant (\d+), mutation:\s*(.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex CommandSeed = new(@"(?:^|\s)--seed\s+(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex CommandTier = new(@"(?:^|\s)--tier\s+(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex FindingLine = new(@"^FINDING (.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    readonly string directory = Path.Combine(options.Root, "tests", "fuzz", "findings");
    readonly string root = options.Root;

    public IReadOnlyList<FindingDto> List()
    {
        if (!Directory.Exists(directory))
            return [];
        List<FindingDto> result = [];
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            });
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        foreach (var file in files
                     .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".vpl" or ".txt")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Take(500))
        {
            try
            {
                result.Add(Describe(file));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return result;
    }

    public (int Total, int Vpl, int Logs) Counts()
    {
        if (!Directory.Exists(directory))
            return (0, 0, 0);
        try
        {
            int total = 0, vpl = 0, logs = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                     }))
            {
                switch (Path.GetExtension(file).ToLowerInvariant())
                {
                    case ".vpl": vpl++; total++; break;
                    case ".txt": logs++; total++; break;
                }
            }
            return (total, vpl, logs);
        }
        catch (IOException)
        {
            return (0, 0, 0);
        }
        catch (UnauthorizedAccessException)
        {
            return (0, 0, 0);
        }
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

    FindingDto Describe(string file)
    {
        var extension = Path.GetExtension(file).ToLowerInvariant();
        var isLog = extension == ".txt";
        var head = ReadHead(file, isLog ? 400 : 40);
        string? finding = null;
        int? seed = null, tier = null, mutant = null;
        string? mutation = null;
        var findingLines = 0;

        for (int i = 0; i < head.Count; i++)
        {
            var line = head[i];
            if (finding is null)
            {
                var header = FindingHeader.Match(line);
                if (header.Success)
                    finding = header.Groups[1].Value.Trim();
            }
            var seedHeader = SeedHeader.Match(line);
            if (seedHeader.Success)
            {
                seed = Parse(seedHeader.Groups[1].Value);
                mutant = Parse(seedHeader.Groups[2].Value);
                mutation = seedHeader.Groups[3].Value.Trim();
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
            isLog ? "log" : "finding",
            finding,
            seed,
            tier,
            mutant,
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
        var allowed = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
        {
            error = "path is outside the findings directory";
            return false;
        }
        if (Path.GetExtension(full).ToLowerInvariant() is not (".vpl" or ".txt"))
        {
            error = "only findings and logs can be shown here";
            return false;
        }
        return true;
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
