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

    /// <summary>MSVC's link.exe, lld-link, and GNU ld, each failing to find a library it was told to link.</summary>
    [GeneratedRegex(@"LNK1181|could not open '[^']*\.lib'|cannot find -l")]
    private static partial Regex MissingLibrary();

    public static ExecutableBuild Classify(string output, string cppPath, CppInterop interop, NativeInputs native)
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

        // An error in a header or source of the package's include directories is the package's, and
        // so are a library that the manifest names and the linker cannot find, and a symbol that
        // nothing the program links defines.
        if (errors.Any(e => native.IncludeDirectories.Any(d => IsUnder(e.File, d))))
            return new ExecutableBuild(null, $"error: the package's native sources do not compile\n{output}");
        if (native.Link.Count > 0 && MissingLibrary().IsMatch(output))
            return new ExecutableBuild(null, $"error: the program does not link, since a library that the manifest's 'link' names was not found\n{output}");
        if (Unlinked(output, cppPath, interop, native) is { } unlinked)
            return unlinked;
        return new ExecutableBuild(null, $"internal compiler error: the generated C++ failed to compile ({cppPath})\n{output}");
    }

    /// <summary>
    /// A link that failed because a symbol is not defined. A symbol that an extern declaration names is
    /// reported at the declaration. Any other symbol that a native source or a library uses is the
    /// package's error, which names the symbol and its user. Null if the generated program uses a
    /// symbol that no extern declaration names, which is a compiler bug.
    /// </summary>
    static ExecutableBuild? Unlinked(string output, string cppPath, CppInterop interop, NativeInputs native)
    {
        var missing = MissingSymbols(output);
        if (missing.Count == 0)
        {
            bool packageLinks = interop.Regions.Count > 0 || native.Sources.Count > 0 || native.Link.Count > 0;
            return packageLinks && UnresolvedSymbol().IsMatch(output)
                ? new ExecutableBuild(null, $"error: the program does not link, since a symbol that it uses is not defined\n{output}")
                : null;
        }
        var declared = new SortedDictionary<int, Diagnostic>();
        List<string> used = [];
        foreach (var (symbol, user) in missing)
        {
            int index = interop.Regions.ToList().FindIndex(r => r.Symbol is { } name && Names(symbol, name));
            if (index >= 0)
            {
                var region = interop.Regions[index];
                declared.TryAdd(index, Catalog.UndefinedExtern(region.Declaration, region.What, region.Symbol!));
                continue;
            }
            if (UserOf(user, cppPath, native) is not { } source)
                return null;
            var line = $"  '{symbol}', which {source} uses";
            if (!used.Contains(line))
                used.Add(line);
        }
        if (declared.Count > 0)
            return new ExecutableBuild(null, null) { Diagnostics = [.. declared.Values] };
        return new ExecutableBuild(null,
            $"error: the program does not link, since nothing that it links defines {(used.Count == 1 ? "this symbol" : "these symbols")}:\n"
            + string.Join('\n', used)
            + "\nhelp: name the library that defines each one in the 'link' list of the manifest's 'native' block\n"
            + output);
    }

    /// <summary>
    /// Each symbol that the linker could not find, with the object or source file that uses it if the
    /// linker says, as MSVC's link.exe, lld-link, and GNU ld report them. A symbol keeps its demangled
    /// form, without the <c>__imp_</c> prefix or <c>__declspec(dllimport)</c> of a DLL's import.
    /// </summary>
    internal static List<(string Symbol, string? User)> MissingSymbols(string output)
    {
        List<(string, string?)> missing = [];
        foreach (Match m in MsvcUnresolved().Matches(output))
            missing.Add((Symbol(m.Groups["symbol"].Value), m.Groups["user"].Value.Trim()));
        foreach (Match m in LldUnresolved().Matches(output))
            missing.Add((Symbol(m.Groups["symbol"].Value), m.Groups["user"].Success ? m.Groups["user"].Value : null));
        foreach (Match m in LdUnresolved().Matches(output))
            missing.Add((Symbol(m.Groups["symbol"].Value), m.Groups["user"].Value));
        return missing;

        static string Symbol(string text)
        {
            text = text.Trim().Trim('"').Trim();
            if (text.StartsWith("__declspec(dllimport) ", StringComparison.Ordinal))
                text = text["__declspec(dllimport) ".Length..];
            return text.StartsWith("__imp_", StringComparison.Ordinal) ? text["__imp_".Length..] : text;
        }
    }

    /// <summary>Whether a symbol as the linker reports it is the C++ name <paramref name="name"/>, with or without its signature.</summary>
    static bool Names(string symbol, string name) =>
        Regex.IsMatch(symbol, $@"(?<![\w:]){Regex.Escape(name)}(?![\w:])");

    /// <summary>
    /// The native source or library file that uses a missing symbol, as a message names it, or null if
    /// the generated program uses it. A native source is named by its path under <c>native/</c>.
    /// </summary>
    static string? UserOf(string? user, string cppPath, NativeInputs native)
    {
        if (string.IsNullOrEmpty(user))
            return "the program";
        var file = Path.GetFileName(user);
        if (Path.GetFileNameWithoutExtension(file).Equals(Path.GetFileNameWithoutExtension(cppPath), StringComparison.OrdinalIgnoreCase))
            return null;
        var sources = native.Sources;
        // An object that the build compiled from a native source is named '{index}_{name}.obj'.
        var match = NativeObject().Match(file);
        string? source = match.Success && int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture) is var index && index < sources.Count
            ? sources[index]
            : sources.FirstOrDefault(s => Path.GetFileName(s).Equals(file, StringComparison.OrdinalIgnoreCase));
        if (source is not null && native.Directory is not null)
            return $"native source '{Path.GetRelativePath(native.Directory, source).Replace('\\', '/')}'";
        return $"'{file}'";
    }

    /// <summary>link.exe: <c>user.obj : error LNK2019: unresolved external symbol "int __cdecl f(int)" (?f@@YAHH@Z) referenced in ...</c>, or LNK2001 without the user's function.</summary>
    [GeneratedRegex("""^(?<user>.+?) : error LNK20(?:19|01): unresolved external symbol (?<symbol>"[^"]*"|\S+)""", RegexOptions.Multiline)]
    private static partial Regex MsvcUnresolved();

    /// <summary>lld-link: <c>lld-link: error: undefined symbol: int __cdecl f(int)</c>, then <c>&gt;&gt;&gt; referenced by path\user.obj:(...)</c>.</summary>
    [GeneratedRegex(@"undefined symbol: (?<symbol>[^\r\n]+)(?:\r?\n>>> referenced by (?<user>.+?\.(?:obj|o|lib|a))\b)?")]
    private static partial Regex LldUnresolved();

    /// <summary>GNU ld: <c>user.cpp:(.text+0x15): undefined reference to `f(int)'</c>, with the quotes of the C locale or of UTF-8.</summary>
    [GeneratedRegex(@"(?<user>[^\s:]+):\([^)]*\): undefined reference to [`‘'](?<symbol>[^'’]+)['’]")]
    private static partial Regex LdUnresolved();

    [GeneratedRegex(@"^(?<index>\d+)_.+\.obj$", RegexOptions.IgnoreCase)]
    private static partial Regex NativeObject();

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
