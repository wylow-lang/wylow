using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Wyl.Compiler.Build;

/// <summary>
/// The environment that vcvars64.bat sets up, which cl.exe works only inside. Running vcvars64.bat
/// takes seconds, so the environment it set up is kept on disk. The kept environment is used again
/// while the installation and the variables vcvars64.bat reads are unchanged, and while every
/// include and library directory it names still exists.
/// </summary>
static class MsvcEnvironment
{
    static readonly Lazy<IReadOnlyDictionary<string, string>?> Captured = new(Capture);

    /// <summary>The MSVC environment, or null if this is not Windows or its toolset is not supported.</summary>
    internal static IReadOnlyDictionary<string, string>? Get() =>
        OperatingSystem.IsWindows()
        && Captured.Value is { } env
        && ToolchainVersions.IsSupported(ToolsetVersion(env), ToolchainVersions.MsvcToolset)
            ? env
            : null;

    internal static string? ToolsetVersion(IReadOnlyDictionary<string, string> env) => env.GetValueOrDefault("VCToolsVersion");

    /// <summary>Why <see cref="Get"/> is null, for a compiler that needs the MSVC environment.</summary>
    internal static string Refusal(string compiler)
    {
        if (!OperatingSystem.IsWindows())
            return $"{compiler} needs MSVC, which exists only on Windows";
        if (Captured.Value is not { } env)
            return $"could not find MSVC, which {compiler} needs; install Visual Studio with the 'Desktop development with C++' workload";
        return $"the MSVC toolset is version {ToolsetVersion(env) ?? "unknown"}, and wyl needs {ToolchainVersions.MsvcToolset} or newer; update Visual Studio";
    }

    static IReadOnlyDictionary<string, string>? Capture()
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

        var key = EnvironmentKey(installation, vcvars);
        if (ReadEnvironment(key) is { } kept)
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
        WriteEnvironment(key, env);
        return env;
    }

    static string EnvironmentFilePath => Path.Combine(AppContext.BaseDirectory, "msvc-environment.json");

    sealed record KeptEnvironment(string Key, Dictionary<string, string> Environment);

    // The variables that vcvars64.bat appends to or takes its settings from, by name and by prefix.
    static readonly string[] VcvarsReads = ["PATH", "INCLUDE", "LIB", "LIBPATH", "EXTERNAL_INCLUDE", "VSINSTALLDIR", "VCINSTALLDIR"];
    static readonly string[] VcvarsReadPrefixes = ["VSCMD_", "VCTOOLS", "WINDOWSSDK", "UCRT"];

    /// <summary>
    /// Everything the environment vcvars64.bat sets up depends on: the installation, its default
    /// toolset, and the variables of this process that vcvars64.bat reads.
    /// </summary>
    static string EnvironmentKey(string installation, string vcvars)
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
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    static IReadOnlyDictionary<string, string>? ReadEnvironment(string key)
    {
        try
        {
            if (!File.Exists(EnvironmentFilePath)
                || JsonSerializer.Deserialize<KeptEnvironment>(File.ReadAllText(EnvironmentFilePath)) is not { } kept
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

    static void WriteEnvironment(string key, Dictionary<string, string> env)
    {
        // Another process may be writing the same file, so each process writes its own and moves it into place.
        var temporary = $"{EnvironmentFilePath}.{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new KeptEnvironment(key, env)));
            File.Move(temporary, EnvironmentFilePath, overwrite: true);
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
}
