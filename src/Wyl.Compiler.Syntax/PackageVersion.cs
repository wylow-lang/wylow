using System.Numerics;

namespace Wyl.Compiler.Syntax;

/// <summary>A package's version, or the version that a dependency entry states (spec programs.manifest.versions).</summary>
public sealed record PackageVersion(BigInteger Major, BigInteger Minor, BigInteger Patch)
{
    /// <summary>
    /// The version that <paramref name="text"/> writes, or null if the text does not have the form
    /// <c>major.minor.patch</c>. If <paramref name="patchOptional"/> is set, <c>major.minor</c> is
    /// also a version, whose patch number is 0.
    /// </summary>
    public static PackageVersion? Parse(string text, bool patchOptional)
    {
        var parts = text.Split('.');
        if (parts.Length != 3 && !(patchOptional && parts.Length == 2))
            return null;
        if (!parts.All(IsNumber))
            return null;
        return new PackageVersion(BigInteger.Parse(parts[0]), BigInteger.Parse(parts[1]), parts.Length == 3 ? BigInteger.Parse(parts[2]) : 0);

        // Decimal digits, with no leading zero except in "0" itself.
        static bool IsNumber(string part) => part.Length > 0 && part.All(char.IsAsciiDigit) && (part.Length == 1 || part[0] != '0');
    }

    /// <summary>
    /// Whether this version, a library's, satisfies <paramref name="stated"/>, the version that a
    /// dependency entry states: it is not lower, and it has the same major number, and below 1.0 the
    /// same minor number too.
    /// </summary>
    public bool Satisfies(PackageVersion stated)
    {
        if (Major != stated.Major || (stated.Major == 0 && Minor != stated.Minor))
            return false;
        return (Minor, Patch).CompareTo((stated.Minor, stated.Patch)) >= 0;
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
