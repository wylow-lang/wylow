using Wyl.Compiler.Text;

// A column counts characters, code points, from 1 (spec lexical.source). Panics print it, so it is
// observable, and it must not count the two UTF-16 units of a character above U+FFFF twice.
static class SourceTextSelfTests
{
    public static List<TestResult> Run()
    {
        List<string> problems = [];
        void Expect(string text, int position, (int, int) expected)
        {
            var actual = new SourceText("columns.wyl", text).GetLineColumn(position);
            if (actual != expected)
                problems.Add($"{text.Replace("\n", "\\n")} at {position}: expected {expected}, found {actual}");
        }

        Expect("ab", 1, (1, 2));
        Expect("a\U0001F600b", 3, (1, 3));          // the emoji is one character, two UTF-16 units
        Expect("éx", 2, (1, 3));              // a combining mark is a character of its own
        Expect("\U0001F600\nx\U0001F600y", 5, (2, 3));
        return [new("source-text/columns", problems.Count == 0 ? null : string.Join('\n', problems))];
    }
}
