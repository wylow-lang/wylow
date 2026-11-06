using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Wyl.Compiler.Build;

/// <summary>
/// The C++ compiler a program is built with. <see cref="ClangSanitized"/> is clang-cl with
/// AddressSanitizer and UBSan, for testing: a program that runs clean under it does nothing undefined
/// that they can see.
/// </summary>
public enum Toolchain { Msvc, Gcc, ClangSanitized, ThreadSanitized }

public sealed record NativeBuildResult(bool Success, string Output);

public sealed class NativeCompiler
{
    static readonly ConcurrentDictionary<string, Lazy<string?>> PrecompiledHeaders = new();
    static readonly Lazy<IReadOnlyDictionary<string, string>?> MsvcEnvironment = new(CaptureMsvcEnvironment);

    // The installed clang may be older than the MSVC STL expects.
    static readonly string[] SanitizedFlags = ["/nologo", "/std:c++20", "/utf-8", "/EHsc", "-fsanitize=address,undefined", "/D_ALLOW_COMPILER_AND_STL_VERSION_MISMATCH"];

    static readonly string[] ThreadSanitizedClangFlags = ["/nologo", "/std:c++20", "/utf-8", "/EHsc", "-fsanitize=thread", "-g", "/D_ALLOW_COMPILER_AND_STL_VERSION_MISMATCH"];

    static readonly string[] ThreadSanitizedGccFlags = ["-std=c++20", "-fsanitize=thread", "-g", "-pthread"];

    readonly string compilerPath;
    readonly IReadOnlyDictionary<string, string>? environment;

    NativeCompiler(Toolchain toolchain, string compilerPath, IReadOnlyDictionary<string, string>? environment)
    {
        Toolchain = toolchain;
        this.compilerPath = compilerPath;
        this.environment = environment;
    }

    public Toolchain Toolchain { get; }

    /// <summary>
    /// Everything a build depends on besides the program's own directory: the generated C++, the flags,
    /// the runtime headers, and the C++ compiler itself. Equal keys mean the executable a previous build
    /// left is the one this build would produce.
    /// </summary>
    public string BuildKey(bool release, string cpp)
    {
        var compiler = new FileInfo(compilerPath);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{Toolchain}|{release}|{compilerPath}|{compiler.Length}|{compiler.LastWriteTimeUtc.Ticks}|{RuntimeContentHash.Value}\n{cpp}")));
    }

    static readonly Lazy<string> RuntimeContentHash = new(() =>
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(RuntimeDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(RuntimeDirectory, file)));
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    });

    public static string RuntimeDirectory => Path.Combine(AppContext.BaseDirectory, "runtime");

    public static NativeCompiler? Find(Toolchain? preferred, out string? error)
    {
        error = null;
        if (preferred is null or Toolchain.Msvc && FindMsvc() is { } msvc)
            return msvc;
        if (preferred is null or Toolchain.Gcc && FindGcc() is { } gcc)
            return gcc;
        if (preferred is Toolchain.ClangSanitized && FindClangSanitized() is { } clang)
            return clang;
        if (preferred is Toolchain.ThreadSanitized && ThreadSanitized.Value is { } threaded)
            return threaded;

        error = preferred switch
        {
            Toolchain.Msvc => "could not find MSVC; install Visual Studio with the 'Desktop development with C++' workload",
            Toolchain.Gcc => "could not find g++ on PATH",
            Toolchain.ClangSanitized => "could not find clang-cl on PATH, or MSVC, whose headers and libraries it uses",
            Toolchain.ThreadSanitized => "no C++ toolchain here builds a program with ThreadSanitizer",
            _ => "no C++ compiler found; install Visual Studio with the 'Desktop development with C++' workload, or g++",
        };
        return null;
    }

    static NativeCompiler? FindMsvc()
    {
        if (!OperatingSystem.IsWindows() || MsvcEnvironment.Value is not { } env)
            return null;
        var cl = FindOnPath("cl.exe", env.GetValueOrDefault("PATH") ?? env.GetValueOrDefault("Path"));
        return cl is null ? null : new NativeCompiler(Toolchain.Msvc, cl, env);
    }

    static NativeCompiler? FindGcc()
    {
        var gcc = FindOnPath(OperatingSystem.IsWindows() ? "g++.exe" : "g++", Environment.GetEnvironmentVariable("PATH"));
        return gcc is null ? null : new NativeCompiler(Toolchain.Gcc, gcc, null);
    }

    static readonly Lazy<NativeCompiler?> ThreadSanitized = new(ProbeThreadSanitized);

    static IEnumerable<NativeCompiler> ThreadSanitizedCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            if (FindOnPath("clang-cl.exe", Environment.GetEnvironmentVariable("PATH")) is { } clang && MsvcEnvironment.Value is { } env)
                yield return new NativeCompiler(Toolchain.ThreadSanitized, clang, env);
            yield break;
        }
        foreach (var name in new[] { "clang++", "g++" })
        {
            if (FindOnPath(name, Environment.GetEnvironmentVariable("PATH")) is { } compiler)
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
    }

    /// <summary>clang-cl compiles against MSVC's headers and libraries, so it runs in MSVC's environment.</summary>
    static NativeCompiler? FindClangSanitized()
    {
        if (!OperatingSystem.IsWindows() || MsvcEnvironment.Value is not { } env)
            return null;
        var clang = FindOnPath("clang-cl.exe", Environment.GetEnvironmentVariable("PATH"));
        if (clang is null)
            return null;
        AddSanitizerRuntimeToPath(clang);
        return new NativeCompiler(Toolchain.ClangSanitized, clang, env);
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
    /// The first report AddressSanitizer or UBSan wrote to a program's standard error, or null. UBSan
    /// reports and continues, so the exit code doesn't show it.
    /// </summary>
    public static string? SanitizerReport(string standardError) =>
        standardError.Replace("\r\n", "\n").Split('\n')
            .FirstOrDefault(l => l.Contains("ERROR: AddressSanitizer", StringComparison.Ordinal) || l.Contains(": runtime error: ", StringComparison.Ordinal));

    static string? FindOnPath(string fileName, string? path) =>
        path?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, fileName))
            .FirstOrDefault(File.Exists);

    /// <summary>cl.exe only works inside the environment that vcvars64.bat sets up, so capture it once.</summary>
    static IReadOnlyDictionary<string, string>? CaptureMsvcEnvironment()
    {
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
            return null;

        var find = new ProcessStartInfo(vswhere);
        foreach (var arg in new[] { "-latest", "-products", "*", "-requires",
                     "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath" })
            find.ArgumentList.Add(arg);
        var installation = ProcessRunner.Run(find).StandardOutput.Trim();
        if (installation.Length == 0)
            return null;

        var vcvars = Path.Combine(installation, "VC", "Auxiliary", "Build", "vcvars64.bat");
        if (!File.Exists(vcvars))
            return null;

        var capture = ProcessRunner.Run(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /s /c \"\"{vcvars}\" >nul 2>&1 && set\"",
        });
        if (capture.ExitCode != 0)
            return null;

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in capture.StandardOutput.Split('\n'))
        {
            int separator = line.IndexOf('=');
            if (separator > 0)
                env[line[..separator]] = line[(separator + 1)..].TrimEnd('\r');
        }
        return env;
    }

    string? PrecompiledDirectory(bool release) =>
        Toolchain == Toolchain.ThreadSanitized
            ? null
            : PrecompiledHeaders.GetOrAdd($"{compilerPath}|{release}", _ => new(() => BuildPrecompiledHeader(release))).Value;

    public NativeBuildResult Build(string cppPath, string exePath, bool release, bool precompiledHeader = true)
    {
        var startInfo = new ProcessStartInfo(compilerPath)
        {
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exePath))!,
        };
        if (environment is not null)
        {
            startInfo.Environment.Clear();
            foreach (var (name, value) in environment)
                startInfo.Environment[name] = value;
        }

        var args = startInfo.ArgumentList;
        if (Toolchain == Toolchain.Msvc)
        {
            foreach (var arg in new[] { "/nologo", "/std:c++20", "/permissive-", "/utf-8", "/EHsc", "/W3" })
                args.Add(arg);
            args.Add(release ? "/O2" : "/Od");
            if (!release)
                args.Add("/DWYL_DEBUG=1");
            args.Add($"/I{RuntimeDirectory}");
            var precompiled = precompiledHeader ? PrecompiledDirectory(release) : null;
            if (precompiled is not null)
            {
                args.Add("/Yuwyl.h");
                args.Add($"/Fp{Path.Combine(precompiled, "wyl.pch")}");
            }
            args.Add($"/Fe{exePath}");
            args.Add($"/Fo{Path.ChangeExtension(exePath, ".obj")}");
            args.Add(cppPath);
            if (precompiled is not null)
                args.Add(Path.Combine(precompiled, "wyl.obj"));
        }
        else if (Toolchain is Toolchain.ClangSanitized or Toolchain.ThreadSanitized && environment is not null)
        {
            foreach (var arg in Toolchain == Toolchain.ThreadSanitized ? ThreadSanitizedClangFlags : SanitizedFlags)
                args.Add(arg);
            // link.exe took over ten seconds per sanitized program, since every link writes its debug
            // information through the one shared PDB server; lld-link has no such server.
            args.Add("-fuse-ld=lld");
            args.Add(release ? "/O2" : "/Od");
            if (!release)
                args.Add("/DWYL_DEBUG=1");
            args.Add($"/I{RuntimeDirectory}");
            args.Add($"/Fe{exePath}");
            if ((precompiledHeader ? PrecompiledDirectory(release) : null) is { } precompiled)
            {
                // As for MSVC, the program and the precompiled object link in one call; /Fo names a
                // directory because that call has two inputs.
                args.Add("/Yuwyl.h");
                args.Add($"/Fp{Path.Combine(precompiled, "wyl.pch")}");
                args.Add($"/Fo{Path.GetDirectoryName(Path.GetFullPath(exePath))}{Path.DirectorySeparatorChar}");
                args.Add(cppPath);
                args.Add(Path.Combine(precompiled, "wyl.obj"));
            }
            else
            {
                args.Add($"/Fo{Path.ChangeExtension(exePath, ".obj")}");
                args.Add(cppPath);
            }
        }
        else
        {
            if (Toolchain == Toolchain.ThreadSanitized)
                foreach (var arg in ThreadSanitizedGccFlags)
                    args.Add(arg);
            else
            {
                args.Add("-std=c++20");
                args.Add("-pthread");
            }
            args.Add(release ? "-O2" : "-O0");
            if (!release)
                args.Add("-DWYL_DEBUG=1");
            // Avoids depending on MinGW's runtime DLLs being on PATH when the program runs.
            if (OperatingSystem.IsWindows())
                args.Add("-static");
            // gcc takes wyl.h.gch from the first include directory holding one.
            if ((precompiledHeader ? PrecompiledDirectory(release) : null) is { } precompiled)
                args.Add($"-I{precompiled}");
            args.Add($"-I{RuntimeDirectory}");
            args.Add("-o");
            args.Add(exePath);
            args.Add(cppPath);
        }

        var result = ProcessRunner.Run(startInfo);
        return new NativeBuildResult(result.ExitCode == 0, (result.StandardOutput + result.StandardError).Trim());
    }

    /// <summary>
    /// Parsing the standard headers dominates compiling a generated program, so every program shares one
    /// precompiled wyl.h per compiler and mode. Null if it can't be built; programs then compile without it.
    /// </summary>
    string? BuildPrecompiledHeader(bool release)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(compilerPath)))[..12];
        var directory = Path.Combine(AppContext.BaseDirectory, "pch", key, release ? "release" : "debug");
        string[] artifacts = Toolchain == Toolchain.Gcc
            ? [Path.Combine(directory, "wyl.h.gch")]
            : [Path.Combine(directory, "wyl.pch"), Path.Combine(directory, "wyl.obj")];

        // The mutex serializes concurrent test runs and CLI invocations building the same files.
        using var mutex = new Mutex(false, "Global\\wyl-pch-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)))[..16]);
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { }
        try
        {
            var newestHeader = Directory.EnumerateFiles(RuntimeDirectory, "*.h", SearchOption.AllDirectories)
                .Max(f => File.GetLastWriteTimeUtc(f));
            if (artifacts.All(f => File.Exists(f) && File.GetLastWriteTimeUtc(f) > newestHeader))
                return directory;

            Directory.CreateDirectory(directory);
            var startInfo = new ProcessStartInfo(compilerPath) { WorkingDirectory = directory };
            if (environment is not null)
            {
                startInfo.Environment.Clear();
                foreach (var (name, value) in environment)
                    startInfo.Environment[name] = value;
            }
            var args = startInfo.ArgumentList;
            if (Toolchain == Toolchain.Msvc)
            {
                var stub = Path.Combine(directory, "wyl.cpp");
                File.WriteAllText(stub, "#include \"wyl.h\"\n");
                foreach (var arg in new[] { "/nologo", "/std:c++20", "/permissive-", "/utf-8", "/EHsc", "/W3", "/c" })
                    args.Add(arg);
                args.Add(release ? "/O2" : "/Od");
                if (!release)
                    args.Add("/DWYL_DEBUG=1");
                args.Add($"/I{RuntimeDirectory}");
                args.Add("/Ycwyl.h");
                args.Add($"/Fp{artifacts[0]}");
                args.Add($"/Fo{artifacts[1]}");
                args.Add(stub);
            }
            else if (Toolchain == Toolchain.ClangSanitized)
            {
                var stub = Path.Combine(directory, "wyl.cpp");
                File.WriteAllText(stub, "#include \"wyl.h\"\n");
                foreach (var arg in SanitizedFlags)
                    args.Add(arg);
                args.Add("/c");
                args.Add(release ? "/O2" : "/Od");
                if (!release)
                    args.Add("/DWYL_DEBUG=1");
                args.Add($"/I{RuntimeDirectory}");
                args.Add("/Ycwyl.h");
                args.Add($"/Fp{artifacts[0]}");
                args.Add($"/Fo{artifacts[1]}");
                args.Add(stub);
            }
            else
            {
                args.Add("-std=c++20");
                args.Add("-pthread");
                args.Add(release ? "-O2" : "-O0");
                if (!release)
                    args.Add("-DWYL_DEBUG=1");
                args.Add("-x");
                args.Add("c++-header");
                args.Add(Path.Combine(RuntimeDirectory, "wyl.h"));
                args.Add("-o");
                args.Add(artifacts[0]);
            }
            return ProcessRunner.Run(startInfo).ExitCode == 0 ? directory : null;
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}
