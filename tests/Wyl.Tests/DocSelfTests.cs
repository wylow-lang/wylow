using System.Text.RegularExpressions;

// What decision 0004 asks of std's doc comments, and that the docs' links lead somewhere.
static partial class DocSelfTests
{
    const string Stdlib = "src/Wyl.Compiler.Semantics/Stdlib";

    [GeneratedRegex(@"^\s*pub\b(?!\s+import\b)")]
    private static partial Regex PublicPattern();

    [GeneratedRegex(@"\bpanic\(")]
    private static partial Regex PanicCallPattern();

    [GeneratedRegex(@"```.*?```", RegexOptions.Singleline)]
    private static partial Regex CodeBlockPattern();

    [GeneratedRegex(@"\]\(([^)\s]+)\)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex("""<a id="([^"]+)"></a>""")]
    private static partial Regex AnchorPattern();

    [GeneratedRegex(@"^#+ (.*)$", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();

    public static IEnumerable<TestResult> Run(string root)
    {
        yield return Result("docs/std-comments", StdComments(root));
        yield return Result("docs/links", Links(root));
    }

    static TestResult Result(string name, IEnumerable<string> problems) =>
        new(name, problems.ToList() is { Count: > 0 } list ? string.Join('\n', list) : null);

    /// <summary>
    /// Every <c>pub</c> declaration has a doc comment, and a function that calls <c>panic</c> or is
    /// marked <c>@CallerLocation</c> has a <c># Panics</c> section. A panic reached through another
    /// call is not seen here, so those sections rest on review.
    /// </summary>
    static IEnumerable<string> StdComments(string root)
    {
        var directory = Path.Combine(root, Stdlib);
        foreach (var path in Directory.GetFiles(directory, "*.wyl", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var lines = File.ReadAllText(path).ReplaceLineEndings("\n").Split('\n');
            var where = Path.GetRelativePath(root, path).Replace('\\', '/');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!PublicPattern().IsMatch(lines[i]))
                    continue;
                int above = i - 1;
                bool callerLocation = false;
                while (above >= 0 && lines[above].TrimStart().StartsWith('@'))
                {
                    callerLocation |= lines[above].Contains("@CallerLocation", StringComparison.Ordinal);
                    above--;
                }
                int comment = above;
                while (comment >= 0 && lines[comment].TrimStart().StartsWith("///", StringComparison.Ordinal))
                    comment--;
                if (comment == above)
                {
                    yield return $"{where}:{i + 1}: no doc comment: {lines[i].Trim()}";
                    continue;
                }
                bool documented = lines[(comment + 1)..(above + 1)].Any(l => l.Contains("# Panics", StringComparison.Ordinal));
                if (!documented && (callerLocation || PanicCallPattern().IsMatch(Body(lines, i))))
                    yield return $"{where}:{i + 1}: can panic, and has no '# Panics': {lines[i].Trim()}";
            }
        }
    }

    /// <summary>The lines of a function's body, up to the closing brace at its declaration's indent.</summary>
    static string Body(string[] lines, int declaration)
    {
        // A type's braces hold members, whose own comments say whether they panic.
        if (!lines[declaration].Contains('(') || !lines[declaration].TrimEnd().EndsWith('{'))
            return "";
        var indent = lines[declaration][..(lines[declaration].Length - lines[declaration].TrimStart().Length)];
        int end = declaration + 1;
        while (end < lines.Length && !lines[end].StartsWith(indent + "}", StringComparison.Ordinal))
            end++;
        return string.Join('\n', lines[(declaration + 1)..end]);
    }

    /// <summary>A relative link reaches a file, and an anchor in it. Code blocks hold no links.</summary>
    static IEnumerable<string> Links(string root)
    {
        var sources = Directory.GetFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(root, "*.md"))
            .Order(StringComparer.Ordinal);
        var anchors = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var path in sources)
        {
            var text = CodeBlockPattern().Replace(File.ReadAllText(path).ReplaceLineEndings("\n"), "");
            foreach (Match link in LinkPattern().Matches(text))
            {
                var target = link.Groups[1].Value;
                if (Regex.IsMatch(target, "^[a-z]+:"))
                    continue;
                var parts = target.Split('#', 2);
                var file = parts[0].Length == 0 ? path : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, parts[0]));
                var where = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (!File.Exists(file) && !Directory.Exists(file))
                    yield return $"{where}: '{target}' leads to no file";
                else if (parts.Length == 2 && file.EndsWith(".md", StringComparison.Ordinal) && !AnchorsOf(file, anchors).Contains(parts[1]))
                    yield return $"{where}: '{target}' leads to no anchor";
            }
        }
    }

    static HashSet<string> AnchorsOf(string file, Dictionary<string, HashSet<string>> known)
    {
        if (known.TryGetValue(file, out var found))
            return found;
        var text = File.ReadAllText(file).ReplaceLineEndings("\n");
        found = [.. AnchorPattern().Matches(text).Select(m => m.Groups[1].Value)];
        // A heading's own anchor, as GitHub makes it.
        foreach (Match heading in HeadingPattern().Matches(text))
            found.Add(Regex.Replace(heading.Groups[1].Value.ToLowerInvariant(), @"[^\w\- ]", "").Trim().Replace(' ', '-'));
        known[file] = found;
        return found;
    }
}
