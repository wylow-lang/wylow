using Wyl.Compiler.Interpret;

namespace Wyl.Harness;

/// <summary>
/// What a run produced, reduced to what every mode must agree on: a native build's run or an
/// interpretation. The runner and the fuzzer compare with the same rule, so they cannot disagree
/// about what a disagreement is (docs/design/ir.md §9.11).
/// </summary>
public sealed record RunOutcome(
    string Output, int ExitCode, string Panic, IReadOnlyList<string> Warnings, bool Panicked, bool Conclusive)
{
    /// <summary>A run nobody can compare: a budget, an unsupported feature, or undefined behavior.</summary>
    public static readonly RunOutcome Inconclusive = new("", 0, "", [], false, false);

    public static RunOutcome From(Interpretation interpretation)
    {
        var warnings = WarningLines(interpretation.Error);
        return interpretation.Status switch
        {
            InterpretationStatus.Completed =>
                new(Text(interpretation.OutputText), interpretation.ExitCode, "", warnings, false, true),
            InterpretationStatus.Panicked =>
                new(Text(interpretation.OutputText), interpretation.ExitCode, PanicText(interpretation.Error), warnings, true, true),
            _ => new("", 0, "", warnings, false, false),
        };
    }

    public static RunOutcome FromNative(string standardOutput, int exitCode, string standardError)
    {
        var panic = PanicText(standardError);
        return new(Text(standardOutput), exitCode, panic, WarningLines(standardError), panic.Length > 0, true);
    }

    /// <summary>A stream's text without its trailing newline, so a mode's line endings don't matter.</summary>
    public static string Text(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        return normalized.EndsWith('\n') ? normalized[..^1] : normalized;
    }

    /// <summary>Only the panic and its location: other stderr lines may legitimately differ by mode.</summary>
    public static string PanicText(string standardError)
    {
        var lines = standardError.Replace("\r\n", "\n").Split('\n');
        int start = Array.FindIndex(lines, line => line.StartsWith("panic: ", StringComparison.Ordinal));
        return start < 0 ? "" : string.Join("\n", lines[start..]).Trim();
    }

    public static IReadOnlyList<string> WarningLines(string standardError)
    {
        const string prefix = "performance warning: ";
        return [.. standardError.Replace("\r\n", "\n").Split('\n')
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line[prefix.Length..])];
    }
}

/// <summary>Which of the compared streams differs, and what it held.</summary>
public sealed record RunDifference(string Kind, string Detail);

/// <summary>The comparison rule the runner and the fuzzer share (docs/design/ir.md §9.11).</summary>
public static partial class RunComparison
{
    /// <summary>
    /// A program the modes are not compared on: it observes an optimization, or its mutation edited
    /// <c>unsafe</c> code, which may do what the language calls undefined.
    /// </summary>
    public static bool SkipsComparison(string text, bool unsafeCode) =>
        unsafeCode || text.Contains("// mode: optimized", StringComparison.Ordinal);

    /// <summary>
    /// Compares two runs: stdout, the panic's message and location, the exit code, and, where the
    /// caller asks, the performance warnings. A program with a budgeted collector may legitimately
    /// reach other cycles when an optimization is off, and one reading `cycleStats` counts what an optimization
    /// keeps off the heap (spec 03-memory-model.md, diagnostics), so neither program's stdout is compared. The abort exit
    /// code is the platform's, so two panics are compared by their text, not their code.
    /// <para>
    /// With <paramref name="ignoreZeroSign"/>, which only a comparison involving a native build asks
    /// for, stdout that differs only in the sign of a printed zero is equal. Two compiler bugs
    /// print <c>0.0</c> where IEEE 754 gives <c>-0.0</c>, or the reverse, from correct C++:
    /// GCC 13 (at least, we didn't test earlier versions) through 16 fold <c>0.0 - (double)i</c> to <c>-(double)i</c> even at -O0 (GCC PR
    /// 126467, fixed for GCC 17), and MSVC /O1 and /O2 fold <c>-(float)(-i)</c> to <c>(float)i</c>
    /// under every /fp mode (https://developercommunity.visualstudio.com/t/MSVC-x64-O2-folds--float-x-to-floa/11176874).
    /// </para>
    /// </summary>
    public static RunDifference? Compare(RunOutcome reference, RunOutcome other, string text, bool compareWarnings = false, bool ignoreZeroSign = false)
    {
        if (!reference.Conclusive || !other.Conclusive)
            return null;
        bool compareOutput = !text.Contains("collectCycles", StringComparison.Ordinal)
            && !text.Contains("cycleStats", StringComparison.Ordinal);
        if (compareOutput && reference.Output != other.Output
            && !(ignoreZeroSign && WithoutZeroSign(reference.Output) == WithoutZeroSign(other.Output)))
            return new("stdout", $"stdout differs\n  reference: {Truncate(reference.Output)}\n  other:     {Truncate(other.Output)}");
        if (reference.Panicked != other.Panicked)
            return new("exit", reference.Panicked
                ? $"the reference panicked and the other run did not\n  panic: {Truncate(reference.Panic)}\n  other stdout: {Truncate(other.Output)}"
                : $"the other run panicked and the reference did not\n  panic: {Truncate(other.Panic)}\n  reference stdout: {Truncate(reference.Output)}");
        if (reference.Panicked)
        {
            if (reference.Panic != other.Panic)
                return new("panic", $"panic output differs\n  reference: {Truncate(reference.Panic)}\n  other:     {Truncate(other.Panic)}");
        }
        else if (reference.ExitCode != other.ExitCode)
        {
            return new("exit", $"exit code differs: reference {reference.ExitCode}, other {other.ExitCode}");
        }
        if (compareWarnings && !reference.Warnings.SequenceEqual(other.Warnings))
            return new("warning", $"performance warnings differ\n  reference: {string.Join(" | ", reference.Warnings)}\n  other:     {string.Join(" | ", other.Warnings)}");
        return null;
    }

    /// <summary>Stdout with the sign of every printed zero dropped (<c>-0.0</c>, <c>-0.00</c>, <c>-0e+00</c>).</summary>
    public static string WithoutZeroSign(string output) => NegativeZero().Replace(output, "$1");

    // A whole printed number: not part of a longer one (-10.0, -0.05) or a word.
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![\w.])-(0(?:\.0*)?(?:[eE][+-]?\d+)?)(?![\w.])")]
    private static partial System.Text.RegularExpressions.Regex NegativeZero();

    static string Truncate(string text) => text.Length > 2000 ? text[..2000] + "…" : text;
}
