using System.Diagnostics;
using System.Text;
using Vpl.Compiler;
using Vpl.Compiler.Build;
using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Text;

/// <summary>
/// A cell of an interaction matrix: one small program, named by its row and column
/// (<c>row/column</c>).
/// </summary>
sealed record MatrixCell(string Name, string Source);

sealed record Matrix(string Name, string Title, List<MatrixCell> Cells);

// Interaction matrices (docs/handoff/testing.md, step 4): a table whose rows and columns are
// features, and whose cells are small programs that combine them. Each cell runs through the same
// oracles as a test case, and its result is recorded in tests/matrix/<name>.snapshot. The snapshot
// is reviewed against the spec cell by cell; after that, a change to any cell fails the suite until
// the snapshot is updated on purpose with --update-snapshots. Errors the spec calls for are part of
// a cell's result, so a cell that stops or starts being an error shows up as a diff too.
static class MatrixTests
{
    sealed record CellResult(string Name, List<string> Lines, string? Failure);

    /// <summary>The most differences a failing matrix lists before it says how many more there are.</summary>
    const int ShownDifferences = 24;

    public static List<TestResult> Run(string root, Toolchain? toolchain, CompileOptions options, string? filter, bool update)
    {
        List<TestResult> results = [];
        foreach (var matrix in Matrices())
        {
            var name = "matrix/" + matrix.Name;
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var snapshot = Path.Combine(root, "tests", "matrix", matrix.Name + ".snapshot");
            var output = Path.Combine(root, "tests", ".out", "matrix", matrix.Name);
            var cells = matrix.Cells
                .AsParallel()
                .AsOrdered()
                .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount / 2))
                .Select(cell => RunCell(cell, output, toolchain, options))
                .ToList();
            results.Add(Compare(name, snapshot, matrix, cells, update));
        }
        return results;
    }

    static IEnumerable<Matrix> Matrices()
    {
        yield return MatrixPlaces.Build();
        yield return MatrixTypes.Build();
        yield return MatrixValues.Build();
    }

    static CellResult RunCell(MatrixCell cell, string directory, Toolchain? toolchain, CompileOptions options)
    {
        try
        {
            var compilation = Compilation.Compile(new SourceText(cell.Name + ".vpl", cell.Source), options);
            var lines = new List<string>();
            foreach (var diagnostic in compilation.Diagnostics)
            {
                var severity = diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning";
                lines.Add($"{severity} {diagnostic.Line}: {diagnostic.Message}");
            }
            if (!compilation.Success)
                return new(cell.Name, lines, null);

            var cellDirectory = Path.Combine(directory, cell.Name.Replace('/', '-'));
            Directory.CreateDirectory(cellDirectory);
            File.WriteAllText(Path.Combine(cellDirectory, "cell.vpl"), cell.Source);
            var build = Driver.BuildExecutable(compilation, "cell", cellDirectory, new BuildOptions(false, toolchain));
            if (!build.Success)
                return new(cell.Name, lines, $"the generated C++ failed to compile:\n{build.Error}");

            var run = ProcessRunner.Run(
                new ProcessStartInfo(build.ExePath!)
                {
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                },
                TimeSpan.FromSeconds(30));
            if (run.TimedOut)
                return new(cell.Name, lines, "timed out");

            var standardError = run.StandardError.Replace("\r\n", "\n");
            if (NativeCompiler.SanitizerReport(standardError) is { } report)
                return new(cell.Name, lines, $"sanitizer: {report}\n{standardError.Trim()}");

            foreach (var line in SuffixLines(run.StandardOutput))
                lines.Add(line.Length == 0 ? "out" : "out " + line);
            foreach (var line in SuffixLines(standardError))
            {
                // clang's ASan cannot hook one function of this MSVC runtime and says so in every run.
                if (line.Contains("interception_win", StringComparison.Ordinal))
                    continue;
                lines.Add(line.Length == 0 ? "err" : "err " + line);
            }
            lines.Add("exit " + run.ExitCode);
            return new(cell.Name, lines, null);
        }
        catch (Exception e)
        {
            return new(cell.Name, [], $"exception: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>The lines of a stream, without the empty line after a trailing newline.</summary>
    static List<string> SuffixLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1] == "")
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    static TestResult Compare(string name, string snapshotPath, Matrix matrix, List<CellResult> cells, bool update)
    {
        var failures = cells.Where(c => c.Failure is not null).ToList();
        if (failures.Count > 0)
            return new(name, string.Join('\n', failures.Select(f => $"{f.Name}: {f.Failure}")));

        if (update)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            File.WriteAllText(snapshotPath, Format(matrix, cells));
            Console.WriteLine($"updated {Path.GetRelativePath(Environment.CurrentDirectory, snapshotPath)} ({cells.Count} cells)");
            return new(name, null);
        }
        if (!File.Exists(snapshotPath))
            return new(name, $"no snapshot at {snapshotPath}; run with --update-snapshots and review every cell against docs/spec");

        var expected = ReadSnapshot(snapshotPath);
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in cells)
        {
            seen.Add(cell.Name);
            if (!expected.TryGetValue(cell.Name, out var wanted))
            {
                problems.Add($"{cell.Name}: new cell, not in the snapshot");
                continue;
            }
            if (!wanted.SequenceEqual(cell.Lines))
                problems.Add($"{cell.Name}:\n  snapshot: {string.Join(" | ", wanted)}\n  actual:   {string.Join(" | ", cell.Lines)}");
        }
        foreach (var missing in expected.Keys.Where(k => !seen.Contains(k)))
            problems.Add($"{missing}: in the snapshot, but no cell");

        if (problems.Count == 0)
            return new(name, null);
        var shown = problems.Take(ShownDifferences).ToList();
        if (problems.Count > ShownDifferences)
            shown.Add($"... and {problems.Count - ShownDifferences} more (run with --update-snapshots to record them, then review the diff)");
        return new(name, string.Join('\n', shown));
    }

    static string Format(Matrix matrix, List<CellResult> cells)
    {
        var builder = new StringBuilder();
        builder.Append("# ").Append(matrix.Title).Append('\n');
        builder.Append("# Generated by Vpl.Tests --update-snapshots. Every cell was reviewed against docs/spec.\n");
        foreach (var cell in cells)
        {
            builder.Append("\n=== ").Append(cell.Name).Append('\n');
            foreach (var line in cell.Lines)
                builder.Append(line).Append('\n');
        }
        return builder.ToString();
    }

    static Dictionary<string, List<string>> ReadSnapshot(string path)
    {
        var cells = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? current = null;
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.StartsWith("=== ", StringComparison.Ordinal))
            {
                current = line[4..];
                cells[current] = [];
            }
            else if (current is not null && line.Length > 0)
            {
                cells[current].Add(line);
            }
        }
        return cells;
    }
}
