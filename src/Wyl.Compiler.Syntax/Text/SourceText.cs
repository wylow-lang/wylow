namespace Wyl.Compiler.Text;

public sealed class SourceText
{
    readonly int[] lineStarts;

    /// <summary>
    /// A file of <paramref name="module"/>; without one, a file compiled on its own, which is the
    /// one file of an unnamed package's one module (spec 06-syntax.md §6.1).
    /// </summary>
    public SourceText(string path, string text, Module? module = null)
    {
        Path = path;
        Text = text;
        Module = module ?? Package.Unnamed().Root;
        lineStarts = ComputeLineStarts(text);
    }

    public string Path { get; }

    /// <summary>The module the file belongs to, which decides what it sees and who sees it.</summary>
    public Module Module { get; }
    public string Text { get; }
    public string FileName => System.IO.Path.GetFileName(Path);

    public static SourceText FromFile(string path) => new(path, File.ReadAllText(path));

    public (int Line, int Column) GetLineColumn(int position)
    {
        int line = Array.BinarySearch(lineStarts, position);
        if (line < 0)
            line = ~line - 1;
        return (line + 1, position - lineStarts[line] + 1);
    }

    static int[] ComputeLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                starts.Add(i + 1);
        }
        return [.. starts];
    }
}

public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;

    public static TextSpan FromBounds(int start, int end) => new(start, Math.Max(end - start, 0));
}

public readonly record struct Location(SourceText Source, TextSpan Span)
{
    public (int Line, int Column) LineColumn => Source.GetLineColumn(Span.Start);

    /// <summary>
    /// Where it is as a panic reports it: the file without its directory, then the line and column
    /// (<c>runtime/wyl/core.h</c>), which the interpreter's messages use too.
    /// </summary>
    public string PanicText
    {
        get
        {
            var (line, column) = LineColumn;
            return $"{Source.FileName}:{line}:{column}";
        }
    }
}
