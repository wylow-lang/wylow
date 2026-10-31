namespace Wyl.Harness;

/// <summary>Finds the repository root from any build output beneath it.</summary>
public static class Repository
{
    public static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "cases")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("could not find tests/cases above " + AppContext.BaseDirectory);
    }
}
