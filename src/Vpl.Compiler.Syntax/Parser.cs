using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Syntax;

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
            Error(Current.Span, "assignment is a statement in VPL and cannot be used inside an expression");
        else
            Error(new TextSpan(Previous.Span.End, 0), $"expected {description}");
        return new Token(kind, new TextSpan(Current.Span.Start, 0), "", null);
    }

    void Error(TextSpan span, string message)
    {
        // One error per position; later errors at the same spot are almost always cascades.
        if (span.Start == lastErrorPosition)
            return;
        lastErrorPosition = span.Start;
        diagnostics.Error(new Location(source, span), message);
    }

    Location Loc(TextSpan span) => new(source, span);

    Location LocationFrom(int startToken) =>
        Loc(TextSpan.FromBounds(tokens[startToken].Span.Start, Previous.Span.End));

    public CompilationUnit ParseCompilationUnit()
    {
        var declarations = new List<Declaration>();
        while (!At(TokenKind.EndOfFile))
        {
            int start = position;
            var annotations = ParseAnnotations();
            var modifiers = ParseModifiers();
            if (At(TokenKind.RefKeyword) && Peek(1).Kind == TokenKind.StructKeyword)
            {
                Advance();
                modifiers = modifiers with { Flags = modifiers.Flags | Modifiers.Ref };
            }
            if (TypeKeywordKind(Current.Kind) is { } kind)
            {
                var allowed = kind switch
                {
                    TypeKind.Class => Modifiers.Public | Modifiers.Private | Modifiers.Abstract | Modifiers.Final,
                    TypeKind.Struct => Modifiers.Public | Modifiers.Private | Modifiers.Ref | Modifiers.Extern,
                    _ => Modifiers.Public | Modifiers.Private | Modifiers.Ref,
                };
                Disallow(modifiers, ~allowed, token => $"'{token.Text}' is not valid on {Article(kind)}");
                if ((modifiers.Flags & (Modifiers.Abstract | Modifiers.Final)) == (Modifiers.Abstract | Modifiers.Final))
                    Error(modifiers.Tokens.First(t => t.Flag == Modifiers.Final).Token.Span, "a class cannot be both 'abstract' and 'final'");
                declarations.Add(ParseTypeDecl(start, modifiers.Flags, kind) with { Annotations = annotations });
                continue;
            }
            if (SyntaxFacts.IsTypeStart(Current.Kind))
            {
                Disallow(modifiers, ~(Modifiers.Public | Modifiers.Private | Modifiers.Unsafe | Modifiers.Extern), token => $"'{token.Text}' is not valid on a function");
                var returnType = ParseType();
                var name = Expect(TokenKind.Identifier, "a function name");
                if (name.Text != "" && (At(TokenKind.Assign) || At(TokenKind.Semicolon)))
                {
                    SkipTopLevelVariable(name);
                    continue;
                }
                var function = ParseFunctionRest(start, modifiers.Flags, returnType, name);
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
            Error(Current.Span, At(TokenKind.ReservedKeyword)
                ? $"'{Current.Text}' is not supported yet"
                : "expected a function or type declaration");
            SkipDeclaration();
            if (position == start)
                Advance();
        }
        return new CompilationUnit(source, declarations);
    }

    static TypeKind? TypeKeywordKind(TokenKind kind) => kind switch
    {
        TokenKind.StructKeyword => TypeKind.Struct,
        TokenKind.ClassKeyword => TypeKind.Class,
        TokenKind.InterfaceKeyword => TypeKind.Interface,
        TokenKind.EnumKeyword => TypeKind.Enum,
        _ => null,
    };

    static string Article(TypeKind kind) => kind switch
    {
        TypeKind.Struct => "a struct",
        TypeKind.Class => "a class",
        TypeKind.Enum => "an enum",
        _ => "an interface",
    };

    // Reported once at the name, rather than as a malformed function that confuses what follows.
    void SkipTopLevelVariable(Token name)
    {
        Error(name.Span, $"top-level variables and constants are not supported yet; declare '{name.Text}' inside a function or type");
        SkipDeclaration();
    }

    void RequireBody(FunctionDecl function)
    {
        bool isExtern = (function.Modifiers & Modifiers.Extern) != 0;
        if (function.Body is null && !isExtern)
            Error(function.NameLocation.Span, $"'{function.Name}' must have a body");
        else if (function.Body is not null && isExtern)
            Error(function.NameLocation.Span, $"extern function '{function.Name}' cannot have a body; it is implemented in C++");
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
            if (At(TokenKind.OpenParen) && !StartsParenthesizedType())
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

    /// <summary>
    /// Whether the parenthesis here opens the type an annotation is on, as in
    /// <c>@Escaping (int) -> int</c> or <c>@Escaping ((int) -> int)?</c>, rather than its arguments.
    /// Parentheses in a type are always followed by one of these, and an annotation's arguments never are.
    /// </summary>
    bool StartsParenthesizedType()
    {
        int depth = 0;
        for (int i = position; i < tokens.Count; i++)
        {
            var kind = tokens[i].Kind;
            if (kind == TokenKind.OpenParen)
                depth++;
            else if (kind == TokenKind.CloseParen && --depth == 0)
                return i + 1 < tokens.Count && tokens[i + 1].Kind is TokenKind.Arrow or TokenKind.Question or TokenKind.OpenBracket or TokenKind.Star;
            else if (kind is TokenKind.EndOfFile or TokenKind.Semicolon or TokenKind.OpenBrace or TokenKind.CloseBrace)
                return false;
        }
        return false;
    }

    // A name is read as a type: annotations take types and literals, and no constant has a name yet.
    Node ParseAnnotationArgument() =>
        SyntaxFacts.IsPrimitiveTypeKeyword(Current.Kind) || At(TokenKind.Identifier) ? ParseType(allowNullable: false) : ParseExpression();

    sealed record ModifierList(Modifiers Flags, List<(Modifiers Flag, Token Token)> Tokens);

    ModifierList ParseModifiers()
    {
        var flags = Modifiers.None;
        var tokens = new List<(Modifiers, Token)>();
        while (true)
        {
            var flag = Current.Kind switch
            {
                TokenKind.PublicKeyword => Modifiers.Public,
                TokenKind.PrivateKeyword => Modifiers.Private,
                TokenKind.ProtectedKeyword => Modifiers.Protected,
                TokenKind.StaticKeyword => Modifiers.Static,
                TokenKind.ReadonlyKeyword => Modifiers.ReadOnly,
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
                return new ModifierList(flags, tokens);

            var token = Advance();
            if ((flags & flag) != 0)
                Error(token.Span, $"duplicate '{token.Text}' modifier");
            else if (flag is Modifiers.Public or Modifiers.Private or Modifiers.Protected
                     && (flags & (Modifiers.Public | Modifiers.Private | Modifiers.Protected)) != 0)
                Error(token.Span, "only one access modifier is allowed");
            flags |= flag;
            tokens.Add((flag, token));
        }
    }

    void Disallow(ModifierList modifiers, Modifiers disallowed, Func<Token, string> message)
    {
        foreach (var (flag, token) in modifiers.Tokens)
        {
            if ((flag & disallowed) != 0)
                Error(token.Span, message(token));
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
