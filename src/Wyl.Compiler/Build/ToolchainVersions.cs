using System.Globalization;
using System.Text.RegularExpressions;

namespace Wyl.Compiler.Build;

/// <summary>
/// The oldest version of each C++ compiler that builds programs (decision 0098). Each is the oldest
/// version that CI runs; <c>docs/design/runtime.md</c> lists them with the platforms.
/// </summary>
public static partial class ToolchainVersions
{
    /// <summary>The MSVC toolset, as <c>VCToolsVersion</c> names it. clang-cl comes from the same Visual Studio installation.</summary>
    public static readonly Version MsvcToolset = new(14, 44);

    /// <summary>g++, on Windows and on Linux.</summary>
    public static readonly Version Gcc = new(13, 0);

    /// <summary>clang++ on Linux, which builds the ThreadSanitizer mode.</summary>
    public static readonly Version LinuxClang = new(18, 0);

    /// <summary>
    /// Whether <paramref name="version"/>, as a compiler prints it ("13.3.0", "18", "14.51.36231"),
    /// is at least <paramref name="minimum"/>. A version that does not start with a number is not.
    /// </summary>
    public static bool IsSupported(string? version, Version minimum)
    {
        if (version is null || LeadingNumbers().Match(version) is not { Success: true } match)
            return false;
        var parts = match.Value.Split('.').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToList();
        while (parts.Count < 2)
            parts.Add(0);
        return new Version(parts[0], parts[1]) >= minimum;
    }

    [GeneratedRegex(@"^\d{1,9}(?:\.\d{1,9})?")]
    private static partial Regex LeadingNumbers();
}
