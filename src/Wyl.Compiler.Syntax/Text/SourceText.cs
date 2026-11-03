namespace Wyl.Compiler.Text;

public sealed class SourceText
{
    readonly int[] lineStarts;

    /// <summary>
    /// A file of <paramref name="module"/>, named <paramref name="name"/> in messages and panics;
    /// without a module, a file compiled on its own, which is the one file of an unnamed package's
    /// one module (spec programs.packages), named without its directory.
    /// </summary>
    public SourceText(string path, string text, Module? module = null, string? name = null)
    {
        Path = path;
        Text = text;
        Module = module ?? Package.Unnamed().Root;
        Name = name ?? System.IO.Path.GetFileName(path);
        lineStarts = ComputeLineStarts(text);
        Module.Add(this);
    }

    public string Path { get; }

    /// <summary>The module the file belongs to, which decides what it sees and who sees it.</summary>
    public Module Module { get; }

    /// <summary>How messages and panics name the file: its path under the package's <c>src/</c>, as in <c>world/chunk.wyl</c>.</summary>
    public string Name { get; }

    public string Text { get; }

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
    /// Where it is as a panic reports it: the file's <see cref="SourceText.Name"/>, then the line and
    /// column (<c>runtime/wyl/core.h</c>), which the interpreter's messages use too.
    /// </summary>
    public string PanicText
    {
        get
        {
            var (line, column) = LineColumn;
            return $"{Source.Name}:{line}:{column}";
        }
    }
}
