namespace Wyl.Compiler.Build;

/// <summary>
/// The per-user directory that a build caches its outputs in, or the directory that
/// <c>WYL_CACHE_DIR</c> names. A download is under <c>sha256/</c> and a native object under
/// <c>native-objects/</c> (decisions 0105 and 0108).
/// </summary>
static class BuildCache
{
    internal static string Root =>
        Environment.GetEnvironmentVariable("WYL_CACHE_DIR") is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wyl", "cache")
                : Path.Combine(
                    Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
                        ? xdg
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
                    "wyl", "cache");
}
