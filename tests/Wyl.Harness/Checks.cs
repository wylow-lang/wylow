using System.Diagnostics;
using System.Text;
using Wyl.Compiler;
using Wyl.Compiler.Build;
using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Interpret;
using Wyl.Compiler.Text;

namespace Wyl.Harness;

public sealed record Mode(string Name, Toolchain? Toolchain, CompileOptions Options)
{
    /// <summary>The run is the IR interpreter, not a native build: no C++ and no toolchain.</summary>
    public bool Interpret => Toolchain is null;
}

/// <summary>A failure the fuzzer found, with the narrowest scope in which it can be reproduced.</summary>
/// <param name="Key">What dedupes findings: a coarse kind, not the failing text, which a reduction changes.</param>
/// <param name="Signature">What a reduction must preserve, which is narrower than the key where it can be.</param>
/// <param name="Detail">What a person reads.</param>
/// <param name="Scope">The check to re-run while reducing, or null when the finding can't reproduce reliably.</param>
public sealed record Finding(string Key, string Signature, string Detail, CheckScope? Scope);

/// <summary>Which oracles to run, so a reduction pays for only the one that found the bug.</summary>
public sealed record CheckScope(
    bool DefaultCompile = false,
    bool NoOptCompile = false,
    bool Determinism = false,
    IReadOnlyList<int>? ModeIndexes = null,
    bool Compare = false,
    bool Strict = false,
    bool Interpretation = false)
{
    public IReadOnlyList<int> Modes => ModeIndexes ?? [];
}

public sealed record CompileOutcome(CompilationResult? Result, IReadOnlyList<Diagnostic>? Errors, Exception? Exception, LowerResult? Lowered);

/// <summary>
/// The oracles from the testing plan: tier 1 compiles in-process with and without optimization and
/// interprets both IR forms, and tier 2 builds and runs every mode, the interpreter among them, and
/// compares them with one rule. A finding says which scope reproduces it.
/// </summary>
public sealed class Checker(string outputDirectory, IReadOnlyList<Mode> modes, double runTimeoutSeconds, string sourceName = "<fuzz>")
{
    static readonly CompileOptions DefaultOptions = new();
    static readonly CompileOptions NoOptOptions = new(Nest: true, Optimize: false);
    const int OutputCap = 1 << 20;

    /// <summary>
    /// A fuzz budget: a program that runs longer than this is inconclusive rather than a finding, so
    /// one runaway mutant can't hold up tier 1. The step budget bounds cheap loops and the time limit
    /// bounds the interpreter's slow ones.
    /// </summary>
    static readonly InterpreterOptions InterpretationOptions =
        new(StepBudget: 20_000_000, TimeLimit: TimeSpan.FromSeconds(2));

    /// <summary>
    /// Warnings a valid program makes the C++ compiler draw about its own code: a comparison with
    /// itself, an unsigned comparison with a value outside its range, a local it writes and never
    /// reads, or an xor of two constants that reads like a power (<c>2 ^ 2</c>). None is about how the
    /// emitter translated something, so they are not the emitter's.
    /// </summary>
    static readonly HashSet<string> SourceWarnings = ["tautological-compare", "type-limits", "unused-but-set-variable", "xor-used-as-pow"];

    public IReadOnlyList<Mode> Modes { get; } = modes;

    public CheckScope Tier1 { get; } = new(DefaultCompile: true, NoOptCompile: true, Determinism: true, Interpretation: true);

    public CheckScope Tier2(bool strict) => new(
        ModeIndexes: [.. Enumerable.Range(0, Modes.Count)],
        Compare: true,
        Strict: strict);

    /// <param name="compiled">Whether the default program compiled, so tier 2 knows whether to run.</param>
    /// <param name="unsafeCode">Whether a mutation edited <c>unsafe</c> code, whose program may do anything at run time.</param>
    public Finding? Check(string text, CheckScope scope, out bool compiled, bool unsafeCode = false)
    {
        var source = new SourceText(sourceName, text);
        var finding = CompileTier(source, scope, unsafeCode, out compiled);
        if (finding is not null || scope.Modes.Count == 0)
            return finding;
        return RunModes(source, text, scope, out compiled, unsafeCode);
    }

    Finding? CompileTier(SourceText source, CheckScope scope, bool unsafeCode, out bool compiled)
    {
        compiled = false;
        CompileOutcome? plain = null, noOpt = null;
        if (scope.DefaultCompile)
        {
            plain = Compile(source, DefaultOptions);
            if (plain.Exception is { } exception)
                return new Finding(
                    $"tier1/exception/default/{ExceptionKey(exception)}",
                    $"tier1/exception/default/{ExceptionKey(exception)}",
                    Format(exception),
                    new CheckScope(DefaultCompile: true));
            compiled = plain.Result?.Success == true;
        }
        if (scope.NoOptCompile)
        {
            noOpt = Compile(source, NoOptOptions);
            if (noOpt.Exception is { } exception)
                return new Finding(
                    $"tier1/exception/no-opt/{ExceptionKey(exception)}",
                    $"tier1/exception/no-opt/{ExceptionKey(exception)}",
                    Format(exception),
                    new CheckScope(NoOptCompile: true));
        }
        if (plain is not null && noOpt is not null && !SameErrors(plain, noOpt))
            return new Finding(
                "tier1/option-errors",
                "tier1/option-errors",
                $"the errors differ with and without --no-opt\n  default: {Describe(plain)}\n  no-opt:  {Describe(noOpt)}",
                new CheckScope(DefaultCompile: true, NoOptCompile: true));
        if (scope.DefaultCompile && scope.Determinism && plain is not null)
        {
            var again = Compile(source, DefaultOptions);
            if (again.Exception is not null || !Same(plain, again))
                return new Finding(
                    "tier1/nondeterministic",
                    "tier1/nondeterministic",
                    again.Exception is { } exception
                        ? $"the second compile threw {Format(exception)}"
                        : "compiling twice gave different diagnostics or C++",
                    null);
        }
        if (scope.Interpretation && plain is { Lowered: { Ir: not null, Optimized: not null } } && noOpt is { Lowered: { Ir: not null, Optimized: not null } })
        {
            if (CheckInterpretation(plain.Lowered, noOpt.Lowered, source, unsafeCode) is { } finding)
                return finding;
        }
        return null;
    }

    /// <summary>
    /// The pass-regression oracle: the interpreter runs the optimized IR and the meaning, and the two
    /// must agree, all without a C++ compiler (docs/design/ir.md §9.11). This is where a pass that
    /// changes the answer fails even when every native mode happens to agree.
    /// </summary>
    Finding? CheckInterpretation(LowerResult optimized, LowerResult meaning, SourceText source, bool unsafeCode)
    {
        var text = source.Text;
        if (RunComparison.SkipsComparison(text, unsafeCode))
            return null;
        var scope = new CheckScope(DefaultCompile: true, NoOptCompile: true, Interpretation: true);
        Interpretation optimizedRun, meaningRun;
        try
        {
            meaningRun = Interpreter.Run(meaning.Ir!, meaning.Optimized!.Callees, InterpretationOptions);
            optimizedRun = Interpreter.Run(optimized.Ir!, optimized.Optimized!.Callees, InterpretationOptions);
        }
        catch (Exception exception)
        {
            return new Finding(
                $"tier1/interpreter-exception/{ExceptionKey(exception)}",
                $"tier1/interpreter-exception/{ExceptionKey(exception)}",
                Format(exception),
                scope);
        }
        if (Unexpected(optimizedRun) is { } optimizedFailure)
            return new Finding($"tier1/interpretation/{optimizedFailure}", $"tier1/interpretation/{optimizedFailure}",
                $"the optimized IR: {optimizedRun.Error.Trim()}", scope);
        if (Unexpected(meaningRun) is { } meaningFailure)
            return new Finding($"tier1/interpretation/{meaningFailure}", $"tier1/interpretation/{meaningFailure}",
                $"the meaning: {meaningRun.Error.Trim()}", scope);
        if (RunComparison.Compare(RunOutcome.From(meaningRun), RunOutcome.From(optimizedRun), text, compareWarnings: true) is { } difference)
            return new Finding(
                $"tier1/interpretation/{difference.Kind}",
                $"tier1/interpretation/{difference.Kind}",
                $"the optimized IR disagrees with the meaning\n{difference.Detail}",
                scope);
        return null;

        string? Unexpected(Interpretation result) => result.Status switch
        {
            InterpretationStatus.Completed or InterpretationStatus.Panicked or InterpretationStatus.BudgetExceeded => null,
            InterpretationStatus.Unsupported => "unsupported",
            // Undefined behavior is the program's in `unsafe` code, which is skipped above; in safe
            // code it is the interpreter's or a pass's.
            _ => text.Contains("unsafe", StringComparison.Ordinal) || text.Contains("@Unchecked", StringComparison.Ordinal) ? null : "undefined-behavior",
        };
    }

    Finding? RunModes(SourceText source, string text, CheckScope scope, out bool compiled, bool unsafeCode)
    {
        compiled = false;
        var results = new List<ModeResult>();
        Finding? warning = null;
        int firstIndex = scope.Modes[0];
        int? warningIndex = scope.Strict
            ? scope.Modes.Where(i => !Modes[i].Interpret && Modes[i].Options.Optimize).Select(i => (int?)i).FirstOrDefault()
            : null;
        foreach (var index in scope.Modes)
        {
            var mode = Modes[index];
            if (mode.Interpret)
            {
                var interpretation = Interpret(source, mode.Options);
                if (interpretation.Exception is { } exception)
                    return new Finding(
                        $"tier2/exception/{mode.Name}/{ExceptionKey(exception)}",
                        $"tier2/exception/{mode.Name}/{ExceptionKey(exception)}",
                        Format(exception),
                        new CheckScope(ModeIndexes: [index]));
                if (interpretation.Errors is { Count: > 0 } interpretationErrors)
                {
                    if (results.Count == 0)
                        return null;
                    return new Finding(
                        $"tier2/options-errors/{mode.Name}",
                        $"tier2/options-errors/{mode.Name}",
                        $"'{mode.Name}' reports errors the first mode doesn't:\n{Describe(interpretationErrors)}",
                        new CheckScope(ModeIndexes: [firstIndex, index]));
                }
                if (interpretation.Run is null)
                    return new Finding(
                        $"tier2/no-run/{mode.Name}",
                        $"tier2/no-run/{mode.Name}",
                        $"'{mode.Name}' produced no interpretation",
                        new CheckScope(ModeIndexes: [index]));
                if (index == firstIndex)
                    compiled = true;
                results.Add(new ModeResult(mode, Skipped(mode) ? RunOutcome.Inconclusive : RunOutcome.From(interpretation.Run)));
                continue;
            }

            var compile = Compile(source, mode.Options);
            if (compile.Exception is { } compileException)
                return new Finding(
                    $"tier2/exception/{mode.Name}/{ExceptionKey(compileException)}",
                    $"tier2/exception/{mode.Name}/{ExceptionKey(compileException)}",
                    Format(compileException),
                    new CheckScope(ModeIndexes: [index]));

            if (compile.Errors is { Count: > 0 } errors)
            {
                // The first mode is where the program must compile at all; a later one failing means the
                // options changed what a program may do, which they never may.
                if (results.Count == 0)
                    return null;
                return new Finding(
                    $"tier2/options-errors/{mode.Name}",
                    $"tier2/options-errors/{mode.Name}",
                    $"'{mode.Name}' reports errors the first mode doesn't:\n{Describe(errors)}",
                    new CheckScope(ModeIndexes: [firstIndex, index]));
            }
            if (compile.Result?.Cpp is null)
                return new Finding(
                    $"tier2/no-cpp/{mode.Name}",
                    $"tier2/no-cpp/{mode.Name}",
                    $"'{mode.Name}' compiled with no errors and no C++",
                    new CheckScope(ModeIndexes: [index]));
            if (index == firstIndex)
                compiled = true;

            var directory = Path.Combine(outputDirectory, mode.Name);
            var build = Driver.BuildExecutable(compile.Result, "prog", directory, new BuildOptions(false, mode.Toolchain!.Value));
            if (!build.Success)
            {
                var fingerprint = BuildFingerprint(build.Error!);
                return new Finding(
                    $"tier2/build/{mode.Name}",
                    $"tier2/build/{mode.Name}/{fingerprint}",
                    build.Error!,
                    new CheckScope(ModeIndexes: [index]));
            }

            // A warning is reported after the runs are compared, so a program that draws one is still
            // checked for what it does; a difference in behavior is the finding to report first.
            if (warningIndex is { } warningAt && index == warningAt)
                warning = CheckWarnings(Path.Combine(directory, "prog.cpp"), index);

            // A mutation in `unsafe` code may do what the language calls undefined, so what the
            // program prints, crashes with, or reports to a sanitizer is the program's, not the
            // compiler's; only the C++ it compiles to must still be sound.
            if (Skipped(mode))
            {
                results.Add(new ModeResult(mode, RunOutcome.Inconclusive));
                continue;
            }

            var run = CappedRunner.Run(build.ExePath!, TimeSpan.FromSeconds(runTimeoutSeconds), OutputCap);
            if (run.TimedOut || run.TooMuchOutput)
            {
                results.Add(new ModeResult(mode, RunOutcome.Inconclusive));
                continue;
            }

            if (NativeCompiler.SanitizerReport(run.StandardError) is { } report)
            {
                var kind = SanitizerKind(report);
                // A stack overflow is a program that recursed too far, which is the runaway-recursion
                // counterpart of the timeout below: exhaustion, not the undefined behavior these
                // sanitizers exist to find.
                if (kind == "stack-overflow")
                {
                    results.Add(new ModeResult(mode, RunOutcome.Inconclusive));
                    continue;
                }
                // `unsafe` and `@Unchecked` exist to do what these checks call UB, so a report there is
                // the program's, not the compiler's; the run is also unusable for comparison.
                if (text.Contains("unsafe", StringComparison.Ordinal) || text.Contains("@Unchecked", StringComparison.Ordinal))
                {
                    results.Add(new ModeResult(mode, RunOutcome.Inconclusive));
                    continue;
                }
                return new Finding(
                    $"tier2/sanitizer/{kind}",
                    $"tier2/sanitizer/{mode.Name}/{kind}",
                    $"{mode.Name}: {report}",
                    new CheckScope(ModeIndexes: [index]));
            }
            results.Add(new ModeResult(mode, RunOutcome.FromNative(run.StandardOutput, run.ExitCode, run.StandardError)));
        }

        if (!scope.Compare)
            return warning;
        var conclusive = results.Where(r => r.Outcome.Conclusive).ToList();
        if (conclusive.Count < 2)
            return warning;
        // The interpreter is the reference where it could run: it is the program's meaning, so every
        // native mode is checked against it, the unoptimized one first, which is the pair the
        // documented collector exception is about.
        var reference = conclusive.FirstOrDefault(r => r.Mode.Interpret) ?? conclusive[0];
        var others = conclusive.Where(r => r != reference).OrderBy(r => r.Mode.Options.Optimize ? 1 : 0).ToList();
        foreach (var other in others)
        {
            var pair = $"{reference.Mode.Name},{other.Mode.Name}";
            var recheck = new CheckScope(ModeIndexes: [IndexOfMode(reference.Mode.Name), IndexOfMode(other.Mode.Name)], Compare: true);
            // The interpreter mirrors the unoptimized IR's releases, so its pair with --no-opt is held
            // to the performance warnings too; an optimization may change what the collector reports.
            bool compareWarnings = reference.Mode.Interpret && !other.Mode.Options.Optimize;
            if (RunComparison.Compare(reference.Outcome, other.Outcome, text, compareWarnings) is { } difference)
                return new Finding(
                    $"tier2/disagree/{pair}/{difference.Kind}",
                    $"tier2/disagree/{pair}/{difference.Kind}",
                    $"{difference.Kind} differs between {reference.Mode.Name} and {other.Mode.Name}\n{difference.Detail}",
                    recheck);
        }
        return warning;

        // A mode the program is not compared in: its mutation edited `unsafe` code, or the test
        // observes an optimization that --no-opt (and the meaning) turns off.
        bool Skipped(Mode mode) => unsafeCode || (!mode.Options.Optimize && text.Contains("// mode: optimized", StringComparison.Ordinal));
    }

    int IndexOfMode(string name) => Modes.Select((mode, index) => (mode, index)).First(pair => pair.mode.Name == name).index;

    /// <summary>
    /// The generated C++ must compile warning-free under the flags CLAUDE.md names. Integer
    /// conversion warnings are left out: g++ draws them after constant folding, on casts the emitter
    /// wrote explicitly, and since C++20 every integer conversion is defined, so a narrowing is a bug
    /// only if it changes a value, which the comparison with the interpreter finds directly.
    /// </summary>
    Finding? CheckWarnings(string cppPath, int referenceIndex)
    {
        var startInfo = new ProcessStartInfo(OperatingSystem.IsWindows() ? "g++.exe" : "g++");
        foreach (var argument in new[] { "-std=c++20", "-fsyntax-only", "-Wall", "-Wextra", "-Wshadow", "-Wno-sign-compare", "-Wno-overflow", $"-I{NativeCompiler.RuntimeDirectory}", cppPath })
            startInfo.ArgumentList.Add(argument);
        ProcessResult result;
        try
        {
            result = ProcessRunner.Run(startInfo, TimeSpan.FromSeconds(120));
        }
        catch (Exception)
        {
            // No g++: the other modes still ran, and the suite's own warning check is the one to run.
            return null;
        }
        var warnings = result.StandardError.Replace("\r\n", "\n").Split('\n')
            .Where(line => line.Contains(": warning: ", StringComparison.Ordinal) && !line.Contains("unused parameter", StringComparison.Ordinal))
            .Where(line => !SourceWarnings.Contains(WarningCategory(line)))
            .ToList();
        if (warnings.Count > 0)
        {
            var category = WarningCategory(warnings[0]);
            // The message without its location, which a reduction's deletions would change: two
            // warnings of one category about different conversions are two findings.
            const string marker = ": warning: ";
            var message = warnings[0][(warnings[0].IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Trim();
            return new Finding(
                $"tier2/cpp-warning/{category}",
                $"tier2/cpp-warning/{category}/{message}",
                string.Join("\n", warnings.Take(10)),
                new CheckScope(ModeIndexes: [referenceIndex], Strict: true));
        }
        if (result.ExitCode != 0)
            return new Finding(
                "tier2/cpp-error",
                "tier2/cpp-error",
                result.StandardError.Trim(),
                new CheckScope(ModeIndexes: [referenceIndex], Strict: true));
        return null;
    }

    static string WarningCategory(string warning)
    {
        const string marker = "[-W";
        int start = warning.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return "other";
        int end = warning.IndexOf(']', start);
        return end < 0 ? "other" : warning[(start + marker.Length)..end];
    }

    static CompileOutcome Compile(SourceText source, CompileOptions options)
    {
        try
        {
            var lowered = Compilation.Lower(source, options);
            var result = Compilation.Emit(lowered, options.Nest);
            return new CompileOutcome(result, [.. result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)], null, lowered);
        }
        catch (Exception exception)
        {
            return new CompileOutcome(null, null, exception, null);
        }
    }

    /// <summary>Interprets the form the mode names, so a mode can be the interpreter beside the builds.</summary>
    static (Interpretation? Run, IReadOnlyList<Diagnostic>? Errors, Exception? Exception) Interpret(SourceText source, CompileOptions options)
    {
        try
        {
            var result = Compilation.Interpret(source, options, InterpretationOptions);
            return (result.Interpretation, [.. result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)], null);
        }
        catch (Exception exception)
        {
            return (null, null, exception);
        }
    }

    static bool SameErrors(CompileOutcome a, CompileOutcome b) =>
        (a.Errors ?? []).Select(Describe).SequenceEqual((b.Errors ?? []).Select(Describe));

    static bool Same(CompileOutcome a, CompileOutcome b) =>
        SameErrors(a, b) && a.Result?.Cpp == b.Result?.Cpp;

    static (int Line, int Column, string Message) Describe(Diagnostic diagnostic)
    {
        var (line, column) = diagnostic.Location.LineColumn;
        return (line, column, diagnostic.Message);
    }

    static string Describe(IReadOnlyList<Diagnostic> errors) =>
        errors.Count == 0 ? "no errors" : string.Join(" | ", errors.Select(e => $"{e.Line}:{e.Location.LineColumn.Column}: {e.Message}"));

    static string Describe(CompileOutcome outcome) => Describe(outcome.Errors ?? []);

    static string Format(Exception exception) => Unwrap(exception).ToString();

    /// <summary>The type and first message line: two crashes of one type are likely two bugs.</summary>
    static string ExceptionKey(Exception exception)
    {
        var unwrapped = Unwrap(exception);
        var line = unwrapped.Message.Split('\n')[0].Trim();
        if (line.Length > 100)
            line = line[..100];
        return unwrapped.GetType().Name + "|" + line;
    }

    static Exception Unwrap(Exception exception) =>
        exception is AggregateException { InnerExceptions.Count: 1 } aggregate ? Unwrap(aggregate.InnerExceptions[0]) : exception;

    /// <summary>The first C++ error without its location, which a reduction's deletions would change.</summary>
    static string BuildFingerprint(string error)
    {
        foreach (var line in error.Replace("\r\n", "\n").Split('\n'))
        {
            int at = line.IndexOf("error", StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                var text = line[at..].Trim();
                return text.Length > 200 ? text[..200] : text;
            }
        }
        return error.Split('\n')[0].Trim();
    }

    static string SanitizerKind(string report)
    {
        const string asan = "AddressSanitizer: ";
        int index = report.IndexOf(asan, StringComparison.Ordinal);
        if (index >= 0)
        {
            var rest = report[(index + asan.Length)..];
            int space = rest.IndexOf(' ');
            return space < 0 ? rest : rest[..space];
        }
        const string ubsan = ": runtime error: ";
        index = report.IndexOf(ubsan, StringComparison.Ordinal);
        if (index >= 0)
        {
            var rest = report[(index + ubsan.Length)..];
            return rest.Length > 60 ? rest[..60] : rest;
        }
        return "report";
    }

    sealed record ModeResult(Mode Mode, RunOutcome Outcome);
}

/// <summary>The generated program's run, with a cap on output so a mutant in an infinite loop can't fill memory.</summary>
public sealed record RunResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut, bool TooMuchOutput);

public static class CappedRunner
{
    public static RunResult Run(string exePath, TimeSpan timeout, int cap)
    {
        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"could not start '{exePath}'");
        var output = new StringBuilder();
        var error = new StringBuilder();
        var overflow = new Overflow();
        var readOutput = Task.Run(() => Read(process.StandardOutput, output, cap, process, overflow));
        var readError = Task.Run(() => Read(process.StandardError, error, Math.Max(cap / 4, 4096), process, overflow));
        bool exited = process.WaitForExit((int)timeout.TotalMilliseconds);
        if (!exited)
            Kill(process);
        process.WaitForExit(5000);
        Task.WaitAll([readOutput, readError], 5000);
        return new RunResult(exited ? process.ExitCode : -1, output.ToString(), error.ToString(), !exited, overflow.Value);
    }

    static void Read(StreamReader reader, StringBuilder builder, int cap, Process process, Overflow overflow)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                int read = reader.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    return;
                builder.Append(buffer, 0, read);
                if (builder.Length > cap)
                {
                    overflow.Value = true;
                    Kill(process);
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The process was killed, or its pipe closed under us.
        }
    }

    static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    sealed class Overflow
    {
        public bool Value;
    }
}
