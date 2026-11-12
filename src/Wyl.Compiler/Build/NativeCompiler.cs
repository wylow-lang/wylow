using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wyl.Compiler.Build;

/// <summary>
/// The C++ compiler a program is built with. <see cref="Clang"/> is clang-cl without sanitizers, the
/// default on Windows. <see cref="ClangSanitized"/> is clang-cl with AddressSanitizer and UBSan, for
/// testing: a program that runs clean under it does nothing undefined that they can see.
/// </summary>
public enum Toolchain { Msvc, Clang, Gcc, ClangSanitized, ThreadSanitized }

public sealed record NativeBuildResult(bool Success, string Output);

public sealed class NativeCompiler
{
    static readonly ConcurrentDictionary<string, Lazy<string?>> PrecompiledHeaders = new();
    static readonly Lazy<IReadOnlyDictionary<string, string>?> MsvcEnvironment = new(CaptureMsvcEnvironment);

    static readonly string[] SanitizedFlags = ["/nologo", "/std:c++20", "/utf-8", "/EHsc", "-fsanitize=address,undefined"];

    static readonly string[] ThreadSanitizedClangFlags = ["/nologo", "/std:c++20", "/utf-8", "/EHsc", "-fsanitize=thread", "-g"];

    // The runtime frees pool slots with sized operator delete, which clang++ before 19 declares only
    // with -fsized-deallocation; g++ accepts the flag and enables it anyway.
    static readonly string[] ThreadSanitizedGccFlags = ["-std=c++20", "-fsanitize=thread", "-fsized-deallocation", "-g", "-pthread"];

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
    /// the runtime headers, the C++ compiler itself, the manifest's <c>native</c> lists, and the files
    /// that <see cref="NativeInputs.Files"/> gives. Equal keys mean the executable a previous build
    /// left is the one this build would produce.
    /// </summary>
    public string BuildKey(bool release, string cpp, NativeInputs? native = null)
    {
        native ??= NativeInputs.None;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{Identity(release)}\n{cpp}\n{native.Text}"));
        foreach (var (path, bytes) in native.Files())
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"\n{path}\n{bytes.Length}\n"));
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>What a build depends on besides the C++ it compiles: the toolchain, the mode, the C++ compiler itself, and the runtime headers.</summary>
    internal string Identity(bool release)
    {
        var compiler = new FileInfo(compilerPath);
        return $"{Toolchain}|{release}|{compilerPath}|{compiler.Length}|{compiler.LastWriteTimeUtc.Ticks}|{RuntimeContentHash.Value}";
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

    static readonly Toolchain[] DefaultOrder = [Toolchain.Clang, Toolchain.Msvc, Toolchain.Gcc];

    static readonly ConcurrentDictionary<Toolchain, Lazy<(NativeCompiler? Compiler, string? Reason)>> Found = new();

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
        if (Msvc() is not { } env)
            return (null, MsvcRefusal("clang-cl"));
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
        if (Msvc() is not { } env)
            return (null, MsvcRefusal("cl.exe"));
        var cl = FindOnPath("cl.exe", env.GetValueOrDefault("PATH") ?? env.GetValueOrDefault("Path"));
        return cl is null
            ? (null, "could not find cl.exe in the MSVC environment")
            : (new NativeCompiler(Toolchain.Msvc, cl, env), null);
    }

    /// <summary>The MSVC environment, or null if this is not Windows or its toolset is not supported.</summary>
    static IReadOnlyDictionary<string, string>? Msvc() =>
        OperatingSystem.IsWindows()
        && MsvcEnvironment.Value is { } env
        && ToolchainVersions.IsSupported(MsvcToolsetVersion(env), ToolchainVersions.MsvcToolset)
            ? env
            : null;

    static string? MsvcToolsetVersion(IReadOnlyDictionary<string, string> env) => env.GetValueOrDefault("VCToolsVersion");

    /// <summary>Why <see cref="Msvc"/> is null, for a compiler that needs the MSVC environment.</summary>
    static string MsvcRefusal(string compiler)
    {
        if (!OperatingSystem.IsWindows())
            return $"{compiler} needs MSVC, which exists only on Windows";
        if (MsvcEnvironment.Value is not { } env)
            return $"could not find MSVC, which {compiler} needs; install Visual Studio with the 'Desktop development with C++' workload";
        return $"the MSVC toolset is version {MsvcToolsetVersion(env) ?? "unknown"}, and wyl needs {ToolchainVersions.MsvcToolset} or newer; update Visual Studio";
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
            if (Msvc() is { } env && VisualStudioClang(env) is { } clang)
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

    /// <summary>
    /// cl.exe only works inside the environment that vcvars64.bat sets up. Running vcvars64.bat takes
    /// seconds, so the environment it set up is kept on disk. The kept environment is used again while
    /// the installation and the variables vcvars64.bat reads are unchanged, and while every include
    /// and library directory it names still exists.
    /// </summary>
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

        var key = MsvcEnvironmentKey(installation, vcvars);
        if (ReadMsvcEnvironment(key) is { } kept)
            return kept;

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
        WriteMsvcEnvironment(key, env);
        return env;
    }

    static string MsvcEnvironmentPath => Path.Combine(AppContext.BaseDirectory, "msvc-environment.json");

    sealed record KeptMsvcEnvironment(string Key, Dictionary<string, string> Environment);

    // The variables that vcvars64.bat appends to or takes its settings from, by name and by prefix.
    static readonly string[] VcvarsReads = ["PATH", "INCLUDE", "LIB", "LIBPATH", "EXTERNAL_INCLUDE", "VSINSTALLDIR", "VCINSTALLDIR"];
    static readonly string[] VcvarsReadPrefixes = ["VSCMD_", "VCTOOLS", "WINDOWSSDK", "UCRT"];

    /// <summary>
    /// Everything the environment vcvars64.bat sets up depends on: the installation, its default
    /// toolset, and the variables of this process that vcvars64.bat reads.
    /// </summary>
    static string MsvcEnvironmentKey(string installation, string vcvars)
    {
        var text = new StringBuilder();
        text.Append(installation).Append('\n').Append(File.GetLastWriteTimeUtc(vcvars).Ticks).Append('\n');
        var toolset = Path.Combine(installation, "VC", "Auxiliary", "Build", "Microsoft.VCToolsVersion.default.txt");
        if (File.Exists(toolset))
            text.Append(File.ReadAllText(toolset).Trim()).Append('\n');
        var variables = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Select(e => (Name: ((string)e.Key).ToUpperInvariant(), Value: e.Value as string ?? ""))
            .Where(e => VcvarsReads.Contains(e.Name) || VcvarsReadPrefixes.Any(e.Name.StartsWith))
            .OrderBy(e => e.Name, StringComparer.Ordinal);
        foreach (var (name, value) in variables)
            text.Append(name).Append('=').Append(value).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    static IReadOnlyDictionary<string, string>? ReadMsvcEnvironment(string key)
    {
        try
        {
            if (!File.Exists(MsvcEnvironmentPath)
                || JsonSerializer.Deserialize<KeptMsvcEnvironment>(File.ReadAllText(MsvcEnvironmentPath)) is not { } kept
                || kept.Key != key)
                return null;
            var env = new Dictionary<string, string>(kept.Environment, StringComparer.OrdinalIgnoreCase);
            // An update of Visual Studio or the Windows SDK can remove directories that a kept environment names.
            if (!env.ContainsKey("INCLUDE") || !env.ContainsKey("LIB"))
                return null;
            var directories = new[] { env["INCLUDE"], env["LIB"] }.SelectMany(list => list.Split(';', StringSplitOptions.RemoveEmptyEntries));
            return directories.All(Directory.Exists) ? env : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    static void WriteMsvcEnvironment(string key, Dictionary<string, string> env)
    {
        // Another process may be writing the same file, so each process writes its own and moves it into place.
        var temporary = $"{MsvcEnvironmentPath}.{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new KeptMsvcEnvironment(key, env)));
            File.Move(temporary, MsvcEnvironmentPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    string? PrecompiledDirectory(bool release) =>
        Toolchain == Toolchain.ThreadSanitized
            ? null
            : PrecompiledHeaders.GetOrAdd($"{Toolchain}|{compilerPath}|{release}", _ => new(() => BuildPrecompiledHeader(release))).Value;

    /// <summary>Whether the compiler takes MSVC's flags, as cl.exe and clang-cl do, rather than gcc's.</summary>
    bool MsvcStyle => environment is not null;

    /// <summary>
    /// The flags that a C++ source of the program compiles with, before its include directories:
    /// the language, the sanitizers, and the build mode.
    /// </summary>
    List<string> CppFlags(bool release)
    {
        List<string> args = Toolchain switch
        {
            Toolchain.Msvc or Toolchain.Clang => ["/nologo", "/std:c++20", "/permissive-", "/utf-8", "/EHsc", "/W3"],
            Toolchain.ClangSanitized => [.. SanitizedFlags],
            Toolchain.ThreadSanitized when MsvcStyle => [.. ThreadSanitizedClangFlags],
            Toolchain.ThreadSanitized => [.. ThreadSanitizedGccFlags],
            _ => ["-std=c++20", "-pthread"],
        };
        args.Add(MsvcStyle ? (release ? "/O2" : "/Od") : (release ? "-O2" : "-O0"));
        if (!release)
            args.Add(MsvcStyle ? "/DWYL_DEBUG=1" : "-DWYL_DEBUG=1");
        return args;
    }

    /// <summary>
    /// The flags that a C source under <c>native/</c> compiles with, before its include directories:
    /// C17, the sanitizers of the program, and the optimization of the build mode.
    /// </summary>
    List<string> CFlags(bool release)
    {
        List<string> args = Toolchain switch
        {
            Toolchain.Msvc or Toolchain.Clang => ["/nologo", "/std:c17", "/utf-8", "/W3"],
            Toolchain.ClangSanitized => ["/nologo", "/std:c17", "/utf-8", "-fsanitize=address,undefined"],
            Toolchain.ThreadSanitized when MsvcStyle => ["/nologo", "/std:c17", "/utf-8", "-fsanitize=thread", "-g"],
            Toolchain.ThreadSanitized => ["-std=c17", "-fsanitize=thread", "-g", "-pthread"],
            _ => ["-std=c17", "-pthread"],
        };
        args.Add(MsvcStyle ? (release ? "/O2" : "/Od") : (release ? "-O2" : "-O0"));
        return args;
    }

    ProcessStartInfo StartInfo(string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(compilerPath) { WorkingDirectory = workingDirectory };
        if (environment is not null)
        {
            startInfo.Environment.Clear();
            foreach (var (name, value) in environment)
                startInfo.Environment[name] = value;
        }
        return startInfo;
    }

    /// <summary>
    /// Builds the executable from the generated C++ and every native source under the package's
    /// <c>native/</c> directory, with the include directories and definitions of <paramref name="native"/>,
    /// and links the libraries it names (spec <c>interop.native</c>, <c>programs.manifest</c>). Each
    /// native source compiles to an object of its own first (<see cref="CompileNative"/>). A definition
    /// could change what <c>wyl.h</c> means, so a program built with definitions does not use the
    /// precompiled header.
    /// </summary>
    public NativeBuildResult Build(string cppPath, string exePath, bool release, bool precompiledHeader = true, NativeInputs? native = null)
    {
        native ??= NativeInputs.None;
        precompiledHeader &= native.Defines.Count == 0;
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
        if (CompileNative(native, release, outputDirectory, out var nativeObjects) is { } failed)
            return failed;

        var startInfo = StartInfo(outputDirectory);
        var args = startInfo.ArgumentList;
        foreach (var arg in CppFlags(release))
            args.Add(arg);
        if (MsvcStyle)
        {
            // link.exe took over ten seconds per sanitized program, since every link writes its debug
            // information through the one shared PDB server; lld-link has no such server.
            if (Toolchain is Toolchain.ClangSanitized or Toolchain.ThreadSanitized)
                args.Add("-fuse-ld=lld");
            args.Add($"/I{RuntimeDirectory}");
            AddNativeFlags(args, native, msvc: true);
            var precompiled = precompiledHeader ? PrecompiledDirectory(release) : null;
            if (precompiled is not null)
            {
                args.Add("/Yuwyl.h");
                args.Add($"/Fp{Path.Combine(precompiled, "wyl.pch")}");
            }
            args.Add($"/Fe{exePath}");
            // clang-cl refuses an object file's name in /Fo when the call has more than one input.
            if (precompiled is not null || nativeObjects.Count > 0)
                args.Add($"/Fo{outputDirectory}{Path.DirectorySeparatorChar}");
            else
                args.Add($"/Fo{Path.ChangeExtension(exePath, ".obj")}");
            args.Add(cppPath);
            if (precompiled is not null)
                args.Add(Path.Combine(precompiled, "wyl.obj"));
            foreach (var obj in nativeObjects)
                args.Add(obj);
            AddLibraries(args, native, msvc: true);
        }
        else
        {
            // Avoids depending on MinGW's runtime DLLs being on PATH when the program runs.
            if (OperatingSystem.IsWindows())
                args.Add("-static");
            // gcc takes wyl.h.gch from the first include directory holding one.
            if ((precompiledHeader ? PrecompiledDirectory(release) : null) is { } precompiled)
                args.Add($"-I{precompiled}");
            args.Add($"-I{RuntimeDirectory}");
            AddNativeFlags(args, native, msvc: false);
            args.Add("-o");
            args.Add(exePath);
            args.Add(cppPath);
            foreach (var obj in nativeObjects)
                args.Add(obj);
            AddLibraries(args, native, msvc: false);
        }

        var result = ProcessRunner.Run(startInfo);
        return new NativeBuildResult(result.ExitCode == 0, (result.StandardOutput + result.StandardError).Trim());
    }

    /// <summary>The include directories and the definitions that every native source and the generated program compile with.</summary>
    static void AddNativeFlags(Collection<string> args, NativeInputs native, bool msvc)
    {
        foreach (var directory in native.IncludeDirectories)
            args.Add((msvc ? "/I" : "-I") + directory);
        foreach (var define in native.Defines)
            args.Add((msvc ? "/D" : "-D") + define);
    }

    /// <summary>
    /// The libraries of <c>link</c>, each in the toolchain's spelling, and the directories of
    /// <c>libraryPaths</c> to search for them. With MSVC and clang-cl, the directories come last,
    /// because every argument after <c>/link</c> goes to the linker.
    /// </summary>
    static void AddLibraries(Collection<string> args, NativeInputs native, bool msvc)
    {
        if (!msvc)
        {
            foreach (var directory in native.LibraryPaths)
                args.Add($"-L{directory}");
            foreach (var library in native.Link)
                args.Add($"-l{library}");
            return;
        }
        foreach (var library in native.Link)
            args.Add($"{library}.lib");
        if (native.LibraryPaths.Count == 0)
            return;
        args.Add("/link");
        foreach (var directory in native.LibraryPaths)
            args.Add($"/LIBPATH:{directory}");
    }

    /// <summary>
    /// Compiles each native source of <paramref name="native"/> to an object in the directory
    /// <c>native-objects</c> of <paramref name="outputDirectory"/>: a C source as C17 and a C++
    /// source as C++20, with the flags of the program's toolchain and mode (decision 0099). An object
    /// whose key file matches is kept from an earlier build. Its key covers the toolchain, the mode,
    /// the compiler's arguments, and every file that the source can include, which is every file of
    /// <see cref="NativeInputs.IncludableFiles"/>. A source's warnings appear only if it fails to
    /// compile. Returns the failure of the first source in order that fails, or null.
    /// </summary>
    NativeBuildResult? CompileNative(NativeInputs native, bool release, string outputDirectory, out List<string> objects)
    {
        var sources = native.Sources;
        objects = [];
        if (sources.Count == 0)
            return null;
        var directory = Path.Combine(outputDirectory, "native-objects");
        Directory.CreateDirectory(directory);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{Identity(release)}\n{native.Text}"));
        foreach (var (path, bytes) in native.IncludableFiles())
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"\n{path}\n{bytes.Length}\n"));
            hash.AppendData(bytes);
        }
        var inputs = Convert.ToHexString(hash.GetHashAndReset());

        var extension = MsvcStyle ? ".obj" : ".o";
        // Each source has an object of its own, so that two of one name in different directories get two objects.
        var paths = sources.Select((source, i) => Path.Combine(directory, $"{i}_{Path.GetFileNameWithoutExtension(source)}{extension}")).ToList();
        // An object of a source that was removed or renamed would otherwise stay in the directory for good.
        var current = paths.Concat(paths.Select(p => p + ".key")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in Directory.EnumerateFiles(directory).Select(Path.GetFullPath).Where(f => !current.Contains(f)).ToList())
            File.Delete(stale);
        var failures = new NativeBuildResult?[sources.Count];
        Parallel.For(0, sources.Count, i => failures[i] = CompileSource(sources[i], paths[i], native, release, outputDirectory, inputs));
        objects = paths;
        return failures.FirstOrDefault(f => f is not null);
    }

    /// <summary>Compiles one native source to <paramref name="obj"/>, unless the object that an earlier build left has the same key.</summary>
    NativeBuildResult? CompileSource(string source, string obj, NativeInputs native, bool release, string outputDirectory, string inputs)
    {
        var startInfo = StartInfo(outputDirectory);
        var args = startInfo.ArgumentList;
        bool c = NativeInputs.IsC(source);
        foreach (var arg in c ? CFlags(release) : CppFlags(release))
            args.Add(arg);
        if (!c)
            args.Add((MsvcStyle ? "/I" : "-I") + RuntimeDirectory);
        AddNativeFlags(args, native, MsvcStyle);
        if (MsvcStyle)
        {
            args.Add("/c");
            args.Add($"/Fo{obj}");
            args.Add(c ? "/Tc" + source : "/Tp" + source);
        }
        else
        {
            args.Add("-c");
            args.Add("-x");
            args.Add(c ? "c" : "c++");
            args.Add(source);
            args.Add("-o");
            args.Add(obj);
        }

        var keyPath = obj + ".key";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{inputs}\n{string.Join('\n', args)}")));
        if (File.Exists(obj) && File.Exists(keyPath) && File.ReadAllText(keyPath) == key)
            return null;
        File.Delete(keyPath);
        var result = ProcessRunner.Run(startInfo);
        if (result.ExitCode != 0)
            return new NativeBuildResult(false, (result.StandardOutput + result.StandardError).Trim());
        File.WriteAllText(keyPath, key);
        return null;
    }

    /// <summary>
    /// Parsing the standard headers dominates compiling a generated program, so every program shares one
    /// precompiled wyl.h per compiler and mode. Null if it can't be built; programs then compile without it.
    /// </summary>
    string? BuildPrecompiledHeader(bool release)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Toolchain}|{compilerPath}")))[..12];
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
            if (Toolchain is Toolchain.Msvc or Toolchain.Clang)
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
