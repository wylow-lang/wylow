using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Syntax;

/// <summary>
/// What a package's <c>package.wylcfg</c> says (spec programs.manifest), read with
/// Wylow's own lexer: a list of entries, each <c>key = value;</c> or <c>key { entries }</c>, whose
/// value is a string or a list of strings. The keys are fixed, so a misspelled one is an error.
/// <c>dependencies</c> is read to its shape and reported as an error.
/// </summary>
public sealed record Manifest(string Name, Location NameLocation)
{
    public const string FileName = "package.wylcfg";

    /// <summary>The package's kind, a program if the manifest has no <c>kind</c> entry.</summary>
    public PackageKind Kind { get; init; } = PackageKind.Program;

    /// <summary>The key of the <c>kind</c> entry, or null if the manifest has none.</summary>
    public Location? KindLocation { get; init; }

    /// <summary>The package's version, or null if the manifest gives none.</summary>
    public PackageVersion? Version { get; init; }

    /// <summary>The platforms that a <c>native</c> block may hold a block for.</summary>
    public static readonly string[] Platforms = ["windows", "linux", "macos"];

    /// <summary>
    /// The lists of the <c>native</c> block, those of the block itself first and then those of its
    /// platform blocks, each in the order the manifest gives them.
    /// </summary>
    public IReadOnlyList<NativeList> Native { get; init; } = [];

    /// <summary>The lists of the <c>native</c> block that apply when the program is built for <paramref name="platform"/>.</summary>
    public IEnumerable<NativeList> NativeFor(string platform) => Native.Where(n => n.Platform is null || n.Platform == platform);

    /// <summary>
    /// One list of the <c>native</c> block, such as <c>link</c>, with the platform block it is in, or
    /// null if it is in the <c>native</c> block itself.
    /// </summary>
    public sealed record NativeList(string? Platform, string Key, IReadOnlyList<string> Values, Location KeyLocation);

    abstract record Entry(string Key, Location KeyLocation);

    /// <summary><c>key = "text";</c> or <c>key = ["a", "b"];</c>: exactly one of the two is set.</summary>
    sealed record ValueEntry(string Key, Location KeyLocation, string? Text, IReadOnlyList<string>? List) : Entry(Key, KeyLocation);

    sealed record BlockEntry(string Key, Location KeyLocation, IReadOnlyList<Entry> Entries) : Entry(Key, KeyLocation);

    enum ValueKind { String, List, Block }

    /// <summary>The manifest, or null with the errors reported if it doesn't give a usable name.</summary>
    public static Manifest? Read(SourceText source, DiagnosticBag diagnostics)
    {
        int errors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        var reader = new Reader(source, diagnostics, new Lexer(source, diagnostics).Lex());
        var entries = reader.ReadEntries(nested: false);
        var keys = Check(entries, [("name", ValueKind.String), ("kind", ValueKind.String), ("version", ValueKind.String), ("dependencies", ValueKind.Block), ("native", ValueKind.Block)], diagnostics);

        var kind = PackageKind.Program;
        if (keys.GetValueOrDefault("kind") is ValueEntry { Text: { } kindText } kindEntry)
        {
            if (kindText == "library")
                kind = PackageKind.Library;
            else if (kindText != "program")
                diagnostics.Add(Catalog.UnknownPackageKind(kindEntry.KeyLocation, kindText));
        }
        var version = ReadVersion(keys.GetValueOrDefault("version"), patchOptional: false, diagnostics);
        if (kind == PackageKind.Library && !keys.ContainsKey("version"))
            diagnostics.Add(Catalog.LibraryWithoutVersion(keys["kind"].KeyLocation));

        if (keys.GetValueOrDefault("dependencies") is BlockEntry dependencies)
        {
            foreach (var dependency in dependencies.Entries)
            {
                if (dependency is not BlockEntry block)
                {
                    diagnostics.Add(Catalog.DependencyNotBlock(dependency.KeyLocation, dependency.Key));
                    continue;
                }
                var given = Check(block.Entries, [("path", ValueKind.String), ("version", ValueKind.String)], diagnostics);
                if (!block.Entries.Any(e => e.Key == "path"))
                    diagnostics.Add(Catalog.DependencyWithoutPath(block.KeyLocation, block.Key));
                ReadVersion(given.GetValueOrDefault("version"), patchOptional: true, diagnostics);
            }
            diagnostics.Add(Catalog.UnusableManifestKey(dependencies.KeyLocation, "dependencies"));
        }
        List<NativeList> nativeLists = [];
        if (keys.GetValueOrDefault("native") is BlockEntry native)
        {
            var given = Check(native.Entries, [.. NativeListKeys, .. Platforms.Select(p => (p, ValueKind.Block))], diagnostics);
            nativeLists.AddRange(Lists(null, given.Values));
            foreach (var platform in Platforms)
            {
                if (given.GetValueOrDefault(platform) is BlockEntry block)
                    nativeLists.AddRange(Lists(platform, Check(block.Entries, NativeListKeys, diagnostics).Values));
            }
        }

        if (keys.GetValueOrDefault("name") is not ValueEntry { Text: { } name } entry)
        {
            if (!keys.ContainsKey("name"))
                diagnostics.Add(Catalog.ManifestWithoutName(new Location(source, new TextSpan(0, 0))));
            return null;
        }
        if (name == Package.Std.Name)
            diagnostics.Add(Catalog.ReservedPackageName(entry.KeyLocation));
        else if (!Lexer.IsIdentifier(name))
            diagnostics.Add(Catalog.PackageNameNotIdentifier(entry.KeyLocation, name));
        bool failed = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error) > errors;
        return failed ? null : new Manifest(name, entry.KeyLocation)
        {
            Kind = kind,
            KindLocation = keys.GetValueOrDefault("kind")?.KeyLocation,
            Version = version,
            Native = nativeLists,
        };

        static IEnumerable<NativeList> Lists(string? platform, IEnumerable<Entry> entries) =>
            entries.OfType<ValueEntry>().Where(e => e.List is not null).Select(e => new NativeList(platform, e.Key, e.List!, e.KeyLocation));
    }

    /// <summary>The version that a <c>version</c> entry gives, or null with the error reported if its text is not a version.</summary>
    static PackageVersion? ReadVersion(Entry? entry, bool patchOptional, DiagnosticBag diagnostics)
    {
        if (entry is not ValueEntry { Text: { } text })
            return null;
        if (PackageVersion.Parse(text, patchOptional) is { } version)
            return version;
        diagnostics.Add(Catalog.MalformedVersion(entry.KeyLocation, text, patchOptional ? "'major.minor' or 'major.minor.patch'" : "'major.minor.patch'"));
        return null;
    }

    static readonly (string Key, ValueKind Kind)[] NativeListKeys =
        [("include", ValueKind.List), ("libraryPaths", ValueKind.List), ("link", ValueKind.List), ("copy", ValueKind.List), ("defines", ValueKind.List)];

    /// <summary>Checks a list of entries against its keys and their kinds, and returns each one given, by key.</summary>
    static Dictionary<string, Entry> Check(IReadOnlyList<Entry> entries, (string Key, ValueKind Kind)[] schema, DiagnosticBag diagnostics)
    {
        var given = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var kind = Array.FindIndex(schema, s => s.Key == entry.Key) is >= 0 and var index ? schema[index].Kind : (ValueKind?)null;
            if (kind is null)
            {
                var keys = string.Join(", ", schema.Select(s => $"'{s.Key}'"));
                diagnostics.Add(Catalog.UnknownManifestKey(entry.KeyLocation, entry.Key, keys));
                continue;
            }
            if (given.ContainsKey(entry.Key))
            {
                diagnostics.Add(Catalog.DuplicateManifestKey(entry.KeyLocation, entry.Key, given[entry.Key].KeyLocation));
                continue;
            }
            var actual = entry switch
            {
                BlockEntry => ValueKind.Block,
                ValueEntry { Text: not null } => ValueKind.String,
                _ => ValueKind.List,
            };
            if (actual != kind)
            {
                diagnostics.Add(kind switch { ValueKind.String => Catalog.ManifestValueKind(entry.KeyLocation, entry.Key, "a string", $"{entry.Key} = \"...\";"), ValueKind.List => Catalog.ManifestValueKind(entry.KeyLocation, entry.Key, "a list of strings", $"{entry.Key} = [\"...\"];"), _ => Catalog.ManifestValueKind(entry.KeyLocation, entry.Key, "a block of entries", $"{entry.Key} {{ ... }}"), });
                continue;
            }
            given[entry.Key] = entry;
        }
        return given;
    }

    sealed class Reader(SourceText source, DiagnosticBag diagnostics, List<Token> tokens)
    {
        int position;

        Token Current => tokens[position];
        Token Previous => tokens[Math.Max(position - 1, 0)];
        bool At(TokenKind kind) => Current.Kind == kind;

        Token Advance()
        {
            var token = Current;
            if (position < tokens.Count - 1)
                position++;
            return token;
        }

        Location Loc(TextSpan span) => new(source, span);

        void Expected(TextSpan span, string what) => diagnostics.Add(Catalog.ManifestExpected(Loc(span), what));

        public List<Entry> ReadEntries(bool nested)
        {
            var entries = new List<Entry>();
            while (!At(TokenKind.EndOfFile) && !(nested && At(TokenKind.CloseBrace)))
            {
                if (ReadEntry() is { } entry)
                    entries.Add(entry);
            }
            return entries;
        }

        Entry? ReadEntry()
        {
            if (!At(TokenKind.Identifier))
            {
                Expected(Current.Span, "a key");
                // Only a '}' with no block open gets here as one; it would stop Skip where it is.
                if (At(TokenKind.CloseBrace))
                    Advance();
                else
                    Skip();
                return null;
            }
            var key = Advance();
            if (At(TokenKind.OpenBrace))
            {
                Advance();
                var entries = ReadEntries(nested: true);
                if (!At(TokenKind.CloseBrace))
                    Expected(new TextSpan(Previous.Span.End, 0), "'}'");
                Advance();
                return new BlockEntry(key.Text, Loc(key.Span), entries);
            }
            if (!At(TokenKind.Assign))
            {
                Expected(new TextSpan(key.Span.End, 0), $"'=' or '{{' after '{key.Text}'");
                Skip();
                return null;
            }
            Advance();
            ValueEntry? entry = null;
            if (At(TokenKind.OpenBracket))
            {
                Advance();
                var list = new List<string>();
                while (!At(TokenKind.CloseBracket) && ReadString() is { } item)
                {
                    list.Add(item);
                    if (!At(TokenKind.Comma))
                        break;
                    Advance();
                }
                if (At(TokenKind.CloseBracket))
                {
                    Advance();
                    entry = new ValueEntry(key.Text, Loc(key.Span), null, list);
                }
                else
                {
                    Expected(Current.Span, "',' or ']'");
                }
            }
            else if (ReadString() is { } text)
            {
                entry = new ValueEntry(key.Text, Loc(key.Span), text, null);
            }
            if (entry is null)
            {
                Skip();
                return null;
            }
            if (!At(TokenKind.Semicolon))
                Expected(new TextSpan(Previous.Span.End, 0), "';'");
            else
                Advance();
            return entry;
        }

        string? ReadString()
        {
            if (!At(TokenKind.StringLiteral))
            {
                Expected(Current.Span, "a string");
                return null;
            }
            var token = Advance();
            if (token.Value is string text)
                return text;
            diagnostics.Add(Catalog.InterpolatedManifestString(Loc(token.Span)));
            return "";
        }

        /// <summary>Skips past the next ';', or to a '}' or the end, after an entry that couldn't be read.</summary>
        void Skip()
        {
            while (!At(TokenKind.EndOfFile) && !At(TokenKind.CloseBrace))
            {
                if (Advance().Kind == TokenKind.Semicolon)
                    return;
            }
        }
    }
}
