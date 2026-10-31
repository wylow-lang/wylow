using System.Text.RegularExpressions;
using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Fuzz;

sealed record BaseFile(string Name, string Text);

/// <summary>
/// Makes mutants deterministically for a seed: each call picks a base test, a mutation, and a site
/// from it, sometimes two of them, and splices the replacement text in. The same seed and corpus give
/// the same sequence, so a finding's mutant can be replayed.
/// </summary>
sealed partial class Mutator
{
    const int AttemptsPerMutation = 60;

    readonly IReadOnlyList<BaseFile> files;
    readonly Random random;
    readonly Dictionary<string, AstIndex> baseIndexes = new(StringComparer.Ordinal);
    int next;

    public Mutator(IReadOnlyList<BaseFile> files, int seed)
    {
        this.files = files;
        random = new Random(seed);
    }

    public int Count => next;

    public Mutant Next()
    {
        int index = next++;
        var file = files[random.Next(files.Count)];
        var text = file.Text;
        var names = new List<string>();
        bool unsafeCode = false;
        int mutationCount = random.Next(2) == 0 ? 1 : 2;
        for (int i = 0; i < mutationCount; i++)
        {
            if (TryMutate(text, file, isBase: i == 0, out var description, out var mutated, out bool unsafeSite) is false)
                break;
            names.Add(description);
            text = mutated;
            unsafeCode |= unsafeSite;
        }
        return new Mutant(index, file.Name, names.Count == 0 ? "none" : string.Join(" + ", names), text, unsafeCode);
    }

    /// <summary>The first call is on the unmodified base file, whose index is cached; later ones re-parse.</summary>
    bool TryMutate(string text, BaseFile file, bool isBase, out string description, out string mutated, out bool unsafeSite)
    {
        var index = isBase
            ? baseIndexes.TryGetValue(file.Name, out var cached) ? cached : baseIndexes[file.Name] = Index(text)
            : Index(text);
        for (int attempt = 0; attempt < AttemptsPerMutation; attempt++)
        {
            var mutation = Mutations.All[random.Next(Mutations.All.Length)];
            var sites = index.Of(mutation.Kind);
            if (sites.Count == 0)
                continue;
            var site = sites[random.Next(sites.Count)];
            if (!mutation.Applies(site))
                continue;
            var candidate = mutation.Build(new MutationContext(text, random), site);
            if (candidate is null)
                continue;
            var applied = Mutations.Apply(text, candidate.Edits);
            if (applied == text)
                continue;
            description = candidate.Name;
            mutated = applied;
            unsafeSite = site.Unsafe;
            return true;
        }
        description = "";
        mutated = text;
        unsafeSite = false;
        return false;
    }

    static AstIndex Index(string text)
    {
        var source = new SourceText("<fuzz>", text);
        var diagnostics = new DiagnosticBag();
        return AstIndex.Of(new Parser(source, diagnostics).ParseCompilationUnit());
    }

    /// <summary>The tests a run starts from, in a stable order, skipping error tests unless asked.</summary>
    public static List<BaseFile> Load(string casesDirectory, string? filter, bool includeErrors)
    {
        var files = new List<BaseFile>();
        foreach (var path in Directory.GetFiles(casesDirectory, "*.wyl", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var name = Path.GetRelativePath(casesDirectory, path).Replace('\\', '/');
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var text = File.ReadAllText(path);
            // An error test already has an error, so mutating it mostly reproduces that error.
            if (!includeErrors && ErrorDirective().IsMatch(text))
                continue;
            files.Add(new BaseFile(name, text));
        }
        return files;
    }

    [GeneratedRegex(@"//\s*error:")]
    private static partial Regex ErrorDirective();
}
