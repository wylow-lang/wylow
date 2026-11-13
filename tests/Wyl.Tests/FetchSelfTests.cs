using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Wyl.Compiler;
using Wyl.Compiler.Build;
using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

/// <summary>
/// The fetch cache (decision 0105): a download is checked and kept, a later load uses the cache
/// without the network, a missing extracted file comes back from the kept archive, and each failure
/// reports its code. The limits (decision 0106) stop a small archive from filling the disk or the
/// memory, and a symbolic link is never followed. <see cref="FakeDownloads"/> supplies the bytes.
/// </summary>
static class FetchSelfTests
{
    public static IEnumerable<TestResult> Run()
    {
        yield return Cache();
        yield return SingleFile();
        yield return Tar();
        yield return Symlink();
        yield return DownloadLimit();
        yield return ExtractLimit();
        yield return ScanLimit();
        yield return Failure("fetch/hash", "https://files.test/x.zip", new string('0', 64), "\"sample/value.h\"", "WYL0550");
        yield return Failure("fetch/missing", "https://files.test/x.zip", FakeDownloads.SampleHash, "\"sample/absent.h\"", "WYL0551");
        yield return Failure("fetch/download", "https://unreachable.test/x.zip", new string('0', 64), "\"sample/value.h\"", "WYL0552");
        yield return Failure("fetch/archive", "https://badzip.test/x.zip", FakeDownloads.BadHash, "\"sample/value.h\"", "WYL0556");
        yield return Failure("fetch/hash-form", "https://files.test/x.zip", "abc", "\"sample/value.h\"", "WYL0553");
        yield return Failure("fetch/files-form", "https://files.test/x.zip", FakeDownloads.SampleHash, "", "WYL0554");
        yield return Failure("fetch/path-form", "https://files.test/x.zip", FakeDownloads.SampleHash, "\"../sample/value.h\"", "WYL0554");
        yield return Failure("fetch/device-form", "https://files.test/x.zip", FakeDownloads.SampleHash, "\"sample/con.h\"", "WYL0554");
        yield return Failure("fetch/url-form", "http://files.test/x.zip", FakeDownloads.SampleHash, "\"sample/value.h\"", "WYL0555");
    }

    /// <summary>A load uses the cache, and a missing extracted file is extracted again from the kept archive.</summary>
    static TestResult Cache()
    {
        var sources = Load("cache", "https://selftest.test/self.zip", FakeDownloads.SelfHash, "\"self/data.c\", \"self/data.h\"", out var directory);
        if (sources.Diagnostics.Count > 0)
            return new("fetch/cache", string.Join('\n', sources.Diagnostics));
        var package = sources.Native.Packages.Single();
        var header = package.FetchedFiles.SingleOrDefault(f => f.EndsWith("data.h", StringComparison.Ordinal));
        var source = package.FetchedFiles.SingleOrDefault(f => f.EndsWith("data.c", StringComparison.Ordinal));
        if (header is null || source is null || !File.Exists(header) || !File.Exists(source))
            return new("fetch/cache", "the files of the download are not in the cache");
        if (package.FetchedSources is not [var only] || !only.EndsWith("data.c", StringComparison.Ordinal))
            return new("fetch/cache", "the fetched C source is not a source of the package");
        if (package.FetchedIncludeDirectories is not [var include] || Path.GetFileName(include) != "self")
            return new("fetch/cache", "the directory of the fetched header is not on the include path");
        var archive = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(header)!, "..", "..", "archive"));
        if (!File.Exists(archive))
            return new("fetch/cache", "the download is not in the cache");
        var stamp = File.GetLastWriteTimeUtc(archive);
        if (PackageSources.Load(directory).Diagnostics.Count > 0)
            return new("fetch/cache", "the second load reports an error");
        if (File.GetLastWriteTimeUtc(archive) != stamp)
            return new("fetch/cache", "the second load downloaded the file again");
        File.Delete(header);
        if (PackageSources.Load(directory).Diagnostics.Count > 0)
            return new("fetch/cache", "the load after deleting a file reports an error");
        if (!File.Exists(header))
            return new("fetch/cache", "a missing extracted file was not extracted again");
        if (File.GetLastWriteTimeUtc(archive) != stamp)
            return new("fetch/cache", "the compiler downloaded the file again to extract it");
        return new("fetch/cache", null);
    }

    /// <summary>A URL that is not an archive names one file, whose name 'files' gives.</summary>
    static TestResult SingleFile()
    {
        var bytes = "#pragma once\nint single_value(void);\n"u8.ToArray();
        FakeDownloads.Serve("single.test", bytes);
        var sources = Load("single", "https://single.test/value.h", Convert.ToHexStringLower(SHA256.HashData(bytes)), "\"single.h\"", out _);
        if (sources.Diagnostics.Count > 0)
            return new("fetch/single", string.Join('\n', sources.Diagnostics));
        var file = sources.Native.Packages.Single().FetchedFiles.Single();
        return new("fetch/single", File.Exists(file) && File.ReadAllText(file) == "#pragma once\nint single_value(void);\n"
            ? null
            : "the downloaded file is not in the cache");
    }

    /// <summary>A tar.gz archive extracts the file that 'files' names.</summary>
    static TestResult Tar()
    {
        var bytes = TarArchive(("tar/data.h", "#pragma once\nint tar_value(void);\n"u8.ToArray()));
        FakeDownloads.Serve("tar.test", bytes);
        var sources = Load("tar", "https://tar.test/package.tar.gz", Convert.ToHexStringLower(SHA256.HashData(bytes)), "\"tar/data.h\"", out _);
        if (sources.Diagnostics.Count > 0)
            return new("fetch/tar", string.Join('\n', sources.Diagnostics));
        var file = sources.Native.Packages.Single().FetchedFiles.Single();
        return new("fetch/tar", File.Exists(file) && file.EndsWith(Path.Combine("tar", "data.h"), StringComparison.Ordinal)
            ? null
            : "the member of the tar archive is not in the cache");
    }

    /// <summary>A symbolic link in an archive is not extracted; the file that names it is missing.</summary>
    static TestResult Symlink()
    {
        var bytes = SymlinkArchive();
        FakeDownloads.Serve("link.test", bytes);
        var (fetch, bag) = Entry("https://link.test/package.tar.gz", Convert.ToHexStringLower(SHA256.HashData(bytes)), "link/data.h");
        var fetched = Downloads.Ensure(fetch, bag);
        return new("fetch/symlink", fetched is null && Codes(bag) == "WYL0551"
            ? null
            : "a symbolic link in an archive was not reported as a missing file");
    }

    /// <summary>A download larger than its limit is refused before it is kept.</summary>
    static TestResult DownloadLimit()
    {
        var (fetch, bag) = Entry("https://files.test/large.zip", new string('a', 64), "sample/value.h");
        Downloads.Ensure(fetch, bag, maxDownload: 10);
        return Code("fetch/download-limit", bag, "WYL0552");
    }

    /// <summary>A zip entry that is larger than the extraction limit is refused before it expands.</summary>
    static TestResult ExtractLimit()
    {
        var bytes = Zip(("cap/value.h", "#pragma once\nint cap_value(void);\n"u8.ToArray()));
        FakeDownloads.Serve("cap.test", bytes);
        var (fetch, bag) = Entry("https://cap.test/package.zip", Convert.ToHexStringLower(SHA256.HashData(bytes)), "cap/value.h");
        Downloads.Ensure(fetch, bag, maxExtracted: 10);
        return Code("fetch/extract-limit", bag, "WYL0556");
    }

    /// <summary>A compressed archive that expands past the scan limit is refused while it is read.</summary>
    static TestResult ScanLimit()
    {
        var bytes = TarArchive(("big/first.bin", new byte[4096]), ("ok/data.h", "#pragma once\n"u8.ToArray()));
        FakeDownloads.Serve("scan.test", bytes);
        var (fetch, bag) = Entry("https://scan.test/package.tar.gz", Convert.ToHexStringLower(SHA256.HashData(bytes)), "ok/data.h");
        Downloads.Ensure(fetch, bag, maxScan: 100);
        return Code("fetch/scan-limit", bag, "WYL0556");
    }

    static TestResult Failure(string name, string url, string hash, string files, string code)
    {
        var sources = Load(name.Replace('/', '-'), url, hash, files, out _);
        return Code(name, string.Join(", ", sources.Diagnostics.Select(d => d.Code).Distinct().Order(StringComparer.Ordinal)), code);
    }

    static TestResult Code(string name, DiagnosticBag bag, string expected) =>
        Code(name, string.Join(", ", bag.Sorted().Select(d => d.Code).Distinct().Order(StringComparer.Ordinal)), expected);

    static TestResult Code(string name, string codes, string expected) =>
        new(name, codes == expected ? null : $"expected {expected}, found {(codes.Length == 0 ? "no diagnostic" : codes)}");

    static string Codes(DiagnosticBag bag) =>
        string.Join(", ", bag.Sorted().Select(d => d.Code).Distinct().Order(StringComparer.Ordinal));

    static (Manifest.FetchEntry Fetch, DiagnosticBag Bag) Entry(string url, string hash, params string[] files)
    {
        var source = new SourceText("package.wylcfg", "name = \"fetch\";");
        var at = new Location(source, new TextSpan(0, 0));
        return (new Manifest.FetchEntry(url, at, hash, at, files, at, at), new DiagnosticBag());
    }

    static PackageSources Load(string name, string url, string hash, string files, out string directory)
    {
        var package = new string([.. name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')]);
        directory = Path.Combine(Path.GetTempPath(), "wyl-fetch-selftest", name);
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        File.WriteAllText(Path.Combine(directory, "package.wylcfg"), $$"""
            name = "{{package}}";
            native {
                fetch {
                    url = "{{url}}";
                    sha256 = "{{hash}}";
                    files = [{{files}}];
                }
            }
            """);
        File.WriteAllText(Path.Combine(directory, "src", "main.wyl"), "void main() { }\n");
        return PackageSources.Load(directory);
    }

    static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(data);
            }
        }
        return memory.ToArray();
    }

    static byte[] TarArchive(params (string Name, byte[] Data)[] entries)
    {
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.NoCompression, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var stream = new MemoryStream(data);
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = stream });
            }
        }
        return memory.ToArray();
    }

    static byte[] SymlinkArchive()
    {
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.NoCompression, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link/data.h") { LinkName = "/etc/passwd" });
        return memory.ToArray();
    }
}
