using System.Text.Json;

namespace Vpl.Mutation;

/// <summary>A file the harness mutates, with the tests that cover its feature.</summary>
/// <param name="Arguments">What the test runner is given before the filter, such as <c>--interpret</c>.</param>
/// <param name="Equivalent">Sites whose mutation no program can observe, with the reason.</param>
public sealed record MutationTarget(string Id, string Title, string Path, string[] Filters, string? Note,
    string[]? Arguments = null, EquivalentSpec[]? Equivalent = null);

/// <summary>
/// A site no test can kill because the change cannot change what a program observes. A site is
/// named by what it changes and the statement it is in, not by its line, so unrelated lines moving
/// leave the record valid; the harness refuses an entry that stops matching exactly one site.
/// </summary>
public sealed record EquivalentSpec(string Kind, string Original, string Replacement, string Context, string Reason);

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
