using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;
using Vpl.Dashboard;

ServerOptions options;
try
{
    options = ServerOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine("vpl-dashboard: " + ex.Message);
    return 2;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
builder.WebHost.UseUrls($"http://localhost:{options.Port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<FindingsService>();
builder.Services.AddSingleton<JobManager>();
builder.Services.AddSingleton<StatusService>();
builder.Services.AddSingleton<LinesService>();
builder.Services.AddSingleton<FileService>();

var app = builder.Build();

// A page on another origin must not be able to start jobs on the loopback server. The custom
// header makes a cross-origin request require a CORS preflight, which this app never grants.
app.Use(async (context, next) =>
{
    if (context.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE")
    {
        if (!string.Equals(context.Request.Headers["X-Vpl-Dashboard"].ToString(), "1", StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "missing X-Vpl-Dashboard header" });
            return;
        }
        var origin = context.Request.Headers["Origin"].ToString();
        if (origin.Length > 0)
        {
            var host = context.Request.Host.Value;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Authority, host, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "cross-origin request refused" });
                return;
            }
        }
    }
    await next();
});

var webRoot = FindWebRoot(options.Root);
var files = new PhysicalFileProvider(webRoot);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = files,
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
});

Api.Map(app);

var status = app.Services.GetRequiredService<StatusService>();
var jobs = app.Services.GetRequiredService<JobManager>();
app.Lifetime.ApplicationStopping.Register(jobs.Shutdown);

Console.WriteLine($"VPL dashboard  http://localhost:{options.Port}");
Console.WriteLine($"repository     {options.Root}");
status.Warm();

if (options.Open)
    app.Lifetime.ApplicationStarted.Register(() => OpenBrowser(options.Port));

app.Run();
return 0;

static string FindWebRoot(string root)
{
    var inRepository = Path.Combine(root, "tools", "Dashboard", "wwwroot");
    if (Directory.Exists(inRepository))
        return inRepository;
    var besideBinary = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    if (Directory.Exists(besideBinary))
        return besideBinary;
    throw new DirectoryNotFoundException("could not find tools/Dashboard/wwwroot");
}

static void OpenBrowser(int port)
{
    try
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = $"http://localhost:{port}",
            UseShellExecute = true,
        });
    }
    catch (Exception)
    {
    }
}
