using Wyl.Compiler.Build;

// A compiler older than the oldest supported version is refused (decision 0098), so the version check
// must read each compiler's own way of printing its version.
static class ToolchainSelfTests
{
    public static List<TestResult> Run()
    {
        List<string> problems = [];
        void Expect(string? version, Version minimum, bool supported)
        {
            if (ToolchainVersions.IsSupported(version, minimum) != supported)
                problems.Add($"'{version}' against {minimum}: expected {(supported ? "supported" : "refused")}");
        }

        Expect("14.51.36231", ToolchainVersions.MsvcToolset, true);
        Expect("14.44.35207", ToolchainVersions.MsvcToolset, true);
        Expect("14.43.34808", ToolchainVersions.MsvcToolset, false);
        Expect("13.3.0", ToolchainVersions.Gcc, true);
        Expect("15.1.0", ToolchainVersions.Gcc, true);
        Expect("12.4.0", ToolchainVersions.Gcc, false);
        Expect("18", ToolchainVersions.LinuxClang, true);
        Expect("18.1.3", ToolchainVersions.LinuxClang, true);
        Expect("17.0.6", ToolchainVersions.LinuxClang, false);
        Expect("", ToolchainVersions.Gcc, false);
        Expect(null, ToolchainVersions.Gcc, false);
        Expect("unknown", ToolchainVersions.Gcc, false);
        return [new("toolchain/versions", problems.Count == 0 ? null : string.Join('\n', problems))];
    }
}
