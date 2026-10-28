using System.Text.Json;

namespace Vpl.Mutation;

/// <summary>A file the harness mutates, with the tests that cover its feature.</summary>
public sealed record MutationTarget(string Id, string Title, string Path, string[] Filters, string? Note);

public static class Targets
{
    public const string RelativePath = "tests/mutation/targets.json";

    public static IReadOnlyList<MutationTarget> Load(string root)
    {
        var path = System.IO.Path.Combine(root, "tests", "mutation", "targets.json");
        if (!File.Exists(path))
            throw new InvalidOperationException($"no target list at {RelativePath}");
        var targets = JsonSerializer.Deserialize<MutationTarget[]>(File.ReadAllText(path), JsonOptions) ?? [];
        if (targets.Length == 0)
            throw new InvalidOperationException("the target list is empty");
        return targets;
    }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
