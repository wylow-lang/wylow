using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Wyl.Harness;

/// <summary>Writes a finding under a name that dedupes it: the same key and signature replace one file.</summary>
public static class Findings
{
    /// <param name="header">The comment lines above the program: what found it and how to replay it.</param>
    /// <returns>The file written.</returns>
    public static string Write(string directory, string key, string signature, string body, IReadOnlyList<string> header)
    {
        var slug = Regex.Replace(key.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 60)
            slug = slug[..60].Trim('-');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key + "\n" + signature)))[..8].ToLowerInvariant();
        var path = Path.Combine(directory, $"{slug}-{hash}.wyl");
        var text = new StringBuilder();
        foreach (var line in header)
            text.AppendLine(line);
        text.AppendLine();
        text.Append(body.TrimEnd('\n')).Append('\n');
        File.WriteAllText(path, text.ToString());
        return path;
    }
}
