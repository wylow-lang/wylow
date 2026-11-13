using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Wyl.Compiler.Build;

public sealed record NativeBuildResult(bool Success, string Output);

/// <summary>
/// One C++ compiler, which compiles and links a program from its generated C++ and the native
/// sources of its packages. <see cref="Toolchains.Find"/> makes one.
/// </summary>
public sealed class NativeCompiler
{
    static readonly ConcurrentDictionary<string, Lazy<string?>> PrecompiledHeaders = new();

    static readonly string[] SanitizedFlags = ["/nologo", "/std:c++20", "/utf-8", "/EHsc", "-fsanitize=address,undefined"];

    static readonly string[] ThreadSanitizedClangFlags = ["/nologo", "/std:c++20", "/utf-8", "/EHsc", "-fsanitize=thread", "-g"];

    // The runtime frees pool slots with sized operator delete, which clang++ before 19 declares only
    // with -fsized-deallocation; g++ accepts the flag and enables it anyway.
    static readonly string[] ThreadSanitizedGccFlags = ["-std=c++20", "-fsanitize=thread", "-fsized-deallocation", "-g", "-pthread"];

    readonly string compilerPath;
    readonly IReadOnlyDictionary<string, string>? environment;

    internal NativeCompiler(Toolchain toolchain, string compilerPath, IReadOnlyDictionary<string, string>? environment)
    {
        Toolchain = toolchain;
        this.compilerPath = compilerPath;
        this.environment = environment;
    }

    public Toolchain Toolchain { get; }

    internal string CompilerPath => compilerPath;

    public static string RuntimeDirectory => Path.Combine(AppContext.BaseDirectory, "runtime");

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
    /// Builds the executable from the generated C++ and every native source of every package, and
    /// links the libraries that the packages name (spec <c>interop.native</c>, <c>programs.manifest</c>).
    /// The generated C++ compiles with the include directories and the definitions of every package.
    /// Each native source compiles to an object of its own first (<see cref="CompileNative"/>). A definition
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
            AddNativeFlags(args, native.IncludeDirectories, native.Defines, msvc: true);
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
            AddNativeFlags(args, native.IncludeDirectories, native.Defines, msvc: false);
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

    static void AddNativeFlags(Collection<string> args, IEnumerable<string> includeDirectories, IEnumerable<string> defines, bool msvc)
    {
        foreach (var directory in includeDirectories)
            args.Add((msvc ? "/I" : "-I") + directory);
        foreach (var define in defines)
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
    /// Compiles each native source of every package to an object in the directory
    /// <c>native-objects</c> of <paramref name="outputDirectory"/>: a C source as C17 and a C++
    /// source as C++20, with the flags of the program's toolchain and mode (decision 0099), and with
    /// the include directories and the definitions of the source's own package. An object whose key
    /// file matches is kept from an earlier build. Its key covers the toolchain, the mode, the
    /// compiler's arguments, the package's lists, and every file that the source can include, which
    /// is every file of the package's <see cref="PackageNative.IncludableFiles"/>. A source's warnings
    /// appear only if it fails to compile. Returns the failure of the first source in order that
    /// fails, or null.
    /// </summary>
    NativeBuildResult? CompileNative(NativeInputs native, bool release, string outputDirectory, out List<string> objects)
    {
        var sources = native.Sources;
        objects = [];
        if (sources.Count == 0)
            return null;
        var directory = Path.Combine(outputDirectory, "native-objects");
        Directory.CreateDirectory(directory);

        var inputs = native.Packages.ToDictionary(p => p, p =>
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.UTF8.GetBytes($"{BuildKeys.Identity(this, release)}\n{p.Text}"));
            foreach (var (path, bytes) in p.IncludableFiles())
            {
                hash.AppendData(Encoding.UTF8.GetBytes($"\n{path}\n{bytes.Length}\n"));
                hash.AppendData(bytes);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        });

        var extension = MsvcStyle ? ".obj" : ".o";
        // Each source has an object of its own, so that two of one name in different directories get two objects.
        var paths = sources.Select((source, i) => Path.Combine(directory, $"{i}_{Path.GetFileNameWithoutExtension(source.Path)}{extension}")).ToList();
        // An object of a source that was removed or renamed would otherwise stay in the directory for good.
        var current = paths.Concat(paths.Select(p => p + ".key")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in Directory.EnumerateFiles(directory).Select(Path.GetFullPath).Where(f => !current.Contains(f)).ToList())
            File.Delete(stale);
        var failures = new NativeBuildResult?[sources.Count];
        Parallel.For(0, sources.Count, i => failures[i] = CompileSource(sources[i].Path, paths[i], sources[i].Package, release, outputDirectory, inputs[sources[i].Package]));
        objects = paths;
        return failures.FirstOrDefault(f => f is not null);
    }

    /// <summary>Compiles one native source to <paramref name="obj"/>, unless the object that an earlier build left has the same key.</summary>
    NativeBuildResult? CompileSource(string source, string obj, PackageNative package, bool release, string outputDirectory, string inputs)
    {
        var startInfo = StartInfo(outputDirectory);
        var args = startInfo.ArgumentList;
        bool c = NativeInputs.IsC(source);
        foreach (var arg in c ? CFlags(release) : CppFlags(release))
            args.Add(arg);
        if (!c)
            args.Add((MsvcStyle ? "/I" : "-I") + RuntimeDirectory);
        AddNativeFlags(args, package.IncludeDirectories, package.Defines, MsvcStyle);
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
