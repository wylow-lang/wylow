using System.Globalization;

namespace Vpl.Dashboard;

public sealed record ServerOptions(int Port, bool Open, string Root)
{
    public const int DefaultPort = 5178;

    public static ServerOptions Parse(string[] args)
    {
        int port = DefaultPort;
        bool open = false;
        string? root = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port":
                    if (i + 1 >= args.Length
                        || !int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                        || number is < 1 or > 65535)
                        throw new ArgumentException("--port needs a number from 1 to 65535");
                    port = number;
                    i++;
                    break;
                case "--root":
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--root needs a path");
                    root = args[++i];
                    break;
                case "--open":
                    open = true;
                    break;
                case "--help" or "-h":
                    Console.WriteLine("usage: dotnet run --project tools/Dashboard -- [--port 5178] [--open] [--root <repo>]");
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"unknown option '{args[i]}' (try --help)");
            }
        }

        root = Path.GetFullPath(root ?? RepoRoot.Find());
        if (!Directory.Exists(Path.Combine(root, "tests", "cases")))
            throw new ArgumentException($"not a VPL repository (no tests/cases): {root}");
        return new(port, open, root);
    }
}

static class RepoRoot
{
    public static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "tests", "cases")))
                    return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("could not find tests/cases above the dashboard; pass --root");
    }
}
