using Wyl.Compiler.Syntax;

static class VersionSelfTests
{
    public static List<TestResult> Run() => [Parse(), Satisfies()];

    static TestResult Parse()
    {
        List<string> problems = [];
        void Accepted(string text, bool patchOptional, string expected)
        {
            if (PackageVersion.Parse(text, patchOptional) is not { } version || version.ToString() != expected)
                problems.Add($"'{text}' (patch {(patchOptional ? "optional" : "required")}) did not parse as {expected}");
        }
        void Rejected(string text, bool patchOptional)
        {
            if (PackageVersion.Parse(text, patchOptional) is { } version)
                problems.Add($"'{text}' (patch {(patchOptional ? "optional" : "required")}) parsed as {version}");
        }

        Accepted("1.4.2", false, "1.4.2");
        Accepted("0.0.0", false, "0.0.0");
        Accepted("12.34.56", false, "12.34.56");
        Accepted(new string('9', 40) + ".0.0", false, new string('9', 40) + ".0.0");
        Accepted("1.4", true, "1.4.0");
        Accepted("1.4.2", true, "1.4.2");
        Rejected("1.4.2.0", false);
        Rejected("1.4.2.0", true);
        Rejected("1", false);
        Rejected("1.4", false);
        Rejected("01.4.2", false);
        Rejected("1.04.2", false);
        Rejected("1.4.02", false);
        Rejected("1..2", false);
        Rejected("1.4.", false);
        Rejected(".1.4", false);
        Rejected("1.4.2 ", false);
        Rejected(" 1.4.2", false);
        Rejected("1.4.-1", false);
        Rejected("a.4.2", false);
        Rejected("", false);
        Rejected("01.4", true);
        return new("version/parse", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    static TestResult Satisfies()
    {
        List<string> problems = [];
        void Expect(string library, string stated, bool satisfies)
        {
            var actual = PackageVersion.Parse(library, false)!.Satisfies(PackageVersion.Parse(stated, true)!);
            if (actual != satisfies)
                problems.Add($"{library} {(satisfies ? "does not satisfy" : "satisfies")} {stated}");
        }

        Expect("1.4.0", "1.4", true);
        Expect("1.4.7", "1.4", true);
        Expect("1.9.0", "1.4", true);
        Expect("1.3.9", "1.4", false);
        Expect("2.0.0", "1.4", false);
        Expect("0.3.0", "0.3", true);
        Expect("0.3.5", "0.3", true);
        Expect("0.4.0", "0.3", false);
        Expect("1.4.2", "1.4.2", true);
        Expect("1.4.9", "1.4.2", true);
        Expect("1.5.0", "1.4.2", true);
        Expect("1.4.1", "1.4.2", false);
        Expect("0.3.4", "0.3.5", false);
        Expect("0.0.0", "0.0", true);
        Expect("0.0.1", "0.0", true);
        Expect("0.1.0", "0.0", false);
        return new("version/satisfies", problems.Count == 0 ? null : string.Join('\n', problems));
    }
}
