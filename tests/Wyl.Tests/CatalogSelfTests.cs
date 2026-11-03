using System.Reflection;
using System.Text.RegularExpressions;
using Wyl.Compiler;
using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Text;

// The diagnostics catalog's completeness (decision 0003): codes are unique and unretired, every
// anchor is a section of the spec, every code is produced by a test case, and every code has a page
// whose example produces exactly that code, or is on the list of codes still without one, which only
// shrinks (a code with a page must leave it; a code without must be on it).
static partial class CatalogSelfTests
{
    const string Pages = "docs/diagnostics";
    const string Unwritten = Pages + "/without-pages.txt";

    [GeneratedRegex("""<a id="([^"]+)"></a>""")]
    private static partial Regex AnchorPattern();

    [GeneratedRegex(@"error\[(WYL\d{4})\]")]
    private static partial Regex MarkerPattern();

    [GeneratedRegex(@"```wyl\r?\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex ExamplePattern();

    /// <summary>A block whose info string names its file, as in <c>```wyl src/main.wyl</c>: one file of a package example.</summary>
    [GeneratedRegex(@"```\w+ ([^\s`]+)\r?\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex PackageFilePattern();

    public static IEnumerable<TestResult> Run(string root)
    {
        yield return Result("catalog/codes", Codes());
        yield return Result("catalog/factories", Factories());
        yield return Result("catalog/anchors", Anchors(root));
        yield return Result("catalog/tests", Produced(root));
        yield return Result("catalog/pages", PageChecks(root));
    }

    static TestResult Result(string name, IEnumerable<string> problems) =>
        new(name, problems.ToList() is { Count: > 0 } list ? string.Join('\n', list) : null);

    static IEnumerable<string> Codes()
    {
        foreach (var group in Catalog.All.GroupBy(d => d.Number).Where(g => g.Count() > 1))
            yield return $"{group.First().Code} is given to {string.Join(", ", group.Select(d => d.Name))}";
        foreach (var descriptor in Catalog.All.Where(d => Catalog.Retired.Contains(d.Number)))
            yield return $"{descriptor.Code} ({descriptor.Name}) is retired and cannot be given again";
    }

    /// <summary>One method per diagnostic, so that a method's name is its identity.</summary>
    static IEnumerable<string> Factories()
    {
        var methods = typeof(Catalog).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.ReturnType == typeof(Diagnostic)).ToList();
        foreach (var group in methods.GroupBy(m => m.Name).Where(g => g.Count() > 1))
            yield return $"catalog method '{group.Key}' is overloaded";
        var described = Catalog.All.Select(d => d.Name).ToHashSet();
        foreach (var method in methods.Where(m => !described.Contains(m.Name)))
            yield return $"catalog method '{method.Name}' has no [Error]";
    }

    static IEnumerable<string> Anchors(string root)
    {
        var anchors = Directory.GetFiles(Path.Combine(root, "docs", "spec"), "*.md")
            .SelectMany(f => AnchorPattern().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToHashSet();
        foreach (var descriptor in Catalog.All.Where(d => !anchors.Contains(d.Anchor)))
            yield return $"{descriptor.Code} ({descriptor.Name}): the spec has no section '{descriptor.Anchor}'";
    }

    /// <summary>Every code is one a test case expects, which its error markers say.</summary>
    static IEnumerable<string> Produced(string root)
    {
        var expected = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "tests", "cases"), "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".wyl", StringComparison.Ordinal) || f.EndsWith(".wylcfg", StringComparison.Ordinal)))
        {
            foreach (Match marker in MarkerPattern().Matches(File.ReadAllText(file)))
                expected.Add(marker.Groups[1].Value);
        }
        foreach (var descriptor in Catalog.All.Where(d => !expected.Contains(d.Code)))
            yield return $"{descriptor.Code} ({descriptor.Name}) is expected by no test case";
        var known = Catalog.All.Select(d => d.Code).ToHashSet();
        foreach (var code in expected.Where(c => !known.Contains(c)).Order(StringComparer.Ordinal))
            yield return $"a test case expects {code}, which the catalog does not have";
    }

    static IEnumerable<string> PageChecks(string root)
    {
        var unwritten = File.ReadAllLines(Path.Combine(root, Unwritten))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet();
        var known = Catalog.All.Select(d => d.Code).ToHashSet();
        foreach (var code in unwritten.Where(c => !known.Contains(c)).Order(StringComparer.Ordinal))
            yield return $"{Unwritten} lists {code}, which the catalog does not have";
        foreach (var descriptor in Catalog.All.OrderBy(d => d.Number))
        {
            var page = Path.Combine(root, Pages, descriptor.Code + ".md");
            if (!File.Exists(page))
            {
                if (!unwritten.Contains(descriptor.Code))
                    yield return $"{descriptor.Code} ({descriptor.Name}) has no page {Pages}/{descriptor.Code}.md and is not in {Unwritten}";
                continue;
            }
            if (unwritten.Contains(descriptor.Code))
                yield return $"{descriptor.Code} has a page, so it must leave {Unwritten}";
            var text = File.ReadAllText(page);
            var files = PackageFilePattern().Matches(text);
            PackageSources sources;
            if (files.Count > 0)
            {
                sources = WritePackage(descriptor.Code, files);
            }
            else if (ExamplePattern().Match(text) is { Success: true } example)
            {
                sources = PackageSources.Of(new SourceText(page, example.Groups[1].Value));
            }
            else
            {
                yield return $"{descriptor.Code}: the page has no ```wyl example";
                continue;
            }
            var result = Compilation.Lower(sources);
            var codes = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Code).Distinct().Order(StringComparer.Ordinal).ToList();
            if (!codes.SequenceEqual([descriptor.Code]))
                yield return $"{descriptor.Code}: its example produces {(codes.Count == 0 ? "no error" : string.Join(", ", codes))}";
        }
    }

    /// <summary>
    /// A page's package example, for the codes only a package can produce: each block naming its
    /// file is written there, under a directory of the code's own, and the package read from it.
    /// </summary>
    static PackageSources WritePackage(string code, MatchCollection files)
    {
        var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wyl-diagnostic-pages", code));
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        foreach (Match file in files)
        {
            var path = Path.GetFullPath(Path.Combine(directory, file.Groups[1].Value));
            if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException($"{code}: example file '{file.Groups[1].Value}' is outside its package");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Groups[2].Value);
        }
        return PackageSources.Load(directory);
    }
}
