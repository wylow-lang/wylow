using Wyl.Compiler;
using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Interpret;
using Wyl.Compiler.Text;
using Wyl.Harness;

// The interpret mode runs the program's meaning (lowering and the mandatory passes, no
// optimization) and checks a test's expectations as a native mode does, except that it skips
// `// mode: optimized` tests and the `// stack:` and `// in-place:` markers, which count
// optimizations, and `// native` tests, which call C++ code (decision 0087). Every program that runs is then interpreted in its optimized form too, and the
// two must mean the same thing, which checks the passes without the emitter or a C++ compiler.

static partial class TestCase
{
    public static TestResult RunInterpreted(string file, string casesDirectory)
    {
        var name = Path.GetRelativePath(casesDirectory, file).Replace('\\', '/');
        try
        {
            return new TestResult(name, CheckInterpreted(file));
        }
        catch (Exception e)
        {
            return new TestResult(name, $"exception: {e}");
        }
    }

    static string? CheckInterpreted(string file)
    {
        var test = Load(file);
        if (test.Problem is not null)
            return test.Problem;
        var expected = test.Expected;
        // A test that observes an optimization isn't compared without one.
        if (expected.Optimized || RunComparison.IsNative(test.Text))
            return null;
        var interpreted = Compilation.Interpret(test.Sources, new CompileOptions(Optimize: false), new InterpreterOptions(Release: expected.Release, Arguments: expected.Arguments));
        if (CompareDiagnostics(expected, interpreted.Diagnostics) is { } diagnosticProblems)
            return diagnosticProblems;
        if (expected.Errors.Count > 0)
            return null;
        if (interpreted.Interpretation is not { } result)
            return "the pipeline produced no IR";

        // A budget exhaustion is inconclusive, like a timeout, not a failure.
        if (result.Status == InterpretationStatus.BudgetExceeded)
            return null;
        if (result.Status == InterpretationStatus.Unsupported)
            return "unsupported: " + result.Error.Trim();
        if (result.Status == InterpretationStatus.Native)
            return "the program calls C++ code, so it needs the '// native' marker: " + result.Error.Trim();

        var problems = new List<string>();
        var actualOutput = result.OutputText.Replace("\r\n", "\n").Split('\n').ToList();
        if (actualOutput[^1] == "")
            actualOutput.RemoveAt(actualOutput.Count - 1);
        if (!actualOutput.SequenceEqual(expected.Output))
        {
            problems.Add("output mismatch");
            problems.Add("  expected: " + string.Join(" | ", expected.Output));
            problems.Add("  actual:   " + string.Join(" | ", actualOutput));
        }

        var stderr = result.Error.Trim().Replace("\r\n", "\n");
        if (expected.Panic is not null)
        {
            var at = expected.PanicLine is { } line ? $"\n    at {test.PanicName}:{line}:" : "";
            if (result.Status != InterpretationStatus.Panicked || !stderr.Contains("panic: " + expected.Panic) || !stderr.Contains(at))
                problems.Add($"expected panic '{expected.Panic}'{(at.Length > 0 ? $" at line {expected.PanicLine}" : "")}, got {result.Status} and stderr: {stderr}");
        }
        else if (result.Status != InterpretationStatus.Completed || result.ExitCode != expected.Exit)
        {
            problems.Add($"exit code {result.ExitCode}, expected {expected.Exit}; stderr: {stderr}");
        }

        const string warningPrefix = "performance warning: ";
        var warnings = stderr.Split('\n').Where(l => l.StartsWith(warningPrefix, StringComparison.Ordinal)).Select(l => l[warningPrefix.Length..]).ToList();
        foreach (var warning in expected.Warnings)
        {
            int index = warnings.FindIndex(w => w.Contains(warning, StringComparison.Ordinal));
            if (index >= 0)
                warnings.RemoveAt(index);
            else
                problems.Add($"missing warning: {warning}");
        }
        problems.AddRange(warnings.Select(w => $"unexpected warning: {w}"));

        // The pass-regression check: the optimized IR is the same program, so interpreting it must
        // agree with interpreting the meaning (docs/design/ir.md §9.11).
        var optimized = Compilation.Interpret(test.Sources, new CompileOptions(), new InterpreterOptions(Release: expected.Release, Arguments: expected.Arguments));
        if (optimized.Interpretation is { } optimizedResult)
        {
            switch (optimizedResult.Status)
            {
                case InterpretationStatus.Completed or InterpretationStatus.Panicked:
                    if (RunComparison.Compare(RunOutcome.From(result), RunOutcome.From(optimizedResult), test.Text, compareWarnings: true) is { } difference)
                        problems.Add($"the optimized IR disagrees with the meaning ({difference.Kind}): {difference.Detail}");
                    break;
                case InterpretationStatus.BudgetExceeded:
                    break;
                default:
                    problems.Add($"the optimized IR ended as {optimizedResult.Status}: {optimizedResult.Error.Trim()}");
                    break;
            }
        }

        return problems.Count == 0 ? null : string.Join('\n', problems);
    }
}
