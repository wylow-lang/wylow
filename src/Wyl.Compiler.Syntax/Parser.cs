using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Syntax;

public sealed partial class Parser
{
    readonly SourceText source;
    readonly DiagnosticBag diagnostics;
    readonly List<Token> tokens;
    int position;
    int lastErrorPosition = -1;

    public Parser(SourceText source, DiagnosticBag diagnostics)
        : this(source, diagnostics, new Lexer(source, diagnostics).Lex())
    {
    }

    Parser(SourceText source, DiagnosticBag diagnostics, List<Token> tokens)
    {
        this.source = source;
        this.diagnostics = diagnostics;
        this.tokens = tokens;
    }

    Token Current => tokens[position];
    Token Previous => tokens[Math.Max(position - 1, 0)];
    Token Peek(int offset) => tokens[Math.Min(position + offset, tokens.Count - 1)];
    bool At(TokenKind kind) => Current.Kind == kind;
    TokenKind KindAt(int index) => tokens[Math.Min(index, tokens.Count - 1)].Kind;

    Token Advance()
    {
        var token = Current;
        if (position < tokens.Count - 1)
            position++;
        return token;
    }

    bool Accept(TokenKind kind)
    {
        if (!At(kind))
            return false;
        Advance();
        return true;
    }

    Token Expect(TokenKind kind, string description)
    {
        if (At(kind))
            return Advance();
        if (SyntaxFacts.IsAssignmentOperator(Current.Kind))
            Report(Catalog.AssignmentInExpression(Loc(Current.Span)));
        else
            Report(Catalog.Expected(Loc(new TextSpan(Previous.Span.End, 0)), description));
        return new Token(kind, new TextSpan(Current.Span.Start, 0), "", null);
    }

    void Report(Diagnostic diagnostic)
    {
        // One error per position; later errors at the same spot are almost always cascades.
        if (diagnostic.Location.Span.Start == lastErrorPosition)
            return;
        lastErrorPosition = diagnostic.Location.Span.Start;
        diagnostics.Add(diagnostic);
    }

    Location Loc(TextSpan span) => new(source, span);

    Location LocationFrom(int startToken) =>
        Loc(TextSpan.FromBounds(tokens[startToken].Span.Start, Previous.Span.End));

    public CompilationUnit ParseCompilationUnit()
    {
        var imports = new List<ImportDecl>();
        var declarations = new List<Declaration>();
        while (!At(TokenKind.EndOfFile))
        {
            int start = position;
            var annotations = ParseAnnotations();
            var modifiers = ParseModifiers();
            if (At(TokenKind.ImportKeyword))
            {
                if (annotations.Count > 0)
                    Report(Catalog.ImportWithAnnotations(annotations[0].Location));
                Disallow(modifiers, ~Modifiers.None, token => Catalog.ModifierOnImport(Loc(token.Span), token.Text));
                // So a file's first lines say what it uses.
                if (declarations.Count > 0)
                    Report(Catalog.ImportAfterDeclaration(Loc(Current.Span)));
                imports.Add(ParseImport(start, modifiers.Visibility));
                continue;
            }
            if (At(TokenKind.RefKeyword) && Peek(1).Kind == TokenKind.StructKeyword)
            {
                Advance();
                modifiers = modifiers with { Flags = modifiers.Flags | Modifiers.Ref };
            }
            if (TypeKeywordKind(Current.Kind) is { } kind)
            {
                var allowed = kind switch
                {
                    TypeKind.Class => Modifiers.Abstract | Modifiers.Final,
                    TypeKind.Struct => Modifiers.Ref | Modifiers.Extern,
                    _ => Modifiers.Ref,
                };
                Disallow(modifiers, ~allowed, token => Catalog.ModifierOnType(Loc(token.Span), token.Text, SyntaxFacts.KindNameWithArticle(kind)));
                if ((modifiers.Flags & (Modifiers.Abstract | Modifiers.Final)) == (Modifiers.Abstract | Modifiers.Final))
                    Report(Catalog.AbstractFinalClass(Loc(modifiers.Tokens.First(t => t.Flag == Modifiers.Final).Token.Span)));
                declarations.Add(ParseTypeDecl(start, modifiers, kind) with { Annotations = annotations });
                continue;
            }
            // A '(' is a grouped function type, or the old spelling, which ParseType reports.
            if (SyntaxFacts.IsTypeStart(Current.Kind) || At(TokenKind.OpenParen))
            {
                Disallow(modifiers, ~(Modifiers.Unsafe | Modifiers.Extern), token => Catalog.ModifierOnFunction(Loc(token.Span), token.Text));
                var returnType = ParseType();
                var name = Expect(TokenKind.Identifier, "a function name");
                if (name.Text != "" && (At(TokenKind.Assign) || At(TokenKind.Semicolon)))
                {
                    SkipTopLevelVariable(name);
                    continue;
                }
                var function = ParseFunctionRest(start, modifiers, returnType, name);
                RequireBody(function);
                declarations.Add(function with { Annotations = annotations });
                continue;
            }
            if (At(TokenKind.VarKeyword) && Peek(1).Kind == TokenKind.Identifier)
            {
                Advance();
                SkipTopLevelVariable(Advance());
                continue;
            }
            Report(At(TokenKind.ReservedKeyword) ? Catalog.ReservedKeyword(Loc(Current.Span), Current.Text) : Catalog.Expected(Loc(Current.Span), "a function or type declaration"));
            SkipDeclaration();
            if (position == start)
                Advance();
        }
        return new CompilationUnit(source, imports, declarations);
    }

    /// <summary>
    /// <c>import a::b::C;</c>, <c>import a::b::{C, D as E};</c>, or <c>import a::b::*;</c>, with
    /// <c>as</c> also after a single name; the visibility before it is already read.
    /// </summary>
    ImportDecl ParseImport(int start, Visibility visibility)
    {
        Advance();
        var path = new List<PathName> { ParsePathName("a module or package name") };
        var names = new List<ImportedName>();
        bool all = false;
        while (Accept(TokenKind.ColonColon))
        {
            if (Accept(TokenKind.Star))
            {
                all = true;
                break;
            }
            if (Accept(TokenKind.OpenBrace))
            {
                if (!At(TokenKind.CloseBrace))
                {
                    do
                        names.Add(ParseImportedName());
                    while (Accept(TokenKind.Comma) && !At(TokenKind.CloseBrace));
                }
                else
                {
                    Report(Catalog.Expected(Loc(Current.Span), "a name to import"));
                }
                Expect(TokenKind.CloseBrace, "'}'");
                break;
            }
            path.Add(ParsePathName("a name"));
        }
        if (!all && names.Count == 0)
        {
            var last = path[^1];
            path.RemoveAt(path.Count - 1);
            names.Add(new ImportedName(last, ParseAlias()));
        }
        var location = LocationFrom(start);
        if (!Accept(TokenKind.Semicolon))
        {
            Report(Catalog.Expected(Loc(new TextSpan(Previous.Span.End, 0)), "';'"));
            if (!At(TokenKind.ImportKeyword))
                SkipDeclaration();
        }
        return new ImportDecl(location, visibility, path, names, all);
    }

    ImportedName ParseImportedName() => new(ParsePathName("a name to import"), ParseAlias());

    // `as` is a name like any other everywhere but here, so it isn't a keyword.
    PathName? ParseAlias()
    {
        if (!At(TokenKind.Identifier) || Current.Text != "as")
            return null;
        Advance();
        return ParsePathName("the name to import it as");
    }

    PathName ParsePathName(string description)
    {
        var name = Expect(TokenKind.Identifier, description);
        return new PathName(name.Text, Loc(name.Span));
    }

    /// <summary>
    /// The <c>a::b::</c> before a name, as in <c>a::b::c</c>; empty if the identifier here isn't
    /// followed by <c>::</c>. Leaves the last name unconsumed.
    /// </summary>
    List<PathName> ParseModulePath()
    {
        var modules = new List<PathName>();
        while (At(TokenKind.Identifier) && Peek(1).Kind == TokenKind.ColonColon)
        {
            var name = Advance();
            modules.Add(new PathName(name.Text, Loc(name.Span)));
            Advance();
        }
        return modules;
    }

    static TypeKind? TypeKeywordKind(TokenKind kind) => kind switch
    {
        TokenKind.StructKeyword => TypeKind.Struct,
        TokenKind.ClassKeyword => TypeKind.Class,
        TokenKind.InterfaceKeyword => TypeKind.Interface,
        TokenKind.EnumKeyword => TypeKind.Enum,
        _ => null,
    };


    // Reported once at the name, rather than as a malformed function that confuses what follows.
    void SkipTopLevelVariable(Token name)
    {
        Report(Catalog.TopLevelVariable(Loc(name.Span), name.Text));
        SkipDeclaration();
    }

    void RequireBody(FunctionDecl function)
    {
        bool isExtern = (function.Modifiers & Modifiers.Extern) != 0;
        if (function.Body is null && !isExtern)
            Report(Catalog.MissingBody(function.NameLocation, function.Name));
        else if (function.Body is not null && isExtern)
            Report(Catalog.ExternBody(function.NameLocation, function.Name));
    }

    List<AnnotationSyntax> ParseAnnotations()
    {
        var annotations = new List<AnnotationSyntax>();
        while (At(TokenKind.At))
        {
            int start = position;
            Advance();
            var name = Expect(TokenKind.Identifier, "an annotation name");
            var arguments = new List<Node>();
            // `@Name (fn(int) -> int)? f`: parentheses starting with `fn` group the annotated declaration's type (spec annotations.syntax).
            if (At(TokenKind.OpenParen) && !StartsType(position))
            {
                Advance();
                if (!At(TokenKind.CloseParen))
                {
                    do
                        arguments.Add(ParseAnnotationArgument());
                    while (Accept(TokenKind.Comma));
                }
                Expect(TokenKind.CloseParen, "')'");
            }
            annotations.Add(new AnnotationSyntax(LocationFrom(start), name.Text, arguments));
        }
        return annotations;
    }

    // A name is read as a type: annotations take types and literals, and no constant has a name yet.
    Node ParseAnnotationArgument() =>
        SyntaxFacts.IsPrimitiveTypeKeyword(Current.Kind) || At(TokenKind.Identifier) ? ParseType(allowNullable: false) : ParseExpression();

    /// <summary>A declaration's modifiers, and its visibility with where it is written, if it is.</summary>
    sealed record ModifierList(Modifiers Flags, List<(Modifiers Flag, Token Token)> Tokens, Visibility Visibility, TextSpan? VisibilitySpan);

    ModifierList ParseModifiers()
    {
        var flags = Modifiers.None;
        var tokens = new List<(Modifiers, Token)>();
        var visibility = Visibility.File;
        TextSpan? visibilitySpan = null;
        while (true)
        {
            if (SyntaxFacts.IsVisibilityKeyword(Current.Kind))
            {
                int start = Current.Span.Start;
                var level = ParseVisibility();
                var span = TextSpan.FromBounds(start, Previous.Span.End);
                if (visibilitySpan is not null)
                {
                    Report(Catalog.DuplicateVisibility(Loc(span)));
                    continue;
                }
                if (tokens.Count > 0)
                {
                    var written = level == Visibility.File ? "pub" : SyntaxFacts.VisibilityText(level);
                    Report(Catalog.VisibilityNotFirst(Loc(span), written, tokens[0].Item2.Text));
                }
                (visibility, visibilitySpan) = (level, span);
                continue;
            }
            if (At(TokenKind.EscapingKeyword))
            {
                Report(Catalog.EscapingElsewhere(Loc(Current.Span)));
                Advance();
                continue;
            }
            var flag = Current.Kind switch
            {
                TokenKind.StaticKeyword => Modifiers.Static,
                TokenKind.MutKeyword => Modifiers.Mut,
                TokenKind.VirtualKeyword => Modifiers.Virtual,
                TokenKind.OverrideKeyword => Modifiers.Override,
                TokenKind.AbstractKeyword => Modifiers.Abstract,
                TokenKind.FinalKeyword => Modifiers.Final,
                TokenKind.UnsafeKeyword => Modifiers.Unsafe,
                TokenKind.ExternKeyword => Modifiers.Extern,
                _ => Modifiers.None,
            };
            if (flag == Modifiers.None)
                return new ModifierList(flags, tokens, visibility, visibilitySpan);

            var token = Advance();
            if ((flags & flag) != 0)
                Report(Catalog.DuplicateModifier(Loc(token.Span), token.Text));
            flags |= flag;
            tokens.Add((flag, token));
        }
    }

    /// <summary><c>pub</c>, <c>pub(module)</c>, or <c>pub(package)</c>; or a Java access keyword, reported as what to write instead.</summary>
    Visibility ParseVisibility()
    {
        var keyword = Advance();
        if (keyword.Kind == TokenKind.JavaAccessKeyword)
        {
            Report(Catalog.NotAVisibility(Loc(keyword.Span), keyword.Text));
            return keyword.Text == "public" ? Visibility.Public : Visibility.File;
        }
        // Only a name in parentheses is a visibility; `pub (fn() -> void)? f;` is a field.
        if (!At(TokenKind.OpenParen) || Peek(1).Kind is not (TokenKind.Identifier or TokenKind.ReservedKeyword) || Peek(2).Kind != TokenKind.CloseParen)
            return Visibility.Public;
        Advance();
        var scope = Advance();
        Advance();
        switch (scope.Text)
        {
            case "module":
                return Visibility.Module;
            case "package":
                return Visibility.Package;
            default:
                Report(Catalog.UnknownVisibilityScope(Loc(scope.Span), scope.Text));
                return Visibility.Public;
        }
    }

    void Disallow(ModifierList modifiers, Modifiers disallowed, Func<Token, Diagnostic> message)
    {
        foreach (var (flag, token) in modifiers.Tokens)
        {
            if ((flag & disallowed) != 0)
                Report(message(token));
        }
    }

    /// <summary>Skips to the end of a declaration, leaving an enclosing '}' unconsumed.</summary>
    void SkipDeclaration()
    {
        while (!At(TokenKind.EndOfFile) && !At(TokenKind.CloseBrace))
        {
            if (Accept(TokenKind.Semicolon))
                return;
            if (At(TokenKind.OpenBrace))
            {
                int depth = 0;
                do
                {
                    if (At(TokenKind.OpenBrace))
                        depth++;
                    else if (At(TokenKind.CloseBrace))
                        depth--;
                    Advance();
                } while (depth > 0 && !At(TokenKind.EndOfFile));
                return;
            }
            Advance();
        }
    }
}
