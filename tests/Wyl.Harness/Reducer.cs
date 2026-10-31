using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Harness;

/// <summary>Deletes statements and declarations while the same failure signature remains.</summary>
public static class Reducer
{
    public static string Reduce(string text, Func<string, string?> check, string signature, int maxChecks, TimeSpan budget)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var current = text;
        int checks = 0;
        // Each accepted deletion re-parses, so no span is ever used after an earlier deletion shifted it.
        while (checks < maxChecks && stopwatch.Elapsed <= budget)
        {
            bool changed = false;
            foreach (var span in Spans(current).OrderByDescending(s => s.Start))
            {
                if (checks >= maxChecks || stopwatch.Elapsed > budget)
                    return current;
                checks++;
                var candidate = current.Remove(span.Start, span.Length);
                if (check(candidate) != signature)
                    continue;
                current = candidate;
                changed = true;
                break;
            }
            if (!changed)
                break;
        }
        return current;
    }

    static List<TextSpan> Spans(string text)
    {
        var source = new SourceText("<fuzz>", text);
        var unit = new Parser(source, new DiagnosticBag()).ParseCompilationUnit();
        return
        [
            .. AstIndex.Of(unit).Deletable
                .Where(span => span.Length > 0 && span.Start >= 0 && span.End <= text.Length)
                // A simple statement's node ends before its ';', which would otherwise be left behind.
                .Select(span => span.End < text.Length && text[span.End] == ';' ? new TextSpan(span.Start, span.Length + 1) : span)
                .Distinct(),
        ];
    }
}
