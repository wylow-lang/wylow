using Wyl.Compiler.Build;

static class SanitizerSelfTests
{
    public static List<TestResult> Run()
    {
        List<string> problems = [];
        void Expect(string standardError, bool reported)
        {
            var actual = Toolchains.SanitizerReport(standardError) is not null;
            if (actual != reported)
                problems.Add($"{standardError.Replace("\n", "\\n")}: expected {(reported ? "a report" : "no report")}, found {(actual ? "a report" : "no report")}");
        }

        Expect("==3050==ERROR: AddressSanitizer: heap-use-after-free on address 0x602000000010", true);
        Expect("race.wyl:5:64: runtime error: signed integer overflow: 2147483647 + 1", true);
        Expect("WARNING: ThreadSanitizer: data race (pid=3050)\n  Write of size 4 at 0x556af08fa758 by thread T2:", true);
        Expect("WARNING: ThreadSanitizer: thread leak", true);
        Expect("ThreadSanitizer: reported 1 warnings", false);
        Expect("", false);
        Expect("hello", false);
        return [new("sanitizer/reports", problems.Count == 0 ? null : string.Join('\n', problems))];
    }
}
