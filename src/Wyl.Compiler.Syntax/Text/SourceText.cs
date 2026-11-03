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

    /// <summary>
    /// Where in <see cref="Text"/> the file's bytes first stop being UTF-8, which the lexer reports
    /// (spec lexical.source); the rest is decoded with U+FFFD for each invalid sequence. Null for text
    /// that was never bytes, or that was valid.
    /// </summary>
    public int? InvalidUtf8At { get; private init; }

    /// <summary>Reads a source file or a manifest: UTF-8, without a byte order mark if it starts with one.</summary>
    public static SourceText FromFile(string path, Module? module = null, string? name = null)
    {
        var (text, invalidAt) = DecodeUtf8(File.ReadAllBytes(path));
        return new SourceText(path, text, module, name) { InvalidUtf8At = invalidAt };
    }

    static (string Text, int? InvalidAt) DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("﻿"u8))
            bytes = bytes[3..];
        // UTF-16 never needs more code units than UTF-8 has bytes.
        var chars = new char[bytes.Length];
        var status = System.Text.Unicode.Utf8.ToUtf16(bytes, chars, out int read, out int written, replaceInvalidSequences: false);
        if (status == System.Buffers.OperationStatus.Done)
            return (new string(chars, 0, written), null);
        return (new string(chars, 0, written) + System.Text.Encoding.UTF8.GetString(bytes[read..]), written);
    }

    /// <summary>The line and column of a position, both from 1; a column counts characters (code points), not UTF-16 units (spec lexical.source).</summary>
    public (int Line, int Column) GetLineColumn(int position)
    {
        int line = Array.BinarySearch(lineStarts, position);
        if (line < 0)
            line = ~line - 1;
        int column = 1;
        for (int i = lineStarts[line]; i < position && i < Text.Length; i++)
        {
            // The second half of a surrogate pair is part of the character the first half started.
            if (!(char.IsLowSurrogate(Text[i]) && i > lineStarts[line] && char.IsHighSurrogate(Text[i - 1])))
                column++;
        }
        return (line + 1, column);
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
