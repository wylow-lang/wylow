using System.Collections.Concurrent;
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
    public string BuildKey(bool release, string cpp, string? nativeDirectory = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{Identity(release)}\n{cpp}"));
        foreach (var (path, bytes) in NativeFiles(nativeDirectory))
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"\n{path}\n{bytes.Length}\n"));
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Every file under a package's <c>native/</c> directory, by its path there, with its bytes, in a stable order.</summary>
    public static IEnumerable<(string Path, byte[] Bytes)> NativeFiles(string? nativeDirectory) => nativeDirectory is null
        ? []
        : Directory.EnumerateFiles(nativeDirectory, "*", SearchOption.AllDirectories)
            .Select(f => (Path: Path.GetRelativePath(nativeDirectory, f).Replace('\\', '/'), File: f))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => (f.Path, File.ReadAllBytes(f.File)));

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

    public static NativeCompiler? Find(Toolchain? preferred, out string? error)
    {
        error = null;
        if ((preferred is null or Toolchain.Clang) && FindClang() is { } clang)
            return clang;
        if ((preferred is null or Toolchain.Msvc) && FindMsvc() is { } msvc)
            return msvc;
        if ((preferred is null or Toolchain.Gcc) && FindGcc() is { } gcc)
            return gcc;
        if (preferred is Toolchain.ClangSanitized && FindClangSanitized() is { } sanitized)
            return sanitized;
        if (preferred is Toolchain.ThreadSanitized && ThreadSanitized.Value is { } threaded)
            return threaded;

        error = preferred switch
        {
            Toolchain.Clang or Toolchain.ClangSanitized =>
                "could not find clang-cl on PATH, or MSVC, whose headers and libraries it uses",
            Toolchain.Msvc => "could not find MSVC; install Visual Studio with the 'Desktop development with C++' workload",
            Toolchain.Gcc => "could not find g++ on PATH",
            Toolchain.ThreadSanitized => "no C++ toolchain here builds a program with ThreadSanitizer",
            _ => "no C++ compiler found; install clang-cl, or Visual Studio with the 'Desktop development with C++' workload, or g++",
        };
        return null;
    }

    static NativeCompiler? FindClang()
    {
        if (!OperatingSystem.IsWindows() || MsvcEnvironment.Value is not { } env)
            return null;
        var clang = FindOnPath("clang-cl.exe", Environment.GetEnvironmentVariable("PATH"));
        return clang is null ? null : new NativeCompiler(Toolchain.Clang, clang, env);
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

    /// <summary>
    /// Builds the executable from the generated C++ and, if <paramref name="nativeDirectory"/> is
    /// given, every <c>.cpp</c> file under it, with that directory on the include path (spec
    /// <c>interop.native</c>). A native source does not include <c>wyl.h</c>, so where the program
    /// uses the precompiled header, the native sources compile in a call of their own first.
    /// </summary>
    public NativeBuildResult Build(string cppPath, string exePath, bool release, bool precompiledHeader = true, string? nativeDirectory = null)
    {
        var nativeSources = nativeDirectory is null
            ? []
            : Directory.GetFiles(nativeDirectory, "*.cpp", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        var nativeObjects = new List<string>();
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
        if (Toolchain is Toolchain.Msvc or Toolchain.Clang)
        {
            foreach (var arg in new[] { "/nologo", "/std:c++20", "/permissive-", "/utf-8", "/EHsc", "/W3" })
                args.Add(arg);
            if (Toolchain == Toolchain.Clang)
                args.Add("/D_ALLOW_COMPILER_AND_STL_VERSION_MISMATCH");
            args.Add(release ? "/O2" : "/Od");
            if (!release)
                args.Add("/DWYL_DEBUG=1");
            args.Add($"/I{RuntimeDirectory}");
            if (nativeDirectory is not null)
            {
                args.Add($"/I{nativeDirectory}");
                if (CompileNative(startInfo, nativeSources, exePath, nativeObjects) is { } failed)
                    return failed;
            }
            var precompiled = precompiledHeader ? PrecompiledDirectory(release) : null;
            if (precompiled is not null)
            {
                args.Add("/Yuwyl.h");
                args.Add($"/Fp{Path.Combine(precompiled, "wyl.pch")}");
            }
            args.Add($"/Fe{exePath}");
            if (Toolchain == Toolchain.Clang && precompiled is not null)
                args.Add($"/Fo{Path.GetDirectoryName(Path.GetFullPath(exePath))}{Path.DirectorySeparatorChar}");
            else
                args.Add($"/Fo{Path.ChangeExtension(exePath, ".obj")}");
            args.Add(cppPath);
            if (precompiled is not null)
                args.Add(Path.Combine(precompiled, "wyl.obj"));
            foreach (var native in nativeObjects)
                args.Add(native);
        }
        else if ((Toolchain is Toolchain.ClangSanitized or Toolchain.ThreadSanitized) && environment is not null)
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
            if (nativeDirectory is not null)
            {
                args.Add($"/I{nativeDirectory}");
                if (CompileNative(startInfo, nativeSources, exePath, nativeObjects) is { } failed)
                    return failed;
            }
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
            foreach (var native in nativeObjects)
                args.Add(native);
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
            if (nativeDirectory is not null)
                args.Add($"-I{nativeDirectory}");
            args.Add("-o");
            args.Add(exePath);
            args.Add(cppPath);
            foreach (var native in nativeSources)
                args.Add(native);
        }

        var result = ProcessRunner.Run(startInfo);
        return new NativeBuildResult(result.ExitCode == 0, (result.StandardOutput + result.StandardError).Trim());
    }

    /// <summary>
    /// Compiles the native sources to objects in a directory beside the executable, with the flags
    /// that <paramref name="program"/> has so far, which are the program's own up to its outputs and
    /// inputs. Returns the failure, or null after adding the objects to <paramref name="objects"/>.
    /// </summary>
    static NativeBuildResult? CompileNative(ProcessStartInfo program, IReadOnlyList<string> sources, string exePath, List<string> objects)
    {
        if (sources.Count == 0)
            return null;
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exePath))!, "native-objects");
        Directory.CreateDirectory(directory);
        // Each source compiles on its own, so that two of one name in different directories get two objects.
        for (int i = 0; i < sources.Count; i++)
        {
            var startInfo = new ProcessStartInfo(program.FileName) { WorkingDirectory = program.WorkingDirectory };
            startInfo.Environment.Clear();
            foreach (var (name, value) in program.Environment)
                startInfo.Environment[name] = value;
            foreach (var arg in program.ArgumentList.Where(a => a != "-fuse-ld=lld"))
                startInfo.ArgumentList.Add(arg);
            var obj = Path.Combine(directory, $"{i}_{Path.GetFileNameWithoutExtension(sources[i])}.obj");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add($"/Fo{obj}");
            startInfo.ArgumentList.Add(sources[i]);
            var result = ProcessRunner.Run(startInfo);
            if (result.ExitCode != 0)
                return new NativeBuildResult(false, (result.StandardOutput + result.StandardError).Trim());
            objects.Add(obj);
        }
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
                if (Toolchain == Toolchain.Clang)
                    args.Add("/D_ALLOW_COMPILER_AND_STL_VERSION_MISMATCH");
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
