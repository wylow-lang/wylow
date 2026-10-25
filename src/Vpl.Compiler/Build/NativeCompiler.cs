using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Vpl.Compiler.Build;

public enum Toolchain { Msvc, Gcc }

public sealed record NativeBuildResult(bool Success, string Output);

public sealed class NativeCompiler
{
    static readonly ConcurrentDictionary<string, Lazy<(string Pch, string Obj)?>> PrecompiledHeaders = new();
    static readonly Lazy<IReadOnlyDictionary<string, string>?> MsvcEnvironment = new(CaptureMsvcEnvironment);

    readonly string compilerPath;
    readonly IReadOnlyDictionary<string, string>? environment;

    NativeCompiler(Toolchain toolchain, string compilerPath, IReadOnlyDictionary<string, string>? environment)
    {
        Toolchain = toolchain;
        this.compilerPath = compilerPath;
        this.environment = environment;
    }

    public Toolchain Toolchain { get; }

    public static string RuntimeDirectory => Path.Combine(AppContext.BaseDirectory, "runtime");

    public static NativeCompiler? Find(Toolchain? preferred, out string? error)
    {
        error = null;
        if (preferred is null or Toolchain.Msvc && FindMsvc() is { } msvc)
            return msvc;
        if (preferred is null or Toolchain.Gcc && FindGcc() is { } gcc)
            return gcc;

        error = preferred switch
        {
            Toolchain.Msvc => "could not find MSVC; install Visual Studio with the 'Desktop development with C++' workload",
            Toolchain.Gcc => "could not find g++ on PATH",
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

    public NativeBuildResult Build(string cppPath, string exePath, bool release)
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
                args.Add("/DVPL_DEBUG=1");
            args.Add($"/I{RuntimeDirectory}");
            var precompiled = PrecompiledHeaders.GetOrAdd(
                $"{compilerPath}|{release}", _ => new(() => BuildPrecompiledHeader(release))).Value;
            if (precompiled is var (pch, pchObject))
            {
                args.Add("/Yuvpl.h");
                args.Add($"/Fp{pch}");
            }
            args.Add($"/Fe{exePath}");
            args.Add($"/Fo{Path.ChangeExtension(exePath, ".obj")}");
            args.Add(cppPath);
            if (precompiled is not null)
                args.Add(precompiled.Value.Obj);
        }
        else
        {
            args.Add("-std=c++20");
            args.Add(release ? "-O2" : "-O0");
            if (!release)
                args.Add("-DVPL_DEBUG=1");
            // Avoids depending on MinGW's runtime DLLs being on PATH when the program runs.
            if (OperatingSystem.IsWindows())
                args.Add("-static");
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
    /// precompiled vpl.h per compiler and mode. Null if it can't be built; programs then compile without it.
    /// </summary>
    (string Pch, string Obj)? BuildPrecompiledHeader(bool release)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(compilerPath)))[..12];
        var directory = Path.Combine(AppContext.BaseDirectory, "pch", key, release ? "release" : "debug");
        var pch = Path.Combine(directory, "vpl.pch");
        var obj = Path.Combine(directory, "vpl.obj");

        // The mutex serializes concurrent test runs and CLI invocations building the same files.
        using var mutex = new Mutex(false, "Global\vpl-pch-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)))[..16]);
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { }
        try
        {
            var newestHeader = Directory.EnumerateFiles(RuntimeDirectory, "*.h", SearchOption.AllDirectories)
                .Max(f => File.GetLastWriteTimeUtc(f));
            if (File.Exists(pch) && File.Exists(obj) && File.GetLastWriteTimeUtc(pch) > newestHeader)
                return (pch, obj);

            Directory.CreateDirectory(directory);
            var stub = Path.Combine(directory, "vpl.cpp");
            File.WriteAllText(stub, "#include \"vpl.h\"\n");
            var startInfo = new ProcessStartInfo(compilerPath) { WorkingDirectory = directory };
            startInfo.Environment.Clear();
            foreach (var (name, value) in environment!)
                startInfo.Environment[name] = value;
            foreach (var arg in new[] { "/nologo", "/std:c++20", "/permissive-", "/utf-8", "/EHsc", "/W3", "/c" })
                startInfo.ArgumentList.Add(arg);
            startInfo.ArgumentList.Add(release ? "/O2" : "/Od");
            if (!release)
                startInfo.ArgumentList.Add("/DVPL_DEBUG=1");
            startInfo.ArgumentList.Add($"/I{RuntimeDirectory}");
            startInfo.ArgumentList.Add("/Ycvpl.h");
            startInfo.ArgumentList.Add($"/Fp{pch}");
            startInfo.ArgumentList.Add($"/Fo{obj}");
            startInfo.ArgumentList.Add(stub);
            return ProcessRunner.Run(startInfo).ExitCode == 0 ? (pch, obj) : null;
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
