using System.Text.Json;

namespace Vpl.Dashboard;

public sealed record MutationTargetDto(string Id, string Title, string Path, string[] Filters, string? Note);

public sealed record MutationItemDto(
    int Site,
    int Line,
    int Column,
    string Kind,
    string Description,
    string Original,
    string Result,
    string? Detail);

public sealed record MutationResultDto(
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
    IReadOnlyList<MutationItemDto> Items);

public sealed record MutationDto(IReadOnlyList<MutationTargetDto> Targets, IReadOnlyList<MutationResultDto> Results);

/// <summary>
/// The mutation harness's results: the target list (tests/mutation/targets.json) and one report per
/// target that has been run (tests/mutation/results/*.json). A report is what the last run found,
/// so the page always shows the newest score for each file.
/// </summary>
public sealed class MutationService(ServerOptions options)
{
    public MutationDto Get()
    {
        var root = options.Root;
        var targets = ReadFile<List<MutationTargetDto>>(Path.Combine(root, "tests", "mutation", "targets.json")) ?? [];
        List<MutationResultDto> results = [];
        var directory = Path.Combine(root, "tests", "mutation", "results");
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                if (ReadFile<MutationResultDto>(file) is { } result)
                    results.Add(result);
            }
        }
        results.Sort((left, right) => right.StartedAt.CompareTo(left.StartedAt));
        return new(targets, results);
    }

    static T? ReadFile<T>(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonDefaults.Options) : default;
        }
        catch (JsonException)
        {
            return default;
        }
        catch (IOException)
        {
            return default;
        }
        catch (UnauthorizedAccessException)
        {
            return default;
        }
    }
}
