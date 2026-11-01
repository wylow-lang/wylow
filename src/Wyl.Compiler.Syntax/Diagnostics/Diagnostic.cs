using System.Collections;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Diagnostics;

public enum DiagnosticSeverity { Error, Warning }

public sealed record Diagnostic(DiagnosticSeverity Severity, Location Location, string Message)
{
    public int Line => Location.LineColumn.Line;

    public override string ToString()
    {
        var (line, column) = Location.LineColumn;
        var severity = Severity == DiagnosticSeverity.Error ? "error" : "warning";
        return $"{Location.Source.Path}:{line}:{column}: {severity}: {Message}";
    }
}

public sealed class DiagnosticBag : IEnumerable<Diagnostic>
{
    readonly List<Diagnostic> diagnostics = [];

    public int Count => diagnostics.Count;
    public bool HasErrors => diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public void Error(Location location, string message) =>
        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, location, message));

    public void AddRange(IEnumerable<Diagnostic> reported) => diagnostics.AddRange(reported);

    public IReadOnlyList<Diagnostic> Sorted() =>
        [.. diagnostics.OrderBy(d => d.Location.Source.Path, StringComparer.Ordinal).ThenBy(d => d.Location.Span.Start)];

    public IEnumerator<Diagnostic> GetEnumerator() => diagnostics.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
