using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;
using Wyl.Harness;

namespace Wyl.Fuzz;

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
            return [("catalog", Catalog(files)), ("determinism", Determinism(files)), ("unsafe-sites", UnsafeSites()), ("comparison", Comparison()),
                ("pattern-mutants-parse", PatternMutantsParse(files)), ("chain-mutants-agree", ChainMutantsAgree(files))];
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
    /// The comparison rule the runner and the fuzzer share: each mode pair must be held to exactly
    /// what docs/design/ir.md §9.11 says, neither more nor less.
    /// </summary>
    static string? Comparison()
    {
        static RunOutcome Run(string output = "ok", int exit = 0, string error = "") =>
            RunOutcome.FromNative(output, exit, error);
        var failures = new List<string>();
        void Expect(string name, RunDifference? difference, string? kind)
        {
            if (difference?.Kind != kind)
                failures.Add($"{name}: expected {kind ?? "no difference"}, got {difference?.Kind ?? "no difference"}");
        }

        Expect("identical runs", RunComparison.Compare(Run(), Run(), ""), null);
        Expect("stdout", RunComparison.Compare(Run("a"), Run("b"), ""), "stdout");
        Expect("a budgeted collector's stdout", RunComparison.Compare(Run("a"), Run("b"), "memory::collectCycles()"), null);
        Expect("stdout of a program reading the collector's stats", RunComparison.Compare(Run("a"), Run("b"), "memory::cycleStats().buffered"), null);
        Expect("exit code", RunComparison.Compare(Run(exit: 0), Run(exit: 2), ""), "exit");
        Expect("the abort code is the platform's",
            RunComparison.Compare(Run(exit: 3, error: "panic: x\n    at f:1:2"), Run(exit: 134, error: "panic: x\n    at f:1:2"), ""), null);
        Expect("panic location",
            RunComparison.Compare(Run(exit: 3, error: "panic: x\n    at f:1:2"), Run(exit: 3, error: "panic: x\n    at f:1:3"), ""), "panic");
        Expect("one run panicked", RunComparison.Compare(Run(), Run(exit: 3, error: "panic: x\n    at f:1:2"), ""), "exit");
        Expect("warnings where the caller asks",
            RunComparison.Compare(Run(error: "performance warning: a"), Run(error: "performance warning: b"), "", compareWarnings: true), "warning");
        Expect("warnings otherwise ignored",
            RunComparison.Compare(Run(error: "performance warning: a"), Run(error: "performance warning: b"), ""), null);
        Expect("an inconclusive run", RunComparison.Compare(RunOutcome.Inconclusive, Run(), ""), null);
        if (OperatingSystem.IsWindows())
        {
            // Stack exhaustion is the runaway-recursion counterpart of a timeout, not a result: its
            // exit code is the OS's, and a sanitizer build reports the same overflow as an access
            // violation, which no mode's code can be compared with or against.
            Expect("a stack overflow", RunComparison.Compare(Run(exit: RunOutcome.StackOverflowExitCode), Run(), ""), null);
            Expect("a stack overflow against a bare access violation",
                RunComparison.Compare(Run(exit: RunOutcome.StackOverflowExitCode), Run(exit: unchecked((int)0xC0000005)), ""), null);
        }
        Expect("a zero's sign where a native build is compared",
            RunComparison.Compare(Run("-0.0\nv -0.00 -0e+00\n"), Run("0.0\nv 0.00 0e+00\n"), "", ignoreZeroSign: true), null);
        Expect("a zero's sign otherwise",
            RunComparison.Compare(Run("-0.0\n"), Run("0.0\n"), ""), "stdout");
        Expect("a nonzero's sign where a native build is compared",
            RunComparison.Compare(Run("-0.05 -10.0 -0.1\n"), Run("0.05 10.0 0.1\n"), "", ignoreZeroSign: true), "stdout");
        Expect("other output where a native build is compared",
            RunComparison.Compare(Run("-0.0 a\n"), Run("0.0 b\n"), "", ignoreZeroSign: true), "stdout");
        if (!RunComparison.SkipsComparison("// mode: optimized", unsafeCode: false))
            failures.Add("a `// mode: optimized` test is compared");
        if (!RunComparison.SkipsComparison("void main() { }", unsafeCode: true))
            failures.Add("a mutant in unsafe code is compared");
        if (RunComparison.SkipsComparison("void main() { }", unsafeCode: false))
            failures.Add("a plain test is skipped");
        return failures.Count == 0 ? null : string.Join("; ", failures);
    }

    /// <summary>
    /// A mutation in `unsafe` code must be marked, since a mutant that has one is compiled but not
    /// run; one in safe code, including a safe block next to an unsafe one, must not be.
    /// </summary>
    /// <summary>
    /// The pattern mutations rewrite syntax rather than values, so each mutant they make of the
    /// corpus must still parse: otherwise they would only ever reach the parser's recovery.
    /// </summary>
    static string? PatternMutantsParse(IReadOnlyList<BaseFile> files)
    {
        var random = new Random(5);
        string[] names = ["pattern-field-discard", "case-guard-true"];
        int made = 0;
        foreach (var file in files)
        {
            var index = AstIndex.Of(new Parser(new SourceText("<fuzz>", file.Text), new DiagnosticBag()).ParseCompilationUnit());
            foreach (var mutation in Mutations.All.Where(m => names.Contains(m.Name)))
            {
                foreach (var site in index.Of(mutation.Kind).Where(mutation.Applies))
                {
                    if (mutation.Build(new MutationContext(file.Text, random), site) is not { } candidate)
                        continue;
                    var diagnostics = new DiagnosticBag();
                    new Parser(new SourceText("<fuzz>", Mutations.Apply(file.Text, candidate.Edits)), diagnostics).ParseCompilationUnit();
                    if (diagnostics.Count > 0)
                        return $"'{mutation.Name}' made a mutant of {file.Name} that doesn't parse: {diagnostics.First()}";
                    made++;
                }
            }
        }
        return made == 0 ? "the pattern mutations made no mutant" : null;
    }

    /// <summary>
    /// <c>constructor-chain</c> changes nothing a program does, so each of its mutants of a test
    /// that compiles must compile and print what the test does, interpreted. Otherwise the mutation
    /// would only make programs the binder rejects, or chaining has a bug the fuzzer would find.
    /// A test that prints source locations is skipped, since the forwarder moves the lines after it.
    /// </summary>
    static string? ChainMutantsAgree(IReadOnlyList<BaseFile> files)
    {
        var mutation = Mutations.All.Single(m => m.Name == "constructor-chain");
        var options = new Wyl.Compiler.Interpret.InterpreterOptions(StepBudget: 20_000_000, TimeLimit: TimeSpan.FromSeconds(2));
        // Optimized, since the passes are where a chain met what they assume. The suite runs this
        // in every mode, so the files run in parallel, each with a random of its own.
        var outcomes = files
            .Where(f => !f.Text.Contains("CallerLocation", StringComparison.Ordinal))
            .Select((file, i) => (file, i))
            .AsParallel()
            .AsOrdered()
            .Select(pair => ChainMutantAgrees(pair.file, mutation, options, new Random(11 + pair.i)))
            .ToList();
        if (outcomes.FirstOrDefault(o => o.Failure is not null).Failure is { } failure)
            return failure;
        return outcomes.Sum(o => o.Made) == 0 ? "'constructor-chain' made no mutant" : null;
    }

    static (string? Failure, int Made) ChainMutantAgrees(BaseFile file, Mutation mutation, Wyl.Compiler.Interpret.InterpreterOptions options, Random random)
    {
        var index = AstIndex.Of(new Parser(new SourceText("<fuzz>", file.Text), new DiagnosticBag()).ParseCompilationUnit());
        var sites = index.Of(mutation.Kind).Where(mutation.Applies).ToList();
        if (sites.Count == 0)
            return (null, 0);
        var original = Wyl.Compiler.Compilation.Interpret(new SourceText(file.Name, file.Text), null, options);
        if (original.Interpretation is not { Status: Wyl.Compiler.Interpret.InterpretationStatus.Completed } expected)
            return (null, 0);
        int made = 0;
        foreach (var site in sites)
        {
            if (mutation.Build(new MutationContext(file.Text, random), site) is not { } candidate)
                continue;
            var mutant = Wyl.Compiler.Compilation.Interpret(new SourceText(file.Name, Mutations.Apply(file.Text, candidate.Edits)), null, options);
            if (mutant.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error) is { } error)
                return ($"'constructor-chain' made a mutant of {file.Name} that doesn't compile: {error}", made);
            if (mutant.Interpretation is not { } run || run.OutputText != expected.OutputText || run.ExitCode != expected.ExitCode)
                return ($"'constructor-chain' changed what {file.Name} prints or returns ({candidate.Edits[1].Start}): {FirstDifference(expected, mutant.Interpretation)}", made);
            made++;
        }
        return (null, made);
    }

    static string FirstDifference(Wyl.Compiler.Interpret.Interpretation expected, Wyl.Compiler.Interpret.Interpretation? actual)
    {
        if (actual is null)
            return "it didn't run";
        var a = expected.OutputText.Split('\n');
        var b = actual.OutputText.Split('\n');
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            if (i >= a.Length || i >= b.Length || a[i] != b[i])
                return $"line {i + 1} was '{(i < a.Length ? a[i] : "")}', now '{(i < b.Length ? b[i] : "")}' ({actual.Status}, exit {actual.ExitCode}: {actual.Error.Trim()})";
        }
        return $"exit {expected.ExitCode}, now {actual.ExitCode}";
    }

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
