using System.Collections.Concurrent;
using System.Diagnostics;

namespace Wyl.Compiler.Build;

/// <summary>
/// The C++ compiler a program is built with. <see cref="Clang"/> is clang-cl without sanitizers, the
/// default on Windows. <see cref="ClangSanitized"/> is clang-cl with AddressSanitizer and UBSan, for
/// testing: a program that runs clean under it does nothing undefined that they can see.
/// </summary>
public enum Toolchain { Msvc, Clang, Gcc, ClangSanitized, ThreadSanitized }

/// <summary>
/// Finds the C++ compiler that builds a program (decision 0098). Each lookup runs once per process,
/// so a compiler that was found, or refused, is not looked for again.
/// </summary>
public static class Toolchains
{
    static readonly Toolchain[] DefaultOrder = [Toolchain.Clang, Toolchain.Msvc, Toolchain.Gcc];

    static readonly ConcurrentDictionary<Toolchain, Lazy<(NativeCompiler? Compiler, string? Reason)>> Found = new();

    /// <summary>
    /// The compiler for <paramref name="preferred"/>, or with none preferred, the first of clang-cl,
    /// MSVC, and g++ that this machine has in a supported version (decision 0098). Returns null, with
    /// the reason in <paramref name="error"/>, if no such compiler is found or the one found is older
    /// than <see cref="ToolchainVersions"/> allows.
    /// </summary>
    public static NativeCompiler? Find(Toolchain? preferred, out string? error)
    {
        error = null;
        if (OperatingSystem.IsMacOS())
        {
            error = "wyl does not build programs on macOS; 'wyl interpret' runs them without a C++ compiler";
            return null;
        }
        List<string> reasons = [];
        foreach (var toolchain in preferred is { } one ? [one] : DefaultOrder)
        {
            var (compiler, reason) = Found.GetOrAdd(toolchain, t => new(() => Lookup(t))).Value;
            if (compiler is not null)
                return compiler;
            reasons.Add(reason!);
        }
        error = reasons.Count == 1
            ? reasons[0]
            : "no supported C++ compiler found:\n" + string.Join('\n', reasons.Select(r => "  " + r));
        return null;
    }

    static (NativeCompiler?, string?) Lookup(Toolchain toolchain) => toolchain switch
    {
        Toolchain.Clang => FindClang(Toolchain.Clang),
        Toolchain.ClangSanitized => FindClang(Toolchain.ClangSanitized),
        Toolchain.Msvc => FindMsvc(),
        Toolchain.Gcc => FindGcc(),
        _ => ProbeThreadSanitized() is { } threaded
            ? (threaded, null)
            : (null, $"no C++ compiler here builds a program with ThreadSanitizer; it needs clang++ {ToolchainVersions.LinuxClang.Major} or g++ {ToolchainVersions.Gcc.Major} on Linux"),
    };

    /// <summary>
    /// The clang-cl of the Visual Studio installation whose MSVC environment the build uses. That
    /// clang-cl is released with the installation's standard library, so the two always match.
    /// </summary>
    static (NativeCompiler?, string?) FindClang(Toolchain toolchain)
    {
        if (MsvcEnvironment.Get() is not { } env)
            return (null, MsvcEnvironment.Refusal("clang-cl"));
        var clang = VisualStudioClang(env);
        if (clang is null)
            return (null, "could not find clang-cl in Visual Studio; install its 'C++ Clang tools for Windows' component");
        if (toolchain == Toolchain.ClangSanitized)
            AddSanitizerRuntimeToPath(clang);
        return (new NativeCompiler(toolchain, clang, env), null);
    }

    static string? VisualStudioClang(IReadOnlyDictionary<string, string> env) =>
        env.GetValueOrDefault("VSINSTALLDIR") is { Length: > 0 } installation
        && Path.Combine(installation, "VC", "Tools", "Llvm", "x64", "bin", "clang-cl.exe") is var clang
        && File.Exists(clang)
            ? clang
            : null;

    static (NativeCompiler?, string?) FindMsvc()
    {
        if (MsvcEnvironment.Get() is not { } env)
            return (null, MsvcEnvironment.Refusal("cl.exe"));
        var cl = FindOnPath("cl.exe", env.GetValueOrDefault("PATH") ?? env.GetValueOrDefault("Path"));
        return cl is null
            ? (null, "could not find cl.exe in the MSVC environment")
            : (new NativeCompiler(Toolchain.Msvc, cl, env), null);
    }

    static (NativeCompiler?, string?) FindGcc()
    {
        var gcc = FindOnPath(OperatingSystem.IsWindows() ? "g++.exe" : "g++", Environment.GetEnvironmentVariable("PATH"));
        if (gcc is null)
            return (null, "could not find g++ on PATH");
        var version = CompilerVersion(gcc, "-dumpfullversion");
        return ToolchainVersions.IsSupported(version, ToolchainVersions.Gcc)
            ? (new NativeCompiler(Toolchain.Gcc, gcc, null), null)
            : (null, $"g++ is version {version ?? "unknown"}, and wyl needs {ToolchainVersions.Gcc.Major} or newer");
    }

    /// <summary>What a gcc-style compiler prints for <paramref name="flag"/>, or null if it can't be run.</summary>
    static string? CompilerVersion(string compiler, string flag)
    {
        var query = new ProcessStartInfo(compiler);
        query.ArgumentList.Add(flag);
        var result = ProcessRunner.Run(query);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    static IEnumerable<NativeCompiler> ThreadSanitizedCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            if (MsvcEnvironment.Get() is { } env && VisualStudioClang(env) is { } clang)
                yield return new NativeCompiler(Toolchain.ThreadSanitized, clang, env);
            yield break;
        }
        foreach (var (name, flag, minimum) in new[] { ("clang++", "-dumpversion", ToolchainVersions.LinuxClang), ("g++", "-dumpfullversion", ToolchainVersions.Gcc) })
        {
            if (FindOnPath(name, Environment.GetEnvironmentVariable("PATH")) is { } compiler
                && ToolchainVersions.IsSupported(CompilerVersion(compiler, flag), minimum))
                yield return new NativeCompiler(Toolchain.ThreadSanitized, compiler, null);
        }
    }

    static NativeCompiler? ProbeThreadSanitized()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wyl-tsan-probe-" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(directory);
            var cpp = Path.Combine(directory, "probe.cpp");
            File.WriteAllText(cpp, "int main() { return 0; }\n");
            var exe = Path.Combine(directory, "probe" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            foreach (var compiler in ThreadSanitizedCandidates())
            {
                if (!compiler.Build(cpp, exe, release: false, precompiledHeader: false).Success)
                    continue;
                var run = ProcessRunner.Run(new ProcessStartInfo(exe), TimeSpan.FromSeconds(30));
                if (run is { ExitCode: 0 } and { TimedOut: false })
                    return compiler;
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Since LLVM 20, sanitized programs on Windows always load the ASan runtime as a DLL, which lives
    /// in clang's resource directory rather than on PATH; every program this process starts inherits
    /// its PATH, so the runner, the matrices, the fuzzer, and `wyl run` all find it.
    /// </summary>
    static void AddSanitizerRuntimeToPath(string clang)
    {
        var query = new ProcessStartInfo(clang);
        query.ArgumentList.Add("/clang:-print-file-name=clang_rt.asan_dynamic-x86_64.dll");
        var dll = ProcessRunner.Run(query).StandardOutput.Trim();
        if (!Path.IsPathRooted(dll) || !File.Exists(dll))
            return;
        var directory = Path.GetDirectoryName(dll)!;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!path.Split(Path.PathSeparator).Contains(directory, StringComparer.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + path);
    }

    /// <summary>
    /// The first report AddressSanitizer, UBSan, or ThreadSanitizer wrote to a program's standard
    /// error, or null. UBSan and ThreadSanitizer report and continue, so the exit code doesn't show
    /// either.
    /// </summary>
    public static string? SanitizerReport(string standardError) =>
        standardError.Replace("\r\n", "\n").Split('\n')
            .FirstOrDefault(l => l.Contains("ERROR: AddressSanitizer", StringComparison.Ordinal)
                || l.Contains(": runtime error: ", StringComparison.Ordinal)
                || l.Contains("WARNING: ThreadSanitizer:", StringComparison.Ordinal));

    static string? FindOnPath(string fileName, string? path) =>
        path?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, fileName))
            .FirstOrDefault(File.Exists);
}
