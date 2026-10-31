namespace Wyl.Dashboard;

/// <summary>Reads text files from the directories the dashboard is allowed to expose.</summary>
public sealed class FileService(ServerOptions options)
{
    const long MaxRead = 2_000_000;
    static readonly string[] AllowedDirectories = ["tests/cases", "examples", "tests/.out", "tests/fuzz/findings", "src"];
    static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wyl", ".cpp", ".h", ".cs", ".ir", ".txt", ".md", ".snapshot", ".json", ".log",
    };

    readonly string root = options.Root;

    public (string? Text, string? Error) Read(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            return (null, "invalid path");

        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!AllowedDirectories.Any(directory =>
                full.StartsWith(Path.GetFullPath(Path.Combine(root, directory)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            return (null, "path is outside the readable directories");
        if (!AllowedExtensions.Contains(Path.GetExtension(full)))
            return (null, "only source and text files can be shown");
        if (!File.Exists(full))
            return (null, "file not found");
        if (new FileInfo(full).Length > MaxRead)
            return (null, "file is too large to show");

        try
        {
            return (File.ReadAllText(full), null);
        }
        catch (IOException ex)
        {
            return (null, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return (null, ex.Message);
        }
    }
}
