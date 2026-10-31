using Wyl.Compiler.Text;

namespace Wyl.Compiler;

/// <summary>
/// The standard library: Wylow source in <c>Stdlib/*.wyl</c>, compiled with every program. The
/// compiler knows its types only through <see cref="Semantics.StdlibSymbols"/>.
/// </summary>
public static class Stdlib
{
    public static IReadOnlyList<SourceText> Sources { get; } = Load();

    public static bool Contains(SourceText source) => Sources.Contains(source);

    static List<SourceText> Load()
    {
        const string prefix = "Wyl.Compiler.Stdlib.";
        var assembly = typeof(Stdlib).Assembly;
        var result = new List<SourceText>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix) && n.EndsWith(".wyl")).Order(StringComparer.Ordinal))
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
