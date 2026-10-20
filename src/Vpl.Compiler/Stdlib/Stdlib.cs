using Vpl.Compiler.Text;

namespace Vpl.Compiler;

/// <summary>
/// The standard library: VPL source in <c>Stdlib/*.vpl</c>, compiled with every program. The
/// compiler knows its types only through <see cref="Semantics.StdlibSymbols"/>.
/// </summary>
public static class Stdlib
{
    public static IReadOnlyList<SourceText> Sources { get; } = Load();

    public static bool Contains(SourceText source) => Sources.Contains(source);

    static List<SourceText> Load()
    {
        const string prefix = "Vpl.Compiler.Stdlib.";
        var assembly = typeof(Stdlib).Assembly;
        var result = new List<SourceText>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix) && n.EndsWith(".vpl")).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            result.Add(new SourceText(name[prefix.Length..], reader.ReadToEnd()));
        }
        if (result.Count == 0)
            throw new InvalidOperationException("the standard library is missing from the compiler");
        return result;
    }
}
