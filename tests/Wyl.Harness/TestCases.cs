using Wyl.Compiler;

namespace Wyl.Harness;

/// <summary>
/// The test cases under <c>tests/cases/</c>: each <c>.wyl</c> file is a program, except the files of a
/// package directory (one with a <c>package.wylcfg</c>, spec 03-programs.md#programs.packages), which
/// together are one. The runner runs both; the fuzzer and the generator work on single files.
/// </summary>
public static class TestCases
{
    /// <summary>Every case, a file or a package directory, by full path, in a stable order.</summary>
    public static List<string> All(string casesDirectory) =>
        [.. SingleFiles(casesDirectory).Concat(Packages(casesDirectory)).Order(StringComparer.Ordinal)];

    public static IEnumerable<string> Packages(string casesDirectory) =>
        Directory.GetDirectories(casesDirectory, "*", SearchOption.AllDirectories).Where(PackageSources.IsPackage);

    /// <summary>The <c>.wyl</c> files that are programs on their own, outside every package directory.</summary>
    public static IEnumerable<string> SingleFiles(string casesDirectory)
    {
        var packages = Packages(casesDirectory).Select(p => p + Path.DirectorySeparatorChar).ToList();
        return Directory.GetFiles(casesDirectory, "*.wyl", SearchOption.AllDirectories)
            .Where(f => !packages.Any(p => f.StartsWith(p, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal);
    }
}
