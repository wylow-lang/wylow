using System.Text;

namespace Wyl.Generate;

/// <summary>Writes indented source, so every line of a program comes from one formatting path.</summary>
sealed class SourceWriter
{
    readonly StringBuilder text = new();
    int indent;

    public void Line(string line) => text.Append(' ', indent * 4).Append(line).Append('\n');

    public void Open(string header)
    {
        Line(header);
        indent++;
    }

    /// <summary>Closes a block and opens the next on the same line: <c>} else {</c>.</summary>
    public void Continue(string header)
    {
        indent--;
        Line("} " + header);
        indent++;
    }

    public void Close()
    {
        indent--;
        Line("}");
    }

    public void Blank() => text.Append('\n');

    public override string ToString() => text.ToString();
}
