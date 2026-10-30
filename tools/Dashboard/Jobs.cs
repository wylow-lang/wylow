using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Vpl.Dashboard;

public enum JobState { Queued, Running, Succeeded, Findings, Failed, Cancelled, Interrupted }

public sealed class JobMetrics
{
    public int? Passed { get; set; }
    public int? Failed { get; set; }
    public double? Seconds { get; set; }
    public int? Mutants { get; set; }
    public int? Compiled { get; set; }
    public int? Findings { get; set; }
    public int? Killed { get; set; }
    public int? Survived { get; set; }
    public int? Equivalent { get; set; }
    public int? Invalid { get; set; }
    public int? TimedOut { get; set; }
    public double? Score { get; set; }
    public string? Rate { get; set; }
    public List<string> Failures { get; set; } = [];
    public List<string> FindingKeys { get; set; } = [];
}

public sealed record JobLine(long Seq, string Text);

public abstract record JobEvent;
public sealed record LineEvent(long Seq, string Text, JobMetrics Metrics) : JobEvent;
public sealed record DoneEvent(JobDto Job) : JobEvent;

public sealed record JobDto(
    string Id,
    string Kind,
    string Title,
    JobState State,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<string> Commands,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    int? ExitCode,
    JobMetrics Metrics,
    bool CanCancel,
    int LineCount,
    string? Error);

public sealed record StreamHandle(
    JobDto Job,
    IReadOnlyList<JobLine> Lines,
    IReadOnlyList<string> Commands,
    bool Finished,
    ChannelReader<JobEvent> Reader,
    ChannelWriter<JobEvent> Writer);

public sealed class Job
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public required IReadOnlyDictionary<string, string> Parameters { get; init; }
    public required IReadOnlyList<StepSpec> Steps { get; init; }
    public required bool RequiresIdle { get; init; }
    public required string LogPath { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public JobSpec? Spec { get; init; }
    public JobState State { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
    public JobMetrics Metrics { get; set; } = new();
    internal List<string> Lines { get; } = [];
    internal List<ChannelWriter<JobEvent>> Subscribers { get; } = [];
    internal StreamWriter? Log { get; set; }
    internal Process? Process { get; set; }
    internal bool CancelRequested { get; set; }
}

public sealed record StartResult(JobDto? Job, string? Error);

/// <summary>
/// Runs the repository's own tools as child processes. Every job is a list of argv steps built
/// from a <see cref="JobSpec"/>; nothing the client sends reaches a shell.
/// </summary>
public sealed class JobManager : IDisposable
{
    const int MaxConcurrent = 3;
    const int MaxHistory = 200;
    static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    readonly object gate = new();
    readonly List<JobSpec> specList;
    readonly Dictionary<string, JobSpec> specs;
    readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);
    readonly List<Job> order = [];
    readonly List<Job> queue = [];
    readonly string root;
    readonly string logDirectory;
    readonly string jobsFile;
    bool disposed;

    public JobManager(ServerOptions options)
    {
        root = options.Root;
        specList = [.. JobSpecs.All(root)];
        specs = specList.ToDictionary(s => s.Kind, StringComparer.Ordinal);
        var stateDirectory = Path.Combine(root, "tools", "Dashboard", ".state");
        logDirectory = Path.Combine(stateDirectory, "logs");
        jobsFile = Path.Combine(stateDirectory, "jobs.json");
        Directory.CreateDirectory(logDirectory);
        Load();
    }

    public IReadOnlyList<JobKindDto> Kinds() =>
        specList.Select(s => new JobKindDto(s.Kind, s.Title, s.Group, s.Description, s.Hint, s.Parameters)).ToArray();

    public StartResult Start(string kind, IReadOnlyDictionary<string, string> input)
    {
        if (!specs.TryGetValue(kind, out var spec))
            return new(null, $"unknown job kind '{kind}'");

        Dictionary<string, string> values;
        IReadOnlyList<StepSpec> steps;
        bool requiresIdle;
        try
        {
            values = JobSpecs.Validate(spec, input);
            var view = new JobValues(values);
            steps = spec.Build(view);
            requiresIdle = spec.AlwaysIdle || (spec.Idle?.Invoke(view) ?? false);
        }
        catch (JobInputException ex)
        {
            return new(null, ex.Message);
        }

        lock (gate)
        {
            var id = NewId();
            var job = new Job
            {
                Id = id,
                Kind = spec.Kind,
                Title = spec.Title,
                Parameters = values,
                Steps = steps,
                RequiresIdle = requiresIdle,
                LogPath = Path.Combine(logDirectory, id + ".log"),
                CreatedAt = DateTimeOffset.Now,
                Spec = spec,
                State = JobState.Queued,
            };
            jobs[id] = job;
            order.Add(job);
            queue.Add(job);
            Persist();
            Pump();
            return new(ToDto(job), null);
        }
    }

    public (IReadOnlyList<string> Commands, string? Error) Preview(string kind, IReadOnlyDictionary<string, string> input)
    {
        if (!specs.TryGetValue(kind, out var spec))
            return ([], $"unknown job kind '{kind}'");
        try
        {
            var values = JobSpecs.Validate(spec, input);
            var steps = spec.Build(new JobValues(values));
            return (steps.Select(JobSpecs.FormatCommand).ToArray(), null);
        }
        catch (JobInputException ex)
        {
            return ([], ex.Message);
        }
    }

    public IReadOnlyList<JobDto> List()
    {
        lock (gate)
            return order.OrderByDescending(j => j.CreatedAt).Select(ToDto).ToArray();
    }

    public JobDto? Get(string id)
    {
        lock (gate)
            return jobs.TryGetValue(id, out var job) ? ToDto(job) : null;
    }

    public string? GetLog(string id, int tail)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var job))
                return null;
            IEnumerable<string> lines = job.Lines.Count > 0 ? job.Lines : ReadLog(job.LogPath);
            return string.Join('\n', lines.TakeLast(Math.Clamp(tail, 1, 20000)));
        }
    }

    public StreamHandle? OpenStream(string id)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var job))
                return null;
            var lines = job.Lines.Count > 0
                ? job.Lines.Select((text, index) => new JobLine(index, text)).ToArray()
                : ReadLog(job.LogPath).Select((text, index) => new JobLine(index, text)).ToArray();
            var channel = Channel.CreateUnbounded<JobEvent>();
            job.Subscribers.Add(channel.Writer);
            return new StreamHandle(
                ToDto(job),
                lines,
                CommandsOf(job),
                job.State is not (JobState.Queued or JobState.Running),
                channel.Reader,
                channel.Writer);
        }
    }

    public void CloseStream(string id, ChannelWriter<JobEvent> writer)
    {
        lock (gate)
        {
            if (jobs.TryGetValue(id, out var job))
                job.Subscribers.Remove(writer);
        }
        writer.TryComplete();
    }

    public bool Cancel(string id)
    {
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var job))
                return false;
            if (job.State == JobState.Queued)
            {
                queue.Remove(job);
                job.State = JobState.Cancelled;
                job.EndedAt = DateTimeOffset.Now;
                job.ExitCode = null;
                NotifyDone(job);
                Persist();
                return true;
            }
            if (job.State != JobState.Running)
                return false;
            job.CancelRequested = true;
            try
            {
                job.Process?.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            return true;
        }
    }

    public (int Running, int Queued) Counts()
    {
        lock (gate)
            return (order.Count(j => j.State == JobState.Running), order.Count(j => j.State == JobState.Queued));
    }

    public IReadOnlyList<SeedDto> RecentSeeds()
    {
        lock (gate)
        {
            return order
                .Where(j => j.Kind == "fuzz"
                    && j.Parameters.TryGetValue("seed", out var seed) && int.TryParse(seed, out _))
                .OrderByDescending(j => j.CreatedAt)
                .Select(j => new SeedDto(
                    int.Parse(j.Parameters["seed"], System.Globalization.CultureInfo.InvariantCulture),
                    int.TryParse(j.Parameters.GetValueOrDefault("tier"), out var tier) ? tier : 1,
                    j.CreatedAt,
                    j.Metrics.Findings ?? 0,
                    j.State.ToString().ToLowerInvariant(),
                    j.Id,
                    j.Kind))
                .DistinctBy(s => (s.Seed, s.Tier))
                .Take(10)
                .ToArray();
        }
    }

    public void Shutdown()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            var now = DateTimeOffset.Now;
            foreach (var job in order.Where(j => j.State is JobState.Running or JobState.Queued))
            {
                job.CancelRequested = true;
                try
                {
                    job.Process?.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
                if (job.State == JobState.Running)
                {
                    job.State = JobState.Interrupted;
                    job.EndedAt = now;
                }
                else
                {
                    job.State = JobState.Cancelled;
                    job.EndedAt = now;
                }
            }
            queue.Clear();
            Persist();
        }
    }

    public void Dispose() => Shutdown();

    string NewId()
    {
        while (true)
        {
            var id = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)
                + "-" + Random.Shared.Next(0x1000, 0x10000).ToString("x4", System.Globalization.CultureInfo.InvariantCulture);
            if (!jobs.ContainsKey(id))
                return id;
        }
    }

    JobDto ToDto(Job job) => new(
        job.Id,
        job.Kind,
        job.Title,
        job.State,
        job.Parameters,
        CommandsOf(job),
        job.CreatedAt,
        job.StartedAt,
        job.EndedAt,
        job.ExitCode,
        job.Metrics,
        job.State is JobState.Queued or JobState.Running,
        job.Lines.Count,
        job.Error);

    static IReadOnlyList<string> CommandsOf(Job job)
    {
        if (job.Steps.Count > 0)
            return job.Steps.Select(JobSpecs.FormatCommand).ToArray();
        if (job.Spec is null)
            return [];
        try
        {
            var values = JobSpecs.Validate(job.Spec, job.Parameters);
            return job.Spec.Build(new JobValues(values)).Select(JobSpecs.FormatCommand).ToArray();
        }
        catch (JobInputException)
        {
            return [];
        }
    }

    void Load()
    {
        try
        {
            if (!File.Exists(jobsFile))
                return;
            var records = JsonSerializer.Deserialize<List<JobRecord>>(File.ReadAllText(jobsFile), JsonDefaults.Options) ?? [];
            foreach (var record in records.OrderBy(r => r.CreatedAt))
            {
                if (string.IsNullOrEmpty(record.Id) || jobs.ContainsKey(record.Id))
                    continue;
                var job = new Job
                {
                    Id = record.Id,
                    Kind = record.Kind,
                    Title = record.Title,
                    Parameters = record.Parameters ?? new Dictionary<string, string>(),
                    Steps = [],
                    RequiresIdle = false,
                    LogPath = Path.Combine(logDirectory, record.Id + ".log"),
                    CreatedAt = record.CreatedAt,
                    Spec = specs.GetValueOrDefault(record.Kind),
                    State = record.State,
                    StartedAt = record.StartedAt,
                    EndedAt = record.EndedAt,
                    ExitCode = record.ExitCode,
                    Error = record.Error,
                    Metrics = record.Metrics ?? new JobMetrics(),
                };
                if (job.State is JobState.Queued or JobState.Running)
                {
                    job.State = JobState.Interrupted;
                    job.EndedAt ??= DateTimeOffset.Now;
                }
                else if (job.State == JobState.Failed && ReportedFindings(job))
                {
                    job.State = JobState.Findings;
                }
                jobs[job.Id] = job;
                order.Add(job);
            }
            Trim();
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    void Trim()
    {
        while (order.Count > MaxHistory)
        {
            var victim = order.FirstOrDefault(j => j.State is not (JobState.Running or JobState.Queued));
            if (victim is null)
                return;
            order.Remove(victim);
            jobs.Remove(victim.Id);
        }
    }

    void Persist()
    {
        try
        {
            var records = order.Select(j => new JobRecord
            {
                Id = j.Id,
                Kind = j.Kind,
                Title = j.Title,
                Parameters = j.Parameters.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
                State = j.State,
                CreatedAt = j.CreatedAt,
                StartedAt = j.StartedAt,
                EndedAt = j.EndedAt,
                ExitCode = j.ExitCode,
                Error = j.Error,
                Metrics = j.Metrics,
            }).ToList();
            var temporary = jobsFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(records, JsonDefaults.Options));
            File.Move(temporary, jobsFile, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    void Pump()
    {
        while (true)
        {
            var active = order.Where(j => j.State == JobState.Running).ToList();
            if (active.Count >= MaxConcurrent || active.Any(j => j.RequiresIdle))
                return;

            Job? next = null;
            foreach (var candidate in queue)
            {
                if (candidate.RequiresIdle && active.Count > 0)
                    continue;
                if (active.Any(running => Conflicts(running, candidate)))
                    continue;
                next = candidate;
                break;
            }
            if (next is null)
                return;

            queue.Remove(next);
            next.State = JobState.Running;
            next.StartedAt = DateTimeOffset.Now;
            next.Error = null;
            try
            {
                next.Log = new StreamWriter(next.LogPath, append: false, Encoding.UTF8) { AutoFlush = true };
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            Persist();
            _ = Task.Run(() => ExecuteAsync(next));
        }
    }

    static bool Conflicts(Job running, Job candidate) =>
        running.Spec?.Lock is { } left && candidate.Spec?.Lock is { } right && left == right;

    async Task ExecuteAsync(Job job)
    {
        int? exit = null;
        string? error = null;
        try
        {
            foreach (var step in job.Steps)
            {
                lock (gate)
                {
                    if (job.CancelRequested)
                        break;
                    Append(job, "$ " + JobSpecs.FormatCommand(step), observe: false);
                }

                var result = await RunStepAsync(job, step);
                exit = result.ExitCode;
                error = result.Error;
                if (error is not null || job.CancelRequested || exit != 0)
                    break;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            lock (gate)
                Finish(job, exit, error);
        }
    }

    async Task<StepResult> RunStepAsync(Job job, StepSpec step)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = step.FileName,
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in step.Arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                lock (gate) Append(job, e.Data, observe: true);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                lock (gate) Append(job, e.Data, observe: true);
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new(null, $"could not start '{step.FileName}': {ex.Message}");
        }

        lock (gate)
            job.Process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        // The parameterless overload waits for the redirected editors to drain.
        process.WaitForExit();
        lock (gate)
            job.Process = null;
        return new(process.ExitCode, null);
    }

    void Append(Job job, string text, bool observe)
    {
        var line = Ansi.Replace(text, "").Replace("\r", "");
        var sequence = job.Lines.Count;
        job.Lines.Add(line);
        if (observe)
            job.Spec?.Observe?.Invoke(line, job.Metrics);
        var lineEvent = new LineEvent(sequence, line, job.Metrics);
        foreach (var subscriber in job.Subscribers)
            subscriber.TryWrite(lineEvent);
        try
        {
            job.Log?.WriteLine(line);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    void Finish(Job job, int? exit, string? error)
    {
        job.ExitCode = exit;
        job.EndedAt = DateTimeOffset.Now;
        job.State = job.CancelRequested ? JobState.Cancelled
            : error is not null ? JobState.Failed
            : exit == 0 ? JobState.Succeeded
            : ReportedFindings(job) ? JobState.Findings
            : JobState.Failed;
        job.Error = error;
        var elapsed = job.StartedAt is { } started ? (job.EndedAt.Value - started).TotalSeconds : 0;
        Append(job, $"# {job.State.ToString().ToLowerInvariant()}{(exit is { } code ? $" (exit {code})" : "")} in {elapsed:F1}s", observe: false);
        try
        {
            job.Log?.Flush();
            job.Log?.Dispose();
        }
        catch (IOException)
        {
        }
        job.Log = null;
        NotifyDone(job);
        Trim();
        Persist();
        Pump();
    }

    /// <summary>
    /// Whether the job exited nonzero only to report what it was asked to look for, such as a
    /// fuzzer's findings: the run completed, and nothing went wrong with the tool itself.
    /// </summary>
    static bool ReportedFindings(Job job) =>
        job.Error is null && (job.Metrics.Findings is > 0 || job.Metrics.FindingKeys.Count > 0 || job.Metrics.Survived is > 0);

    void NotifyDone(Job job)
    {
        var done = new DoneEvent(ToDto(job));
        foreach (var subscriber in job.Subscribers)
        {
            subscriber.TryWrite(done);
            subscriber.TryComplete();
        }
        job.Subscribers.Clear();
    }

    static IEnumerable<string> ReadLog(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadLines(path) : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    sealed record StepResult(int? ExitCode, string? Error);

    sealed class JobRecord
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Title { get; set; } = "";
        public Dictionary<string, string> Parameters { get; set; } = [];
        public JobState State { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        public int? ExitCode { get; set; }
        public string? Error { get; set; }
        public JobMetrics Metrics { get; set; } = new();
    }
}
