using Vpl.Compiler.Text;

namespace Vpl.Compiler;

/// <summary>The VPL source compiled with every program, which declares standard types such as <c>Result</c>.</summary>
public static class Prelude
{
    public static SourceText Source { get; } = Load();

    static SourceText Load()
    {
        using var stream = typeof(Prelude).Assembly.GetManifestResourceStream("Vpl.Compiler.Prelude.vpl")
            ?? throw new InvalidOperationException("the prelude is missing from the compiler");
        using var reader = new StreamReader(stream);
        return new SourceText("prelude.vpl", reader.ReadToEnd());
    }
}
