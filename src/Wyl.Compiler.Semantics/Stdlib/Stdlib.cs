using Wyl.Compiler.Text;

namespace Wyl.Compiler;

/// <summary>
/// The standard library: Wylow source in <c>Stdlib/*.wyl</c>, compiled with every program. The
/// compiler knows its types only through <see cref="Semantics.StdlibSymbols"/>.
/// </summary>
public static class Stdlib
{
    /// <summary>The files of the package <c>std</c>, so whether code is the standard library's is <c>Module.Package.IsStd</c>.</summary>
    public static IReadOnlyList<SourceText> Sources { get; } = Load();

    static List<SourceText> Load()
    {
        const string prefix = "Wyl.Compiler.Stdlib.";
        var assembly = typeof(Stdlib).Assembly;
        var result = new List<SourceText>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix) && n.EndsWith(".wyl")).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            result.Add(new SourceText(name[prefix.Length..], reader.ReadToEnd(), Package.Std.Root));
        }
        if (result.Count == 0)
            throw new InvalidOperationException("the standard library is missing from the compiler");
        return result;
    }
}
