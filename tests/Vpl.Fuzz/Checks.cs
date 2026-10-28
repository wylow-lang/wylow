using System.Diagnostics;
using System.Text;
using Vpl.Compiler;
using Vpl.Compiler.Build;
using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Text;

namespace Vpl.Fuzz;

sealed record Mode(string Name, Toolchain Toolchain, CompileOptions Options);

/// <summary>A failure the fuzzer found, with the narrowest scope in which it can be reproduced.</summary>
/// <param name="Key">What dedupes findings: a coarse kind, not the failing text, which a reduction changes.</param>
/// <param name="Signature">What a reduction must preserve, which is narrower than the key where it can be.</param>
/// <param name="Detail">What a person reads.</param>
/// <param name="Scope">The check to re-run while reducing, or null when the finding can't reproduce reliably.</param>
sealed record Finding(string Key, string Signature, string Detail, CheckScope? Scope);

/// <summary>Which oracles to run, so a reduction pays for only the one that found the bug.</summary>
sealed record CheckScope(
    bool DefaultCompile = false,
    bool NoOptCompile = false,
    bool Determinism = false,
    IReadOnlyList<int>? ModeIndexes = null,
    bool Compare = false,
    bool Strict = false)
{
    public IReadOnlyList<int> Modes => ModeIndexes ?? [];
}

sealed record CompileOutcome(CompilationResult? Result, IReadOnlyList<Diagnostic>? Errors, Exception? Exception);

/// <summary>
/// The oracles from the testing plan: tier 1 compiles in-process with and without optimization, and tier
/// 2 builds and runs every mode and compares them. A finding says which scope reproduces it.
/// </summary>
sealed class Checker(string outputDirectory, IReadOnlyList<Mode> modes, double runTimeoutSeconds)
{
    static readonly CompileOptions DefaultOptions = new();
    static readonly CompileOptions NoOptOptions = new(Nest: true, Optimize: false);
    const int OutputCap = 1 << 20;

    /// <summary>
    /// Warnings a valid program makes the C++ compiler draw about its own constants: a comparison
    /// with itself, or an unsigned comparison with a value outside its range. No emitter can remove
    /// those without changing what the program computes (a float compared with itself is false for
    /// NaN), so they are not the emitter's.
    /// </summary>
    static readonly HashSet<string> SourceWarnings = ["tautological-compare", "type-limits"];

    public IReadOnlyList<Mode> Modes { get; } = modes;

    public CheckScope Tier1 { get; } = new(DefaultCompile: true, NoOptCompile: true, Determinism: true);

    public CheckScope Tier2(bool strict) => new(
        ModeIndexes: [.. Enumerable.Range(0, Modes.Count)],
        Compare: true,
        Strict: strict);

    /// <param name="compiled">Whether the default program compiled, so tier 2 knows whether to run.</param>
    /// <param name="unsafeCode">Whether a mutation edited <c>unsafe</c> code, whose program may do anything at run time.</param>
    public Finding? Check(string text, CheckScope scope, out bool compiled, bool unsafeCode = false)
    {
        var source = new SourceText("<fuzz>", text);
        var finding = CompileTier(source, scope, out compiled);
        if (finding is not null || scope.Modes.Count == 0)
            return finding;
        return RunModes(source, text, scope, out compiled, unsafeCode);
    }

    Finding? CompileTier(SourceText source, CheckScope scope, out bool compiled)
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
        return null;
    }

    Finding? RunModes(SourceText source, string text, CheckScope scope, out bool compiled, bool unsafeCode)
    {
        compiled = false;
        var results = new List<ModeResult>();
        int referenceIndex = scope.Modes[0];
        foreach (var index in scope.Modes)
        {
            var mode = Modes[index];
            var compile = Compile(source, mode.Options);
            if (compile.Exception is { } exception)
                return new Finding(
                    $"tier2/exception/{mode.Name}/{ExceptionKey(exception)}",
                    $"tier2/exception/{mode.Name}/{ExceptionKey(exception)}",
                    Format(exception),
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
                    new CheckScope(ModeIndexes: [referenceIndex, index]));
            }
            if (compile.Result?.Cpp is null)
                return new Finding(
                    $"tier2/no-cpp/{mode.Name}",
                    $"tier2/no-cpp/{mode.Name}",
                    $"'{mode.Name}' compiled with no errors and no C++",
                    new CheckScope(ModeIndexes: [index]));
            if (index == referenceIndex)
                compiled = true;

            var directory = Path.Combine(outputDirectory, mode.Name);
            var build = Driver.BuildExecutable(compile.Result, "prog", directory, new BuildOptions(false, mode.Toolchain));
            if (!build.Success)
            {
                var fingerprint = BuildFingerprint(build.Error!);
                return new Finding(
                    $"tier2/build/{mode.Name}",
                    $"tier2/build/{mode.Name}/{fingerprint}",
                    build.Error!,
                    new CheckScope(ModeIndexes: [index]));
            }

            if (scope.Strict && index == referenceIndex && CheckWarnings(Path.Combine(directory, "prog.cpp"), referenceIndex) is { } warning)
                return warning;

            // A mutation in `unsafe` code may do what the language calls undefined, so what the
            // program prints, crashes with, or reports to a sanitizer is the program's, not the
            // compiler's; only the C++ it compiles to must still be sound.
            if (unsafeCode)
            {
                results.Add(new ModeResult(mode.Name, "", -1, "", Conclusive: false));
                continue;
            }

            // The suite skips a test that observes an optimization under --no-opt, so what it prints
            // there may legitimately differ (CLAUDE.md: declining an optimization is always correct).
            if (!mode.Options.Optimize && text.Contains("// mode: optimized", StringComparison.Ordinal))
            {
                results.Add(new ModeResult(mode.Name, "", -1, "", Conclusive: false));
                continue;
            }

            var run = CappedRunner.Run(build.ExePath!, TimeSpan.FromSeconds(runTimeoutSeconds), OutputCap);
            if (run.TimedOut || run.TooMuchOutput)
            {
                results.Add(new ModeResult(mode.Name, "", -1, "", Conclusive: false));
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
                    results.Add(new ModeResult(mode.Name, "", -1, "", Conclusive: false));
                    continue;
                }
                // `unsafe` and `@Unchecked` exist to do what these checks call UB, so a report there is
                // the program's, not the compiler's; the run is also unusable for comparison.
                if (text.Contains("unsafe", StringComparison.Ordinal) || text.Contains("@Unchecked", StringComparison.Ordinal))
                {
                    results.Add(new ModeResult(mode.Name, "", -1, "", Conclusive: false));
                    continue;
                }
                return new Finding(
                    $"tier2/sanitizer/{kind}",
                    $"tier2/sanitizer/{mode.Name}/{kind}",
                    $"{mode.Name}: {report}",
                    new CheckScope(ModeIndexes: [index]));
            }
            results.Add(new ModeResult(mode.Name, Normalize(run.StandardOutput), run.ExitCode, PanicText(run.StandardError), Conclusive: true));
        }

        if (!scope.Compare)
            return null;
        var conclusive = results.Where(r => r.Conclusive).ToList();
        if (conclusive.Count < 2)
            return null;
        var reference = conclusive[0];
        // A budgeted collector may legitimately reach other cycles when an optimization is off.
        bool compareOutput = !text.Contains("collectCycles", StringComparison.Ordinal);
        foreach (var other in conclusive.Skip(1))
        {
            var pair = $"{reference.Name},{other.Name}";
            var recheck = new CheckScope(ModeIndexes: [IndexOfMode(reference.Name), IndexOfMode(other.Name)], Compare: true);
            if (compareOutput && other.Output != reference.Output)
                return new Finding(
                    $"tier2/disagree/{pair}/stdout",
                    $"tier2/disagree/{pair}/stdout",
                    $"stdout differs between {reference.Name} and {other.Name}\n  {reference.Name}: {Truncate(reference.Output)}\n  {other.Name}: {Truncate(other.Output)}",
                    recheck);
            if (other.ExitCode != reference.ExitCode)
                return new Finding(
                    $"tier2/disagree/{pair}/exit",
                    $"tier2/disagree/{pair}/exit",
                    $"exit code differs between {reference.Name} ({reference.ExitCode}) and {other.Name} ({other.ExitCode})\n  {other.Name} stderr: {Truncate(other.Panic)}",
                    recheck);
            if (other.Panic != reference.Panic)
                return new Finding(
                    $"tier2/disagree/{pair}/panic",
                    $"tier2/disagree/{pair}/panic",
                    $"panic output differs between {reference.Name} and {other.Name}\n  {reference.Name}: {Truncate(reference.Panic)}\n  {other.Name}: {Truncate(other.Panic)}",
                    recheck);
        }
        return null;
    }

    int IndexOfMode(string name) => Modes.Select((mode, index) => (mode, index)).First(pair => pair.mode.Name == name).index;

    /// <summary>The generated C++ must compile warning-free under the flags CLAUDE.md names.</summary>
    Finding? CheckWarnings(string cppPath, int referenceIndex)
    {
        var startInfo = new ProcessStartInfo(OperatingSystem.IsWindows() ? "g++.exe" : "g++");
        foreach (var argument in new[] { "-std=c++20", "-fsyntax-only", "-Wall", "-Wextra", "-Wconversion", "-Wsign-conversion", "-Wshadow", $"-I{NativeCompiler.RuntimeDirectory}", cppPath })
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
            return new Finding(
                $"tier2/cpp-warning/{category}",
                $"tier2/cpp-warning/{category}",
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
            var result = Compilation.Compile(source, options);
            return new CompileOutcome(result, [.. result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)], null);
        }
        catch (Exception exception)
        {
            return new CompileOutcome(null, null, exception);
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

    static string Normalize(string output)
    {
        var text = output.Replace("\r\n", "\n");
        return text.EndsWith('\n') ? text[..^1] : text;
    }

    /// <summary>Only the panic and its location: performance warnings may legitimately differ by mode.</summary>
    static string PanicText(string standardError)
    {
        var lines = standardError.Replace("\r\n", "\n").Split('\n');
        int start = Array.FindIndex(lines, line => line.StartsWith("panic: ", StringComparison.Ordinal));
        return start < 0 ? "" : string.Join("\n", lines[start..]).Trim();
    }

    static string Truncate(string text) => text.Length > 2000 ? text[..2000] + "…" : text;

    sealed record ModeResult(string Name, string Output, int ExitCode, string Panic, bool Conclusive);
}

/// <summary>The generated program's run, with a cap on output so a mutant in an infinite loop can't fill memory.</summary>
sealed record RunResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut, bool TooMuchOutput);

static class CappedRunner
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
