using System.Collections;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Diagnostics;

public enum DiagnosticSeverity { Error, Warning }

/// <summary>
/// What a catalog entry declares: its code, severity, and spec section, and for a warning the name
/// that <c>@SuppressWarnings</c> turns it off by.
/// </summary>
public sealed record DiagnosticDescriptor(int Number, DiagnosticSeverity Severity, string Anchor, string Name, string? WarningName = null)
{
    public string Code => $"WYL{Number:D4}";
}

public enum AttachedKind { Note, Help }

/// <summary>A <c>note:</c> or <c>help:</c> line; without a location of its own it is shown at the primary one.</summary>
public sealed record AttachedLine(AttachedKind Kind, Location? Location, string Message);

/// <summary>
/// A compiler error or warning. Only <see cref="Catalog"/> makes one, so every diagnostic has a code,
/// a fixed severity, and a spec section (decision 0003).
/// </summary>
public sealed class Diagnostic
{
    internal Diagnostic(DiagnosticDescriptor descriptor, Location location, string message, IReadOnlyList<AttachedLine> attached)
    {
        Descriptor = descriptor;
        Location = location;
        Message = message;
        Attached = attached;
    }

    public DiagnosticDescriptor Descriptor { get; }
    public Location Location { get; }
    public string Message { get; }
    public IReadOnlyList<AttachedLine> Attached { get; }

    public DiagnosticSeverity Severity => Descriptor.Severity;
    public string Code => Descriptor.Code;
    public int Line => Location.LineColumn.Line;

    internal Diagnostic Note(Location? at, string message) => With(new AttachedLine(AttachedKind.Note, at, message));
    internal Diagnostic Note(string message) => Note(null, message);
    internal Diagnostic Help(Location? at, string message) => With(new AttachedLine(AttachedKind.Help, at, message));
    internal Diagnostic Help(string message) => Help(null, message);

    Diagnostic With(AttachedLine line) => new(Descriptor, Location, Message, [.. Attached, line]);

    /// <summary>The primary line alone: <c>path:line:column: error[WYL0001]: message</c>.</summary>
    public string Primary
    {
        get
        {
            var severity = Severity == DiagnosticSeverity.Error ? "error" : "warning";
            return $"{Format(Location)}: {severity}[{Code}]: {Message}";
        }
    }

    public override string ToString()
    {
        if (Attached.Count == 0)
            return Primary;
        var lines = new List<string> { Primary };
        foreach (var line in Attached)
            lines.Add($"{Format(line.Location ?? Location)}: {(line.Kind == AttachedKind.Note ? "note" : "help")}: {line.Message}");
        return string.Join(Environment.NewLine, lines);
    }

    static string Format(Location location)
    {
        var (line, column) = location.LineColumn;
        return $"{location.Source.Path}:{line}:{column}";
    }
}

public sealed class DiagnosticBag : IEnumerable<Diagnostic>
{
    readonly List<Diagnostic> diagnostics = [];

    /// <summary>The errors reported so far, which a warning never adds to, so a check of whether binding something failed counts these.</summary>
    public int ErrorCount { get; private set; }

    public bool HasErrors => ErrorCount > 0;

    public void Add(Diagnostic diagnostic)
    {
        diagnostics.Add(diagnostic);
        if (diagnostic.Severity == DiagnosticSeverity.Error)
            ErrorCount++;
    }

    public void AddRange(IEnumerable<Diagnostic> reported)
    {
        foreach (var diagnostic in reported)
            Add(diagnostic);
    }

    public IReadOnlyList<Diagnostic> Sorted() =>
        [.. diagnostics.OrderBy(d => d.Location.Source.Path, StringComparer.Ordinal).ThenBy(d => d.Location.Span.Start)];

    public IEnumerator<Diagnostic> GetEnumerator() => diagnostics.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
