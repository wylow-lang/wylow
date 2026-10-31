using System.Text.Json;
using System.Threading.Channels;

namespace Wyl.Dashboard;

public sealed record StartJobRequest(string Kind, Dictionary<string, string>? Params);

public static class Api
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { ok = true }));

        app.MapGet("/api/status", (StatusService status) => Results.Ok(status.Get()));
        app.MapGet("/api/lines", (LinesService lines) => Results.Ok(lines.Get()));
        app.MapGet("/api/kinds", (JobManager jobs) => Results.Ok(jobs.Kinds()));
        app.MapGet("/api/reference", () => Results.Ok(Reference.All));

        app.MapGet("/api/jobs", (JobManager jobs) => Results.Ok(jobs.List()));
        app.MapGet("/api/jobs/{id}", (string id, JobManager jobs) =>
            jobs.Get(id) is { } job ? Results.Ok(job) : Results.NotFound(new { error = "job not found" }));
        app.MapGet("/api/jobs/{id}/log", (string id, int? tail, JobManager jobs) =>
            jobs.GetLog(id, tail ?? 5000) is { } log
                ? Results.Text(log, "text/plain; charset=utf-8")
                : Results.NotFound(new { error = "job not found" }));

        app.MapPost("/api/jobs", (StartJobRequest request, JobManager jobs) =>
        {
            var result = jobs.Start(request.Kind, request.Params ?? new Dictionary<string, string>());
            return result.Job is { } job
                ? Results.Ok(job)
                : Results.BadRequest(new { error = result.Error });
        });
        app.MapPost("/api/jobs/preview", (StartJobRequest request, JobManager jobs) =>
        {
            var (commands, error) = jobs.Preview(request.Kind, request.Params ?? new Dictionary<string, string>());
            return error is null ? Results.Ok(new { commands }) : Results.BadRequest(new { error });
        });
        app.MapPost("/api/jobs/{id}/cancel", (string id, JobManager jobs) =>
            jobs.Cancel(id) ? Results.Ok(new { ok = true }) : Results.NotFound(new { error = "job is not running" }));
        app.MapGet("/api/jobs/{id}/stream", (string id, HttpContext context, JobManager jobs) =>
            StreamAsync(context, jobs, id));

        app.MapGet("/api/mutation", (MutationService mutation) => Results.Ok(mutation.Get()));

        app.MapGet("/api/findings", (FindingsService findings) => Results.Ok(findings.List()));
        app.MapGet("/api/findings/content", (string path, FindingsService findings) =>
        {
            var (text, error) = findings.Read(path);
            return error is null
                ? Results.Text(text ?? "", "text/plain; charset=utf-8")
                : Results.BadRequest(new { error });
        });

        app.MapGet("/api/file", (string path, FileService files) =>
        {
            var (text, error) = files.Read(path);
            return error is null
                ? Results.Text(text ?? "", "text/plain; charset=utf-8")
                : Results.BadRequest(new { error });
        });
    }

    static async Task StreamAsync(HttpContext context, JobManager manager, string id)
    {
        var handle = manager.OpenStream(id);
        if (handle is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var response = context.Response;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache, no-transform";
        response.Headers["X-Accel-Buffering"] = "no";
        var cancellation = context.RequestAborted;

        try
        {
            var snapshot = new { job = handle.Job, lines = handle.Lines, commands = handle.Commands };
            await WriteEventAsync(response, "snapshot", JsonSerializer.Serialize(snapshot, JsonDefaults.Options), cancellation);
            if (handle.Finished)
            {
                await WriteEventAsync(response, "done", JsonSerializer.Serialize(new DoneEvent(handle.Job), JsonDefaults.Options), cancellation);
                return;
            }

            while (!cancellation.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    if (!await handle.Reader.WaitToReadAsync(timeout.Token))
                        break;
                    while (handle.Reader.TryRead(out var jobEvent))
                        await WriteEventAsync(
                            response,
                            EventName(jobEvent),
                            JsonSerializer.Serialize(jobEvent, jobEvent.GetType(), JsonDefaults.Options),
                            cancellation);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    await response.WriteAsync(": ping\n\n", cancellation);
                    await response.Body.FlushAsync(cancellation);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            manager.CloseStream(id, handle.Writer);
        }
    }

    static string EventName(JobEvent jobEvent) => jobEvent switch
    {
        LineEvent => "line",
        DoneEvent => "done",
        _ => "message",
    };

    static async Task WriteEventAsync(HttpResponse response, string name, string data, CancellationToken cancellationToken)
    {
        await response.WriteAsync($"event: {name}\ndata: {data}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
