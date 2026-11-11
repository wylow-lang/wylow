using System.Text.RegularExpressions;
using Wyl.Compiler;
using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Text;

// The spec's code produces exactly the errors and warnings it marks (introduction.normative). A block's
// info string says how it is checked: `java` is a file, `java statements` is the body of main, and
// `java unchecked` is not compiled, for what the compiler doesn't implement yet or a declaration of
// std itself. Leaving a block out takes that word, so what isn't checked can be listed with a grep.
static partial class SpecExampleSelfTests
{
    const string Chapters = "docs/spec";

    /// <summary>The declarations a block may assume, each added only if the block uses it and declares no such name.</summary>
    static readonly (string Name, string Declaration)[] Assumed =
    [
        ("Point", "struct Point { pub int x; pub int y; }"),
        ("Node", "class Node { pub int value; pub mut Node? next; }"),
        ("Order", "class Order { pub int id; pub int quantity; }"),
        ("Direction", "enum Direction { North, East, South, West }"),
        ("Pair", "struct Pair<A, B> { pub A first; pub B second; }"),
        ("Option", "enum Option<T> { None, Some(T value) }"),
        ("log", "void log(String text) { }"),
    ];

    [GeneratedRegex(@"```java(?: (\w+))?\r?\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex BlockPattern();

    [GeneratedRegex(@"(?:error|warning)\[(WYL\d{4})\]")]
    private static partial Regex MarkerPattern();

    [GeneratedRegex(@"\b(struct|class|interface|enum)(\s+\w+[^{]*)\{\s*\.\.\.\s*\}")]
    private static partial Regex ElidedTypePattern();

    [GeneratedRegex(@"\b(void|int)\s+main\s*\(")]
    private static partial Regex MainPattern();

    [GeneratedRegex(@"^\s*import\s", RegexOptions.Multiline)]
    private static partial Regex ImportPattern();

    public static IEnumerable<TestResult> Run(string root)
    {
        var problems = Check(root).ToList();
        yield return new("spec/examples", problems.Count > 0 ? string.Join('\n', problems) : null);
    }

    static IEnumerable<string> Check(string root) =>
        Directory.GetFiles(Path.Combine(root, Chapters), "*.md").Order(StringComparer.Ordinal)
            .SelectMany(path =>
            {
                var text = File.ReadAllText(path).ReplaceLineEndings("\n");
                return BlockPattern().Matches(text).Select(block => (Path: path, Text: text, Block: block));
            })
            .AsParallel()
            .AsOrdered()
            .SelectMany(found => CheckFound(found.Path, found.Text, found.Block).ToList())
            .ToList();

    static IEnumerable<string> CheckFound(string path, string text, Match block)
    {
        var mode = block.Groups[1].Value;
        if (mode == "unchecked")
            yield break;
        var where = $"{Chapters}/{Path.GetFileName(path)}:{text.AsSpan(0, block.Index).Count('\n') + 1}";
        if (mode is not ("" or "statements"))
        {
            yield return $"{where}: unknown info string 'java {mode}'";
            yield break;
        }
        foreach (var problem in CheckBlock(block.Groups[2].Value, mode == "statements"))
            yield return $"{where}: {problem}";
    }

    /// <summary>
    /// Compiles the block, then again without each line whose marked errors were reported, since an
    /// earlier stage's errors keep a later stage from running. Removing a line can break others, so
    /// only the first compile may fail on an error the block doesn't mark.
    /// </summary>
    static IEnumerable<string> CheckBlock(string body, bool statements)
    {
        var lines = body.TrimEnd('\n').Split('\n');
        var wanted = new HashSet<(int Line, string Code)>();
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Match marker in MarkerPattern().Matches(lines[i]))
                wanted.Add((i + 1, marker.Groups[1].Value));
        }

        var remaining = new HashSet<(int Line, string Code)>(wanted);
        for (int pass = 0; ; pass++)
        {
            var reported = Errors(lines, statements);
            if (pass == 0)
            {
                foreach (var error in reported.Where(e => !wanted.Contains((e.Line, e.Code))))
                    yield return $"line {error.Line} of the block reports {error.Code}, unmarked: {error.Message}";
            }
            var found = reported.Select(e => (e.Line, e.Code)).Where(remaining.Contains).ToList();
            if (found.Count == 0)
                break;
            remaining.ExceptWith(found);
            foreach (var (line, _) in found)
                lines[line - 1] = "";
        }
        foreach (var (line, code) in remaining.Order())
            yield return $"line {line} of the block marks {code}, which is not reported";
    }

    static List<(int Line, string Code, string Message)> Errors(string[] lines, bool statements)
    {
        var text = string.Join('\n', lines.Select(Elide));
        var used = string.Join('\n', lines);
        // On the first line, so a line of the program is a line of the block.
        var head = ImportPattern().IsMatch(text) ? "" : "import std::{memory, panic}; import std::collections::List; ";
        var source = head + (statements ? "void main() { " : "") + text + (statements ? "\n}" : "");
        if (!statements && !MainPattern().IsMatch(text))
            source += "\nvoid main() { }";
        foreach (var (name, declaration) in Assumed)
        {
            if (Regex.IsMatch(used, $@"\b{name}\b") && !Regex.IsMatch(used, $@"\b(struct|class|enum|interface)\s+{name}\b|\bvoid\s+{name}\s*\("))
                source += "\n" + declaration;
        }

        return Compilation.Lower(new SourceText("example.wyl", source), DiagnosticsOnly.Options).Diagnostics
            .Select(d => (d.Location.LineColumn.Line, d.Code, d.Message))
            .Where(e => e.Line <= lines.Length)
            .ToList();
    }

    /// <summary>Code left out (introduction.notation.examples): a type with no members, or a body that can't be reached past.</summary>
    static string Elide(string line) =>
        ElidedTypePattern().Replace(line, "$1$2{ }").Replace("...", "std::panic(\"\");");
}
