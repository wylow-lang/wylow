using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Fuzz;

/// <summary>
/// Checks that the catalog still builds mutants: every mutation applies somewhere in the corpus,
/// applying its edits changes the text, and the same seed makes the same mutants twice. Without
/// this, a broken mutation would leave the fuzzer finding nothing and passing forever.
/// </summary>
public static class FuzzSelfTests
{
    public static IReadOnlyList<(string Name, string? Failure)> Run(string casesDirectory)
    {
        try
        {
            var files = Mutator.Load(casesDirectory, filter: null, includeErrors: false);
            if (files.Count == 0)
                return [("catalog", "the corpus is empty")];
            return [("catalog", Catalog(files)), ("determinism", Determinism(files)), ("unsafe-sites", UnsafeSites())];
        }
        catch (Exception exception)
        {
            return [("self-test", exception.ToString())];
        }
    }

    static string? Catalog(IReadOnlyList<BaseFile> files)
    {
        var random = new Random(3);
        var found = Mutations.All.ToDictionary(mutation => mutation.Name, _ => false, StringComparer.Ordinal);
        foreach (var file in files)
        {
            var index = AstIndex.Of(new Parser(new SourceText("<fuzz>", file.Text), new DiagnosticBag()).ParseCompilationUnit());
            foreach (var mutation in Mutations.All.Where(m => !found[m.Name]))
            {
                foreach (var site in index.Of(mutation.Kind))
                {
                    if (!mutation.Applies(site))
                        continue;
                    if (mutation.Build(new MutationContext(file.Text, random), site) is not { } candidate)
                        continue;
                    if (Mutations.Apply(file.Text, candidate.Edits) == file.Text)
                        continue;
                    found[mutation.Name] = true;
                    break;
                }
            }
            if (found.Values.All(value => value))
                break;
        }
        var missing = found.Where(pair => !pair.Value).Select(pair => $"'{pair.Key}'").ToList();
        return missing.Count == 0 ? null : "no mutation could be made by " + string.Join(", ", missing);
    }

    static string? Determinism(IReadOnlyList<BaseFile> files)
    {
        var first = new Mutator(files, seed: 7);
        var second = new Mutator(files, seed: 7);
        int made = 0;
        for (int i = 0; i < 200; i++)
        {
            var a = first.Next();
            var b = second.Next();
            if (a.File != b.File || a.Description != b.Description || a.Text != b.Text)
                return $"mutant {i} differs between two runs of seed 7";
            if (a.Description != "none")
                made++;
        }
        return made < 100 ? $"only {made} of 200 mutants could be made" : null;
    }

    /// <summary>
    /// A mutation in `unsafe` code must be marked, since a mutant that has one is compiled but not
    /// run; one in safe code, including a safe block next to an unsafe one, must not be.
    /// </summary>
    static string? UnsafeSites()
    {
        const string text = """
            unsafe int first(int* values) {
                return values[0];
            }

            void main() {
                int x = 1;
                unsafe {
                    x = x + 2;
                }
                x = 3;
            }
            """;
        var index = AstIndex.Of(new Parser(new SourceText("<fuzz>", text), new DiagnosticBag()).ParseCompilationUnit());
        var literals = index.Of(SiteKind.IntegerLiteral);
        string? Check(string what, int offset, bool expected)
        {
            int start = text.IndexOf(what, StringComparison.Ordinal) + offset;
            return literals.FirstOrDefault(site => site.Span.Start == start) is { } site
                ? site.Unsafe == expected ? null : $"the literal in '{what}' is marked {(site.Unsafe ? "unsafe" : "safe")}"
                : $"no literal site in '{what}'";
        }
        return Check("values[0]", 7, expected: true)
            ?? Check("x + 2", 4, expected: true)
            ?? Check("x = 3", 4, expected: false);
    }
}
