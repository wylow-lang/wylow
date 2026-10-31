using System.Diagnostics;

namespace Wyl.Compiler.Build;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

public static class ProcessRunner
{
    public static ProcessResult Run(ProcessStartInfo startInfo, TimeSpan? timeout = null)
    {
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"could not start '{startInfo.FileName}'");
        // Read both streams concurrently; reading one to the end first can deadlock on a full pipe.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        bool exited = process.WaitForExit(timeout is null ? Timeout.Infinite : (int)timeout.Value.TotalMilliseconds);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        return new ProcessResult(exited ? process.ExitCode : -1, stdout.Result, stderr.Result, !exited);
    }
}
