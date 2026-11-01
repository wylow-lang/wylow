using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Syntax;

/// <summary>
/// What a package's <c>package.wylcfg</c> says (spec 06-syntax.md §6.1, The manifest), read with
/// Wylow's own lexer: a list of entries, each <c>key = value;</c> or <c>key { entries }</c>, whose
/// value is a string or a list of strings. The keys are fixed, so a misspelled one is an error.
/// Only <c>name</c> means anything yet; <c>dependencies</c> and <c>native</c> are read to their shape
/// and reported as not supported.
/// </summary>
public sealed record Manifest(string Name, Location NameLocation)
{
    public const string FileName = "package.wylcfg";

    abstract record Entry(string Key, Location KeyLocation);

    /// <summary><c>key = "text";</c> or <c>key = ["a", "b"];</c>: exactly one of the two is set.</summary>
    sealed record ValueEntry(string Key, Location KeyLocation, string? Text, IReadOnlyList<string>? List) : Entry(Key, KeyLocation);

    sealed record BlockEntry(string Key, Location KeyLocation, IReadOnlyList<Entry> Entries) : Entry(Key, KeyLocation);

    enum Kind { String, List, Block }

    /// <summary>The manifest, or null with the errors reported if it doesn't give a usable name.</summary>
    public static Manifest? Read(SourceText source, DiagnosticBag diagnostics)
    {
        int errors = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        var reader = new Reader(source, diagnostics, new Lexer(source, diagnostics).Lex());
        var entries = reader.ReadEntries(nested: false);
        var keys = Check(entries, [("name", Kind.String), ("dependencies", Kind.Block), ("native", Kind.Block)], diagnostics);

        if (keys.GetValueOrDefault("dependencies") is BlockEntry dependencies)
        {
            foreach (var dependency in dependencies.Entries)
            {
                if (dependency is not BlockEntry block)
                    diagnostics.Error(dependency.KeyLocation, $"a dependency is a block, as in '{dependency.Key} {{ path = \"../{dependency.Key}\"; }}'");
                else if (Check(block.Entries, [("path", Kind.String)], diagnostics).Count == 0 && !block.Entries.Any(e => e.Key == "path"))
                    diagnostics.Error(block.KeyLocation, $"dependency '{block.Key}' needs a 'path'");
            }
            diagnostics.Error(dependencies.KeyLocation, "'dependencies' is not supported yet");
        }
        if (keys.GetValueOrDefault("native") is BlockEntry native)
        {
            Check(native.Entries, [("include", Kind.List), ("link", Kind.List)], diagnostics);
            diagnostics.Error(native.KeyLocation, "'native' is not supported yet");
        }

        if (keys.GetValueOrDefault("name") is not ValueEntry { Text: { } name } entry)
        {
            if (!keys.ContainsKey("name"))
                diagnostics.Error(new Location(source, new TextSpan(0, 0)), "the manifest must give the package's 'name', as in 'name = \"my_game\";'");
            return null;
        }
        if (name == Package.Std.Name)
            diagnostics.Error(entry.KeyLocation, "'std' is the standard library's name");
        else if (!Lexer.IsIdentifier(name))
            diagnostics.Error(entry.KeyLocation, $"'{name}' is not a package name, which is written like a module's, as in 'voxel_game'");
        bool failed = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error) > errors;
        return failed ? null : new Manifest(name, entry.KeyLocation);
    }

    /// <summary>Checks a list of entries against its keys and their kinds, and returns each one given, by key.</summary>
    static Dictionary<string, Entry> Check(IReadOnlyList<Entry> entries, (string Key, Kind Kind)[] schema, DiagnosticBag diagnostics)
    {
        var given = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var kind = Array.FindIndex(schema, s => s.Key == entry.Key) is >= 0 and var index ? schema[index].Kind : (Kind?)null;
            if (kind is null)
            {
                var keys = string.Join(", ", schema.Select(s => $"'{s.Key}'"));
                diagnostics.Error(entry.KeyLocation, $"'{entry.Key}' is not a key here; the keys are {keys}");
                continue;
            }
            if (given.ContainsKey(entry.Key))
            {
                diagnostics.Error(entry.KeyLocation, $"'{entry.Key}' is given more than once");
                continue;
            }
            var actual = entry switch
            {
                BlockEntry => Kind.Block,
                ValueEntry { Text: not null } => Kind.String,
                _ => Kind.List,
            };
            if (actual != kind)
            {
                diagnostics.Error(entry.KeyLocation, kind switch
                {
                    Kind.String => $"'{entry.Key}' is a string, as in '{entry.Key} = \"...\";'",
                    Kind.List => $"'{entry.Key}' is a list of strings, as in '{entry.Key} = [\"...\"];'",
                    _ => $"'{entry.Key}' is a block of entries, as in '{entry.Key} {{ ... }}'",
                });
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

        void Error(TextSpan span, string message) => diagnostics.Error(Loc(span), message);

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
                Error(Current.Span, "expected a key, as in 'name = \"my_game\";'");
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
                    Error(new TextSpan(Previous.Span.End, 0), "expected '}'");
                Advance();
                return new BlockEntry(key.Text, Loc(key.Span), entries);
            }
            if (!At(TokenKind.Assign))
            {
                Error(new TextSpan(key.Span.End, 0), $"expected '=' or '{{' after '{key.Text}'");
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
                    Error(Current.Span, "expected ',' or ']'");
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
                Error(new TextSpan(Previous.Span.End, 0), "expected ';'");
            else
                Advance();
            return entry;
        }

        string? ReadString()
        {
            if (!At(TokenKind.StringLiteral))
            {
                Error(Current.Span, "expected a string, as in \"text\"");
                return null;
            }
            var token = Advance();
            if (token.Value is string text)
                return text;
            Error(token.Span, "a manifest's strings can't be interpolated");
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
