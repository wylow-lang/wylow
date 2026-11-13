using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.Build;

/// <summary>
/// Obtains the file that a package's manifest fetches, from the per-user cache or from its URL, and
/// gives the files of the download that the package uses (decision 0105). The size limits keep a
/// small archive from filling the disk or the memory (decision 0106).
/// </summary>
static class Downloads
{
    internal const long DefaultDownload = 512L * 1024 * 1024;
    internal const long DefaultExtracted = 512L * 1024 * 1024;
    internal const long DefaultScan = 1024L * 1024 * 1024;

    static readonly string[] ArchiveExtensions = [".zip", ".tar", ".tar.gz", ".tgz"];

    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>What a download produced: a stream of its content, or the reason that it failed.</summary>
    internal sealed record Response(Stream? Content, string? Error);

    /// <summary>The files of a fetch that the package uses, each with its full path in the cache.</summary>
    internal sealed record Fetched(IReadOnlyList<string> Files, IReadOnlyList<string> IncludeDirectories, IReadOnlyList<string> Sources);

    /// <summary>Replaces the HTTP download while the tests run.</summary>
    internal static Func<Uri, Response>? Override { get; set; }

    /// <summary>
    /// Fetches the file that <paramref name="fetch"/> names, checks its hash, and returns the files
    /// that the package uses, or null after reporting why the files are not there. A cache entry with
    /// every file is used without a download. The limits bound the download, the extracted files,
    /// and the bytes that reading a tar archive expands to.
    /// </summary>
    internal static Fetched? Ensure(
        Manifest.FetchEntry fetch,
        DiagnosticBag diagnostics,
        long maxDownload = DefaultDownload,
        long maxExtracted = DefaultExtracted,
        long maxScan = DefaultScan)
    {
        var uri = new Uri(fetch.Url);
        var root = Path.Combine(CacheRoot, "sha256", fetch.Sha256);
        var archivePath = Path.Combine(root, "archive");
        var filesRoot = Path.Combine(root, "files");
        var files = fetch.Files.Distinct(StringComparer.Ordinal).ToList();

        // The mutex serializes concurrent builds that fetch the same file.
        using var mutex = new Mutex(false, "Global\\wyl-fetch-" + fetch.Sha256[..16]);
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { }
        try
        {
            var paths = files.Select(f => FullPath(filesRoot, f)).ToList();
            if (paths.All(File.Exists))
                return Result(paths);
            if (!File.Exists(archivePath) && !Download(fetch, uri, archivePath, maxDownload, diagnostics))
                return null;
            return Extract(fetch, uri, archivePath, filesRoot, files, maxExtracted, maxScan, diagnostics);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    static Fetched Result(IReadOnlyList<string> files) =>
        new(files,
            [.. files.Select(f => Path.GetDirectoryName(f)!).Distinct(StringComparer.Ordinal)],
            [.. files.Where(f => Path.GetExtension(f) is ".c" or ".cpp")]);

    /// <summary>Downloads the file into <paramref name="archivePath"/>, or reports why it cannot.</summary>
    static bool Download(Manifest.FetchEntry fetch, Uri uri, string archivePath, long maxDownload, DiagnosticBag diagnostics)
    {
        var download = Override?.Invoke(uri) ?? Get(uri);
        if (download.Content is not { } content)
        {
            diagnostics.Add(Catalog.FetchDownloadFailed(fetch.UrlLocation, fetch.Url, OneLine(download.Error ?? "the download failed")));
            return false;
        }
        using var _ = content;
        var temporary = $"{archivePath}.{Environment.ProcessId}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            // A build that stops during the write leaves a temporary file, never a partial archive.
            using (var output = File.Create(temporary))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var actual = CopyHashed(content, output, hash, maxDownload);
                if (actual != fetch.Sha256)
                {
                    diagnostics.Add(Catalog.FetchHashMismatch(fetch.Sha256Location, fetch.Sha256, actual, fetch.Url));
                    return false;
                }
            }
            File.Move(temporary, archivePath, overwrite: true);
            return true;
        }
        catch (TooLargeException)
        {
            diagnostics.Add(Catalog.FetchDownloadFailed(fetch.UrlLocation, fetch.Url, $"the file is larger than {Size(maxDownload)}"));
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException)
        {
            diagnostics.Add(Catalog.FetchDownloadFailed(fetch.UrlLocation, fetch.Url, OneLine(exception.Message)));
            return false;
        }
        finally
        {
            Delete(temporary);
        }
    }

    static Response Get(Uri uri)
    {
        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("wyl");
            response = Client.Send(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var content = response.Content.ReadAsStream();
            var result = new OwnedStream(content, response);
            response = null;
            return new(result, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            response?.Dispose();
            return new(null, exception.Message);
        }
    }

    /// <summary>Extracts the files that the package uses, unless the cache entry already holds them all.</summary>
    static Fetched? Extract(
        Manifest.FetchEntry fetch,
        Uri uri,
        string archivePath,
        string filesRoot,
        IReadOnlyList<string> files,
        long maxExtracted,
        long maxScan,
        DiagnosticBag diagnostics)
    {
        var temporary = $"{filesRoot}.{Environment.ProcessId}.tmp";
        try
        {
            if (Directory.Exists(temporary))
                Directory.Delete(temporary, recursive: true);
            Directory.CreateDirectory(temporary);
            if (IsArchive(uri))
            {
                if (!ExtractArchive(fetch, uri, archivePath, temporary, files, maxExtracted, maxScan, diagnostics))
                    return null;
            }
            else
            {
                // A URL that names one file: 'files' gives the name that file takes.
                var destination = FullPath(temporary, files[0]);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(archivePath, destination, overwrite: true);
                foreach (var missing in files.Skip(1))
                    diagnostics.Add(Catalog.FetchMissingFile(fetch.FilesLocation, missing, fetch.Url));
                if (files.Count > 1)
                    return null;
            }
            if (Directory.Exists(filesRoot))
                Directory.Delete(filesRoot, recursive: true);
            Directory.Move(temporary, filesRoot);
            return Result([.. files.Select(f => FullPath(filesRoot, f))]);
        }
        catch (TooLargeException exception)
        {
            diagnostics.Add(Catalog.FetchArchive(fetch.UrlLocation, $"the archive is larger than {Size(exception.Limit)} when it is unpacked"));
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Catalog.FetchArchive(fetch.UrlLocation, OneLine(exception.Message)));
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporary))
                    Directory.Delete(temporary, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Copies each file that the package uses from the archive, reporting each one that the archive does not hold.</summary>
    static bool ExtractArchive(
        Manifest.FetchEntry fetch,
        Uri uri,
        string archivePath,
        string directory,
        IReadOnlyList<string> files,
        long maxExtracted,
        long maxScan,
        DiagnosticBag diagnostics)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var wanted = files.ToHashSet(StringComparer.Ordinal);
        var remaining = maxExtracted;
        try
        {
            if (uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var archive = ZipFile.OpenRead(archivePath);
                foreach (var path in files)
                {
                    if (archive.GetEntry(path) is not { } entry)
                        continue;
                    // A zip entry gives its unpacked size, so a bomb is refused before it expands.
                    if (entry.Length > remaining)
                        throw new TooLargeException(maxExtracted);
                    using var source = entry.Open();
                    Write(directory, path, source, ref remaining, maxExtracted);
                    found.Add(path);
                }
            }
            else
            {
                using var file = File.OpenRead(archivePath);
                using Stream decompressed = uri.AbsolutePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                    || uri.AbsolutePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
                        ? new GZipStream(file, CompressionMode.Decompress)
                        : file;
                using var limited = new LimitedStream(decompressed, maxScan);
                using var reader = new TarReader(limited);
                while (reader.GetNextEntry() is { } entry)
                {
                    var name = ArchiveName(entry.Name);
                    if (entry.DataStream is null || !wanted.Contains(name) || !found.Add(name))
                        continue;
                    if (entry.Length > remaining)
                        throw new TooLargeException(maxExtracted);
                    Write(directory, name, entry.DataStream, ref remaining, maxExtracted);
                }
            }
        }
        catch (TooLargeException exception)
        {
            diagnostics.Add(Catalog.FetchArchive(fetch.UrlLocation, $"the archive is larger than {Size(exception.Limit)} when it is unpacked"));
            return false;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Catalog.FetchArchive(fetch.UrlLocation, OneLine(exception.Message)));
            return false;
        }
        foreach (var missing in files.Where(f => !found.Contains(f)))
            diagnostics.Add(Catalog.FetchMissingFile(fetch.FilesLocation, missing, fetch.Url));
        return found.Count == files.Count;
    }

    static void Write(string directory, string path, Stream source, ref long remaining, long maxExtracted)
    {
        var destination = FullPath(directory, path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var output = File.Create(destination);
        remaining -= Copy(source, output, remaining, maxExtracted);
    }

    /// <summary>Copies a stream, and reports the whole download's limit when the copy passes its share.</summary>
    static long Copy(Stream source, Stream destination, long limit, long maxExtracted)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit)
                throw new TooLargeException(maxExtracted);
            destination.Write(buffer, 0, read);
        }
        return total;
    }

    /// <summary>Copies a download while hashing it, and reports the download's limit when the file passes it.</summary>
    static string CopyHashed(Stream source, Stream destination, IncrementalHash hash, long limit)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit)
                throw new TooLargeException(limit);
            hash.AppendData(buffer, 0, read);
            destination.Write(buffer, 0, read);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static bool IsArchive(Uri uri) =>
        ArchiveExtensions.Any(extension => uri.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>A tar entry's name without the './' that some archives put first.</summary>
    static string ArchiveName(string name)
    {
        while (name.StartsWith("./", StringComparison.Ordinal))
            name = name[2..];
        return name;
    }

    static string FullPath(string root, string path) =>
        Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

    static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Trim();

    static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (1L << 30)} GiB",
        >= 1L << 20 => $"{bytes / (1L << 20)} MiB",
        >= 1L << 10 => $"{bytes / (1L << 10)} KiB",
        _ => $"{bytes} bytes",
    };

    /// <summary>The per-user cache, or the directory that <c>WYL_CACHE_DIR</c> names.</summary>
    static string CacheRoot =>
        Environment.GetEnvironmentVariable("WYL_CACHE_DIR") is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wyl", "cache")
                : Path.Combine(
                    Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
                        ? xdg
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
                    "wyl", "cache");

    /// <summary>A limit was passed, and the diagnostic names the limit that the reader knows.</summary>
    sealed class TooLargeException(long limit) : Exception
    {
        public long Limit { get; } = limit;
    }

    /// <summary>A response's stream, which disposes the response with itself.</summary>
    sealed class OwnedStream(Stream content, IDisposable owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => content.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => content.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                content.Dispose();
                owner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>A stream that stops a reader that expands a compressed archive past the limit.</summary>
    sealed class LimitedStream(Stream content, long limit) : Stream
    {
        long read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(new Span<byte>(buffer, offset, count));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0)
                return 0;
            // Read one byte past the limit, so a stream that ends exactly at the limit is not refused.
            var allowed = (int)Math.Min(buffer.Length, limit - read + 1);
            if (allowed <= 0)
                throw new TooLargeException(limit);
            var count = content.Read(buffer[..allowed]);
            read += count;
            if (read > limit)
                throw new TooLargeException(limit);
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                content.Dispose();
            base.Dispose(disposing);
        }
    }
}
