using System.Globalization;
using System.Text.RegularExpressions;
using Wyl.Compiler.CodeGen;
using Wyl.Compiler.Diagnostics;

namespace Wyl.Compiler.Build;

/// <summary>
/// Whose mistake a failed C++ build is. The program passed every Wylow check, so a failure in the
/// generated C++ is a compiler bug, except where a header or a native source does not match the
/// program's extern blocks (spec <c>interop</c>). A failed signature or layout check is reported as
/// that check's diagnostic, and another error on the lines that an extern declaration produced is
/// reported at the declaration.
/// </summary>
static partial class BuildFailure
{
    /// <summary>An error line of MSVC or clang-cl, <c>file(line,column): error ...: text</c>, or of gcc, <c>file:line:column: error: text</c>.</summary>
    [GeneratedRegex(@"^(?<file>.+?)(?:\((?<line>\d+)(?:,\d+)?\)|:(?<line>\d+):\d+):\s*(?:fatal\s+)?error\b[^:]*:\s*(?<message>.*)$", RegexOptions.Multiline)]
    private static partial Regex ErrorLine();

    [GeneratedRegex(@"unresolved external symbol|undefined reference to|undefined symbol")]
    private static partial Regex UnresolvedSymbol();

    [GeneratedRegex(@"wyl-interop-check (\d+)")]
    private static partial Regex Check();

    public static ExecutableBuild Classify(string output, string cppPath, CppInterop interop, string? nativeDirectory)
    {
        var errors = ErrorLine().Matches(output).Select(m => (File: m.Groups["file"].Value.Trim(), Line: int.Parse(m.Groups["line"].Value, CultureInfo.InvariantCulture), Message: m.Groups["message"].Value.Trim())).ToList();
        var cppName = Path.GetFileName(cppPath);
        // An error other than a failed check on a region's lines is the C++ compiler's rejection of
        // its declaration, which replaces the region's checks, since they fail after it.
        var rejected = new SortedDictionary<int, Diagnostic>();
        foreach (var error in errors.Where(e => Path.GetFileName(e.File).Equals(cppName, StringComparison.OrdinalIgnoreCase) && !Check().IsMatch(e.Message)))
        {
            int index = interop.Regions.ToList().FindIndex(r => r.FirstLine <= error.Line && error.Line <= r.LastLine);
            if (index >= 0 && !rejected.ContainsKey(index))
                rejected[index] = Catalog.CppRejected(interop.Regions[index].Declaration, interop.Regions[index].What, error.Message);
        }
        var failed = Check().Matches(output).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Where(i => i < interop.Checks.Count && !rejected.ContainsKey(interop.Checks[i].Region)).Distinct().Order()
            .Select(i => interop.Checks[i].Diagnostic);
        var diagnostics = rejected.Values.Concat(failed).ToList();
        if (diagnostics.Count > 0)
            return new ExecutableBuild(null, null) { Diagnostics = diagnostics };

        // An error in a header or source of native/ is the package's, and so is a C++ function that
        // an extern block declares and nothing defines.
        if (nativeDirectory is not null && errors.Any(e => IsUnder(e.File, nativeDirectory)))
            return new ExecutableBuild(null, $"error: the package's native sources do not compile\n{output}");
        if (interop.Regions.Count > 0 && UnresolvedSymbol().IsMatch(output))
            return new ExecutableBuild(null, $"error: the program does not link, since a C++ function that an extern block declares is not defined\n{output}");
        return new ExecutableBuild(null, $"internal compiler error: the generated C++ failed to compile ({cppPath})\n{output}");
    }

    static bool IsUnder(string file, string directory)
    {
        try
        {
            var full = Path.GetFullPath(file, Path.GetDirectoryName(directory)!);
            return full.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
