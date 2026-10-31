using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace Wyl.Mutation;

sealed record ProcessOutcome(int ExitCode, bool TimedOut, string Output, string Error)
{
    public string All => Error.Length > 0 ? Output + "\n" + Error : Output;
}

enum MutantState { Killed, Survived, Invalid, TimedOut }

sealed record MutantOutcome(MutantState State, string Detail, double Seconds);

/// <summary>
/// Runs one mutant at a time, the way the compiler's own tests would: build a private copy of the
/// file's project with the mutated source over the source tree, put only the resulting assembly
/// into a private copy of the test runner, and run the covering tests. Neither the working tree nor
/// the build output the rest of the tooling uses is touched, so a killed run leaves nothing behind.
/// </summary>
sealed class MutationRunner
{
    const int OutputCap = 1 << 21;

    readonly string root;
    readonly string runnerDirectory;
    readonly string projectDirectory;
    readonly string projectPath;
    readonly string mutatedSourcePath;
    readonly string assemblyName;
    readonly IReadOnlyList<string> filters;
    readonly IReadOnlyList<string> arguments;
    readonly TimeSpan buildTimeout;
    readonly TimeSpan testTimeout;

    public MutationRunner(string root, string sourceProjectPath, string sourceFile, IReadOnlyList<string> filters,
        IReadOnlyList<string> arguments, TimeSpan buildTimeout, TimeSpan testTimeout)
    {
        this.root = root;
        this.filters = filters;
        this.arguments = arguments;
        this.buildTimeout = buildTimeout;
        this.testTimeout = testTimeout;

        var sourceProjectDirectory = Path.GetDirectoryName(sourceProjectPath)!;
        assemblyName = AssemblyNameOf(sourceProjectPath);
        var relative = Path.GetRelativePath(sourceProjectDirectory, sourceFile);
        var mutationDirectory = Path.Combine(root, "tests", ".out", "mutation");
        runnerDirectory = Path.Combine(mutationDirectory, "runner");
        projectDirectory = Path.Combine(mutationDirectory, "src", assemblyName);
        projectPath = Path.Combine(projectDirectory, Path.GetFileName(sourceProjectPath));
        mutatedSourcePath = Path.Combine(projectDirectory, relative);
        SourceDirectory = sourceProjectDirectory;
    }

    public string SourceDirectory { get; }

    /// <summary>Builds the runner on request, then copies it and the project it builds into a private directory.</summary>
    public void Prepare(bool build)
    {
        if (build)
        {
            var result = Run("dotnet", ["build", Path.Combine("tests", "Wyl.Tests", "Wyl.Tests.csproj"), "--nologo", "-v", "minimal"],
                root, TimeSpan.FromMinutes(10));
            if (result.TimedOut || result.ExitCode != 0)
                throw new InvalidOperationException("building the test runner failed:\n" + Tail(result.All));
        }

        CopyRunner();
        CopyProject();
    }

    /// <summary>The tests must pass on the untouched compiler before a mutant's failure means anything.</summary>
    public void Baseline()
    {
        foreach (var filter in filters)
        {
            // The runner takes one filter, so each is a run of its own, as it is for a mutant.
            List<string> runArguments = [Path.Combine(runnerDirectory, "Wyl.Tests.dll"), .. arguments];
            if (filter.Length > 0)
                runArguments.Add(filter);
            var result = Run("dotnet", runArguments, root, testTimeout);
            if (result.TimedOut)
                throw new InvalidOperationException($"the baseline run of '{filter}' timed out");
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"the baseline run of '{filter}' failed:\n" + Tail(result.All));
            Console.WriteLine($"baseline: {filter} - {Summary(result.All)}");
        }
    }

    public MutantOutcome RunMutant(string text)
    {
        var stopwatch = Stopwatch.StartNew();
        File.WriteAllText(mutatedSourcePath, text);
        var build = Run("dotnet",
            ["build", projectPath, "--nologo", "-v", "minimal", "-p:BuildProjectReferences=false", "-p:TreatWarningsAsErrors=false"],
            root, buildTimeout);
        if (build.TimedOut)
            return new(MutantState.Invalid, "the build timed out", stopwatch.Elapsed.TotalSeconds);
        if (build.ExitCode != 0)
        {
            if (build.All.Contains("error CS", StringComparison.Ordinal))
                return new(MutantState.Invalid, FirstError(build.All), stopwatch.Elapsed.TotalSeconds);
            throw new InvalidOperationException("building the mutant failed for a reason that isn't the mutation:\n" + Tail(build.All));
        }

        var dll = Path.Combine(projectDirectory, "bin", "Debug", "net10.0", assemblyName + ".dll");
        if (!File.Exists(dll))
            throw new InvalidOperationException($"the mutant built but wrote no {assemblyName}.dll");
        File.Copy(dll, Path.Combine(runnerDirectory, assemblyName + ".dll"), true);

        foreach (var filter in filters)
        {
            List<string> runArguments = [Path.Combine(runnerDirectory, "Wyl.Tests.dll"), .. arguments];
            if (filter.Length > 0)
                runArguments.Add(filter);
            var run = Run("dotnet", runArguments, root, testTimeout);
            var seconds = stopwatch.Elapsed.TotalSeconds;
            if (run.TimedOut)
                return new(MutantState.TimedOut, $"the tests timed out on '{filter}'", seconds);
            if (run.ExitCode != 0)
                return new(MutantState.Killed, FailureDetail(run, filter), seconds);
        }
        return new(MutantState.Survived, "", stopwatch.Elapsed.TotalSeconds);
    }

    /// <summary>Puts the compiler's own assembly back into the private runner, so a later run starts clean.</summary>
    public void Restore()
    {
        var original = Path.Combine(root, "tests", "Wyl.Tests", "bin", "Debug", "net10.0", assemblyName + ".dll");
        if (Directory.Exists(runnerDirectory) && File.Exists(original))
            File.Copy(original, Path.Combine(runnerDirectory, assemblyName + ".dll"), true);
    }

    void CopyRunner()
    {
        var source = Path.Combine(root, "tests", "Wyl.Tests", "bin", "Debug", "net10.0");
        if (!File.Exists(Path.Combine(source, "Wyl.Tests.dll")))
            throw new InvalidOperationException("the test runner is not built; build tests/Wyl.Tests first");
        Directory.CreateDirectory(runnerDirectory);
        CopyDirectory(source, runnerDirectory, ["pch"]);
        // The precompiled header is 250 MB; it is the same for every target, so it is copied once.
        if (!Directory.Exists(Path.Combine(runnerDirectory, "pch")) && Directory.Exists(Path.Combine(source, "pch")))
            CopyDirectory(Path.Combine(source, "pch"), Path.Combine(runnerDirectory, "pch"), []);
    }

    void CopyProject()
    {
        if (Directory.Exists(projectDirectory))
            Directory.Delete(projectDirectory, recursive: true);
        CopyDirectory(SourceDirectory, projectDirectory, ["bin", "obj"]);
        // The copy builds beside the tree, so its references to the real projects must not be relative.
        var document = XDocument.Load(projectPath);
        foreach (var reference in document.Descendants().Where(element => element.Name.LocalName == "ProjectReference"))
        {
            if (reference.Attribute("Include")?.Value is { } include)
                reference.SetAttributeValue("Include", Path.GetFullPath(Path.Combine(SourceDirectory, include)));
        }
        document.Save(projectPath);
    }

    static string AssemblyNameOf(string projectPath)
    {
        var declared = XDocument.Load(projectPath).Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "AssemblyName")?.Value;
        return string.IsNullOrWhiteSpace(declared) ? Path.GetFileNameWithoutExtension(projectPath) : declared;
    }

    static void CopyDirectory(string source, string target, string[] skipped)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (skipped.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            CopyDirectory(directory, Path.Combine(target, name), skipped);
        }
    }

    static ProcessOutcome Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
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
        var output = new StringBuilder();
        var error = new StringBuilder();
        var gate = new object();
        process.OutputDataReceived += (_, line) =>
        {
            if (line.Data is not null)
                lock (gate)
                    Append(output, line.Data);
        };
        process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is not null)
                lock (gate)
                    Append(error, line.Data);
        };

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"could not start '{fileName}': {exception.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        bool exited = process.WaitForExit((int)timeout.TotalMilliseconds);
        if (!exited)
            Kill(process);
        process.WaitForExit();
        return new(exited ? process.ExitCode : -1, !exited, output.ToString(), error.ToString());
    }

    static void Append(StringBuilder builder, string line)
    {
        if (builder.Length < OutputCap)
            builder.AppendLine(line);
    }

    static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }

    static string FirstError(string text) =>
        text.Split('\n').FirstOrDefault(line => line.Contains("error CS", StringComparison.Ordinal))?.Trim() ?? "the build failed";

    static string FailureDetail(ProcessOutcome outcome, string filter)
    {
        var lines = outcome.All.Split('\n');
        var failure = lines.FirstOrDefault(line => line.StartsWith("FAIL ", StringComparison.Ordinal) && !line.StartsWith("FAIL unknown", StringComparison.Ordinal));
        var summary = lines.LastOrDefault(line => line.Contains(" passed, ", StringComparison.Ordinal));
        var detail = failure ?? summary ?? $"exit {outcome.ExitCode}";
        return filter.Length > 0 ? $"{filter}: {detail.Trim()}" : detail.Trim();
    }

    static string Summary(string text) =>
        text.Split('\n').LastOrDefault(line => line.Contains(" passed, ", StringComparison.Ordinal))?.Trim() ?? "ran";

    static string Tail(string text)
    {
        var lines = text.Trim().Split('\n');
        return string.Join('\n', lines.Length <= 20 ? lines : lines[^20..]);
    }
}
