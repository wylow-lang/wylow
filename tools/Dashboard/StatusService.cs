using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Vpl.Compiler.Build;

namespace Vpl.Dashboard;

public sealed record GitDto(string Branch, string Commit, string Subject, int Dirty);
public sealed record ToolchainDto(string Id, string Name, bool Available, string? Error);
public sealed record BuildDto(bool Stale, DateTimeOffset? LastBuilt);
public sealed record SeedDto(int Seed, int Tier, DateTimeOffset At, int Findings, string State, string JobId, string Kind);
public sealed record FindingsDto(int Total, int Vpl, int Logs);

public sealed record StatusDto(
    string Root,
    string Name,
    GitDto? Git,
    string? Dotnet,
    IReadOnlyList<ToolchainDto> Toolchains,
    BuildDto Build,
    int Running,
    int Queued,
    IReadOnlyList<SeedDto> RecentSeeds,
    FindingsDto Findings);

public sealed class StatusService
{
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(4);
    static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".out", ".coverage", ".state", ".git", ".vs",
    };
    static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".h", ".vpl", ".props",
    };

    readonly ServerOptions options;
    readonly JobManager jobs;
    readonly FindingsService findings;
    readonly object gate = new();
    StatusDto? cached;
    DateTimeOffset cachedAt;

    public StatusService(ServerOptions options, JobManager jobs, FindingsService findings)
    {
        this.options = options;
        this.jobs = jobs;
        this.findings = findings;
    }

    public void Warm() => _ = Task.Run(() =>
    {
        try
        {
            Get();
        }
        catch (Exception)
        {
        }
    });

    public StatusDto Get()
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (cached is not null && now - cachedAt < CacheFor)
                return cached;
            cached = Build();
            cachedAt = now;
            return cached;
        }
    }

    StatusDto Build()
    {
        var root = options.Root;
        var (running, queued) = jobs.Counts();
        var (total, vpl, logs) = findings.Counts();
        return new(
            root,
            new DirectoryInfo(root).Name,
            GitInfo(root),
            Run("dotnet", ["--version"], root, 5000),
            Toolchains(),
            BuildInfo(root),
            running,
            queued,
            jobs.RecentSeeds(),
            new FindingsDto(total, vpl, logs));
    }

    static GitDto? GitInfo(string root)
    {
        var commit = Run("git", ["-C", root, "rev-parse", "--short", "HEAD"], root, 4000);
        if (commit is null)
            return null;
        var branch = Run("git", ["-C", root, "rev-parse", "--abbrev-ref", "HEAD"], root, 4000) ?? "detached";
        var subject = Run("git", ["-C", root, "log", "-1", "--format=%s"], root, 4000) ?? "";
        var status = Run("git", ["-C", root, "status", "--porcelain"], root, 6000) ?? "";
        var dirty = status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        return new(branch, commit, subject, dirty);
    }

    static IReadOnlyList<ToolchainDto> Toolchains()
    {
        return
        [
            Probe(Toolchain.Msvc, "msvc", "MSVC"),
            Probe(Toolchain.Gcc, "gcc", "g++"),
            Probe(Toolchain.ClangSanitized, "asan", "clang-cl + ASan/UBSan"),
        ];

        static ToolchainDto Probe(Toolchain toolchain, string id, string name)
        {
            try
            {
                var found = NativeCompiler.Find(toolchain, out var error);
                return new(id, name, found is not null, found is null ? error : null);
            }
            catch (Exception ex)
            {
                return new(id, name, false, ex.Message);
            }
        }
    }

    static BuildDto BuildInfo(string root)
    {
        var testRunner = Path.Combine(root, "tests", "Vpl.Tests", "bin", "Debug", "net10.0", "Vpl.Tests.dll");
        var fuzzer = Path.Combine(root, "tests", "Vpl.Fuzz", "bin", "Debug", "net10.0", "Vpl.Fuzz.dll");
        var built = new List<DateTime>();
        if (File.Exists(testRunner))
            built.Add(File.GetLastWriteTimeUtc(testRunner));
        if (File.Exists(fuzzer))
            built.Add(File.GetLastWriteTimeUtc(fuzzer));
        var lastBuilt = built.Count == 0 ? (DateTime?)null : built.Min();
        if (built.Count < 2)
            return new(true, ToLocal(lastBuilt));

        var newest = NewestSource(root);
        return new(newest > lastBuilt!.Value, ToLocal(lastBuilt));
    }

    static DateTimeOffset? ToLocal(DateTime? utc) =>
        utc is { } value ? new DateTimeOffset(value, TimeSpan.Zero).ToLocalTime() : null;

    static DateTime NewestSource(string root)
    {
        var newest = DateTime.MinValue;
        foreach (var directory in new[] { "src", "runtime", "tests/Vpl.Tests", "tests/Vpl.Fuzz" })
        {
            var path = Path.Combine(root, directory);
            if (!Directory.Exists(path))
                continue;
            foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                     }))
            {
                var relative = Path.GetRelativePath(root, file);
                if (relative.Split(Path.DirectorySeparatorChar, '/').Any(SkippedDirectories.Contains))
                    continue;
                if (!SourceExtensions.Contains(Path.GetExtension(file)))
                    continue;
                var written = File.GetLastWriteTimeUtc(file);
                if (written > newest)
                    newest = written;
            }
        }
        foreach (var file in new[] { "Directory.Build.props", "vpl.slnx" })
        {
            var path = Path.Combine(root, file);
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > newest)
                newest = File.GetLastWriteTimeUtc(path);
        }
        return newest;
    }

    public static string? Run(string file, string[] arguments, string workingDirectory, int timeoutMilliseconds)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = file,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }
        }
        var standardOutput = output.GetAwaiter().GetResult();
        _ = error.GetAwaiter().GetResult();
        return process.HasExited && process.ExitCode == 0 ? standardOutput.Trim() : null;
    }
}
