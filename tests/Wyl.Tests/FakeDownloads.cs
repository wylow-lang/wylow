using System.Collections.Concurrent;
using Wyl.Compiler.Build;

/// <summary>
/// Replaces the HTTP download while the suite runs, so a test case that fetches a native file does
/// not use the network. The cache goes in a directory of this process, and the suite empties it
/// before it runs.
/// </summary>
static class FakeDownloads
{
    /// <summary>The SHA-256 of <see cref="Sample"/>, which the test cases and their pages repeat.</summary>
    public const string SampleHash = "3806927d8ed43dea85666325770b2a572b1dcc6e355127eaa87938ec1d85ab11";

    /// <summary>The SHA-256 of <see cref="Bad"/>, which the test cases and their pages repeat.</summary>
    public const string BadHash = "306dd75289584f36ed2491b76f151702d5535c722337074d638f62a0a63b572e";

    /// <summary>The SHA-256 of the self-tests' own archive, which no test case and no page names.</summary>
    public const string SelfHash = "2a031fedda7e6ce843fc6ad964bacff250ebf835c25df86e2e3ac4a754f96278";

    // sample/value.h and sample/value.c, with fixed timestamps and no compression.
    static readonly byte[] Sample = Convert.FromBase64String(
        "UEsDBBQAAAAAAAAAIQDZRbwjJQAAACUAAAAOAAAAc2FtcGxlL3ZhbHVlLmgjcHJhZ21hIG9uY2UKaW50IHNhbXBsZV92YWx1ZSh2b2lkKTsKUEsDBBQAAAAAAAAAIQCCqClwOAAAADgAAAAOAAAAc2FtcGxlL3ZhbHVlLmMjaW5jbHVkZSAidmFsdWUuaCIKaW50IHNhbXBsZV92YWx1ZSh2b2lkKSB7IHJldHVybiA3OyB9ClBLAQIUABQAAAAAAAAAIQDZRbwjJQAAACUAAAAOAAAAAAAAAAAAAACkAQAAAABzYW1wbGUvdmFsdWUuaFBLAQIUABQAAAAAAAAAIQCCqClwOAAAADgAAAAOAAAAAAAAAAAAAACkAVEAAABzYW1wbGUvdmFsdWUuY1BLBQYAAAAAAgACAHgAAAC1AAAAAAA=");

    static readonly byte[] Bad = "this is not a zip archive\n"u8.ToArray();

    // self/data.h and self/data.c, which the self-tests delete and extract again.
    static readonly byte[] Self = Convert.FromBase64String(
        "UEsDBBQAAAAAAAAAIQDjgV7xIwAAACMAAAALAAAAc2VsZi9kYXRhLmgjcHJhZ21hIG9uY2UKaW50IHNlbGZfdmFsdWUodm9pZCk7ClBLAwQUAAAAAAAAACEAbDSxGzYAAAA2AAAACwAAAHNlbGYvZGF0YS5jI2luY2x1ZGUgImRhdGEuaCIKaW50IHNlbGZfdmFsdWUodm9pZCkgeyByZXR1cm4gMTE7IH0KUEsBAhQAFAAAAAAAAAAhAOOBXvEjAAAAIwAAAAsAAAAAAAAAAAAAAKQBAAAAAHNlbGYvZGF0YS5oUEsBAhQAFAAAAAAAAAAhAGw0sRs2AAAANgAAAAsAAAAAAAAAAAAAAKQBTAAAAHNlbGYvZGF0YS5jUEsFBgAAAAACAAIAcgAAAKsAAAAAAA==");

    static readonly ConcurrentDictionary<string, byte[]> Served = new();

    /// <summary>The bytes that a host other than the fixed ones serves, such as a tar archive that a self-test builds.</summary>
    public static void Serve(string host, byte[] bytes) => Served[host] = bytes;

    public static void Install()
    {
        var cache = Path.Combine(Path.GetTempPath(), "wyl-fetch-tests-" + Environment.ProcessId);
        if (Directory.Exists(cache))
            Directory.Delete(cache, recursive: true);
        Directory.CreateDirectory(cache);
        Environment.SetEnvironmentVariable("WYL_CACHE_DIR", cache);
        Downloads.Override = uri => uri.Host switch
        {
            "unreachable.test" => new Downloads.Response(null, "no such host"),
            "badzip.test" => Bytes(Bad),
            "selftest.test" => Bytes(Self),
            _ => Bytes(Served.TryGetValue(uri.Host, out var bytes) ? bytes : Sample),
        };
    }

    static Downloads.Response Bytes(byte[] bytes) => new(new MemoryStream(bytes), null);
}
