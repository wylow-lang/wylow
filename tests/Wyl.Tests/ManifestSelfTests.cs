using Wyl.Compiler;
using Wyl.Compiler.Build;
using Wyl.Compiler.Syntax;

static class ManifestSelfTests
{
    public static List<TestResult> Run(string outputDirectory) => [PlatformPaths(outputDirectory), PlatformLists(outputDirectory)];

    static TestResult PlatformPaths(string outputDirectory)
    {
        var directory = Path.Combine(outputDirectory, "manifest", "platform-paths");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Write(directory, "src/main.wyl", "void main() { }\n");
        Directory.CreateDirectory(Path.Combine(directory, "include"));
        File.WriteAllText(Path.Combine(directory, "include", "file.txt"), "");
        File.WriteAllText(Path.Combine(directory, "file.txt"), "");
        var current = NativeInputs.Platform;
        var others = Manifest.Platforms.Where(p => p != current).ToList();
        string ManifestText(string currentInclude) => $$"""
            name = "platforms";
            native {
                {{current}} {
                    include = ["{{currentInclude}}"];
                    copy = ["{{(currentInclude == "include" ? "file.txt" : "absent.txt")}}"];
                }
                {{others[0]}} {
                    include = ["absent"];
                    copy = ["absent.txt"];
                }
                {{others[1]}} {
                    include = ["absent"];
                    copy = ["absent.txt"];
                }
            }
            """;
        Write(directory, "package.wylcfg", ManifestText("include"));

        List<string> problems = [];
        var accepted = PackageSources.Load(directory);
        if (accepted.Diagnostics.Count > 0)
            problems.Add($"the current platform's paths are valid, and the others' are not, but the load reported {string.Join("; ", accepted.Diagnostics)}");

        Write(directory, "package.wylcfg", ManifestText("absent"));
        var refused = PackageSources.Load(directory);
        var codes = refused.Diagnostics.Select(d => d.Code).Distinct().ToList();
        if (!codes.Contains("WYL0518"))
            problems.Add("a missing include directory of the current platform is not reported");
        if (codes.Any(code => code != "WYL0518"))
            problems.Add($"a missing path reported {string.Join(", ", codes)}");
        return new("manifest/platform-paths", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    static TestResult PlatformLists(string outputDirectory)
    {
        var directory = Path.Combine(outputDirectory, "manifest", "platform-lists");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Write(directory, "src/main.wyl", "void main() { }\n");
        foreach (var name in new[] { "first", "second", "third", "other" })
            Directory.CreateDirectory(Path.Combine(directory, name));
        var current = NativeInputs.Platform;
        var other = Manifest.Platforms.First(p => p != current);
        Write(directory, "package.wylcfg", $$"""
            name = "lists";
            native {
                include = ["first", "second"];
                defines = ["BASE=1"];
                {{current}} {
                    include = ["third"];
                    defines = ["PLATFORM=2"];
                }
                {{other}} {
                    include = ["other"];
                    defines = ["ELSEWHERE=3"];
                }
            }
            """);

        List<string> problems = [];
        var loaded = PackageSources.Load(directory);
        if (loaded.Diagnostics.Count > 0)
            return new("manifest/platform-lists", $"the package does not load: {string.Join("; ", loaded.Diagnostics)}");
        var native = loaded.Native.Packages.Single(p => p.Package == loaded.Package);
        var include = native.Include.Select(Path.GetFileName).ToList();
        if (!include.SequenceEqual(["first", "second", "third"]))
            problems.Add($"the include directories are {string.Join(", ", include)} instead of first, second, third");
        if (!native.Defines.SequenceEqual(["BASE=1", "PLATFORM=2"]))
            problems.Add($"the definitions are {string.Join(", ", native.Defines)} instead of BASE=1, PLATFORM=2");
        return new("manifest/platform-lists", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    static void Write(string directory, string path, string text)
    {
        var file = Path.Combine(directory, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }
}
