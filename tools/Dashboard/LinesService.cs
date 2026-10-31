namespace Wyl.Dashboard;

public sealed record LineCountDto(string Name, string? Path, int Files, int Lines, int Code);

public sealed record LineGroupDto(
    string Id,
    string Label,
    IReadOnlyList<LineCountDto> Items,
    int Files,
    int Lines,
    int Code);

public sealed record LinesDto(
    IReadOnlyList<LineGroupDto> Groups,
    int Files,
    int Lines,
    int Code);

/// <summary>Source line counts by project and by kind of code we wrote.</summary>
public sealed class LinesService
{
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".out", ".coverage", ".state", ".git", ".vs", "findings",
    };

    readonly ServerOptions options;
    readonly object gate = new();
    LinesDto? cached;
    DateTimeOffset cachedAt;

    public LinesService(ServerOptions options) => this.options = options;

    public LinesDto Get()
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (cached is not null && now - cachedAt < CacheFor)
                return cached;
            cached = Build();
            cachedAt = now;
            return cached;
        }
    }

    LinesDto Build()
    {
        var root = options.Root;
        var projects = Projects(root);
        var groups = new[]
        {
            Group("csharp", "C#", CSharpItems(root, projects)),
            Group("wyl", "Wylow", WylItems(root)),
            Group("cpp", "C++", NonEmpty([
                Count(root, "Runtime", "runtime", Files(root, "runtime", ["*.h", "*.hpp", "*.cpp"])),
            ])),
            Group("other", "Other", NonEmpty([
                Count(root, "Dashboard UI", "tools/Dashboard/wwwroot",
                    Files(root, "tools/Dashboard/wwwroot", ["*.js", "*.css", "*.html"], recursive: false)),
                Count(root, "Tool scripts", "tools", Files(root, "tools", ["*.sh", "*.ps1"], recursive: false)),
            ])),
        };
        return new(groups, groups.Sum(group => group.Files), groups.Sum(group => group.Lines), groups.Sum(group => group.Code));
    }

    static LineGroupDto Group(string id, string label, IReadOnlyList<LineCountDto> items) => new(
        id,
        label,
        items,
        items.Sum(item => item.Files),
        items.Sum(item => item.Lines),
        items.Sum(item => item.Code));

    static IReadOnlyList<LineCountDto> NonEmpty(LineCountDto[] items) =>
        items.Where(item => item.Files > 0).ToArray();

    sealed record Project(string Name, string Directory);

    static IReadOnlyList<Project> Projects(string root)
    {
        var projects = Files(root, null, ["*.csproj"])
            .Select(file => new Project(
                Path.GetFileNameWithoutExtension(file),
                Relative(root, Path.GetDirectoryName(file)!)))
            .OrderBy(project => project.Directory, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return projects;
    }

    static IReadOnlyList<LineCountDto> CSharpItems(string root, IReadOnlyList<Project> projects)
    {
        var counts = new Dictionary<string, (int Files, int Lines, int Code)>(StringComparer.OrdinalIgnoreCase);
        var loose = new List<LineCountDto>();
        foreach (var file in Files(root, null, ["*.cs"]))
        {
            var relative = Relative(root, file);
            var project = projects.FirstOrDefault(candidate =>
                relative.StartsWith(candidate.Directory + "/", StringComparison.OrdinalIgnoreCase));
            if (project is null)
            {
                if (CountFile(file) is { } single)
                    loose.Add(new(Path.GetFileName(file), Relative(root, Path.GetDirectoryName(file)!), 1, single.Lines, single.Code));
                continue;
            }

            var (files, lines, code) = counts.GetValueOrDefault(project.Directory);
            if (CountFile(file) is { } read)
                counts[project.Directory] = (files + 1, lines + read.Lines, code + read.Code);
        }

        var items = projects
            .Where(project => counts.ContainsKey(project.Directory))
            .Select(project =>
            {
                var (files, lines, code) = counts[project.Directory];
                return new LineCountDto(project.Name, project.Directory, files, lines, code);
            })
            .ToList();
        items.AddRange(loose.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase));
        return items;
    }

    static IReadOnlyList<LineCountDto> WylItems(string root) => NonEmpty([
        Count(root, "Standard library", "src/Wyl.Compiler.Semantics/Stdlib",
            Files(root, "src/Wyl.Compiler.Semantics/Stdlib", ["*.wyl"])),
        Count(root, "Test cases", "tests/cases", Files(root, "tests/cases", ["*.wyl"])),
        Count(root, "Matrix snapshots", "tests/matrix", Files(root, "tests/matrix", ["*.snapshot"])),
        Count(root, "Examples", "examples", Files(root, "examples", ["*.wyl"], recursive: false)),
    ]);

    static LineCountDto Count(string root, string name, string path, IEnumerable<string> files)
    {
        int count = 0, lines = 0, code = 0;
        foreach (var file in files)
        {
            if (CountFile(file) is not { } read)
                continue;
            count++;
            lines += read.Lines;
            code += read.Code;
        }
        return new(name, path, count, lines, code);
    }

    static IEnumerable<string> Files(string root, string? directory, string[] patterns, bool recursive = true)
    {
        var start = directory is null ? root : Path.Combine(root, directory);
        if (!Directory.Exists(start))
            yield break;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var pattern in patterns)
            foreach (var file in Directory.EnumerateFiles(start, pattern, options))
                if (!IsSkipped(root, file))
                    yield return file;
    }

    static bool IsSkipped(string root, string file) =>
        Relative(root, file).Split('/', '\\').Any(SkippedDirectories.Contains);

    static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    static (int Lines, int Code)? CountFile(string path)
    {
        string[] content;
        try
        {
            content = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        int code = 0;
        bool block = false;
        foreach (var line in content)
            if (IsCode(line, ref block))
                code++;
        return (content.Length, code);
    }

    static bool IsCode(string line, ref bool block)
    {
        bool code = false;
        for (int i = 0; i < line.Length; i++)
        {
            char current = line[i];
            if (block)
            {
                if (current == '*' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    block = false;
                    i++;
                }
                continue;
            }

            if (current == '/' && i + 1 < line.Length)
            {
                char next = line[i + 1];
                if (next == '/')
                    break;
                if (next == '*')
                {
                    block = true;
                    i++;
                    continue;
                }
            }

            if (current is '"' or '\'')
            {
                code = true;
                i = SkipQuoted(line, i);
                continue;
            }

            if (!char.IsWhiteSpace(current))
                code = true;
        }
        return code;
    }

    static int SkipQuoted(string line, int start)
    {
        char quote = line[start];
        for (int i = start + 1; i < line.Length; i++)
        {
            if (line[i] == '\\')
            {
                i++;
                continue;
            }
            if (line[i] != quote)
                continue;
            if (i + 1 < line.Length && line[i + 1] == quote)
            {
                i++;
                continue;
            }
            return i;
        }
        return line.Length;
    }
}
