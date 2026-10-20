using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Syntax;

public sealed class Parser
{
    readonly SourceText source;
    readonly DiagnosticBag diagnostics;
    readonly List<Token> tokens;
    int position;
    int lastErrorPosition = -1;

    public Parser(SourceText source, DiagnosticBag diagnostics)
    {
        this.source = source;
        this.diagnostics = diagnostics;
        tokens = new Lexer(source, diagnostics).Lex();
    }

    Token Current => tokens[position];
    Token Previous => tokens[Math.Max(position - 1, 0)];
    Token Peek(int offset) => tokens[Math.Min(position + offset, tokens.Count - 1)];
    bool At(TokenKind kind) => Current.Kind == kind;

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
            var modifiers = ParseModifiers();
            if (TypeKeywordKind(Current.Kind) is { } kind)
            {
                var allowed = kind == TypeKind.Class
                    ? Modifiers.Public | Modifiers.Private | Modifiers.Abstract | Modifiers.Final
                    : Modifiers.Public | Modifiers.Private;
                Disallow(modifiers, ~allowed, token => $"'{token.Text}' is not valid on {Article(kind)}");
                if ((modifiers.Flags & (Modifiers.Abstract | Modifiers.Final)) == (Modifiers.Abstract | Modifiers.Final))
                    Error(modifiers.Tokens.First(t => t.Flag == Modifiers.Final).Token.Span, "a class cannot be both 'abstract' and 'final'");
                declarations.Add(ParseTypeDecl(start, modifiers.Flags, kind));
                continue;
            }
            if (SyntaxFacts.IsTypeStart(Current.Kind))
            {
                Disallow(modifiers, ~(Modifiers.Public | Modifiers.Private), token => $"'{token.Text}' is not valid on a function");
                var returnType = ParseType();
                var name = Expect(TokenKind.Identifier, "a function name");
                var function = ParseFunctionRest(start, modifiers.Flags, returnType, name);
                RequireBody(function);
                declarations.Add(function);
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
        _ => null,
    };

    static string Article(TypeKind kind) => kind switch
    {
        TypeKind.Struct => "a struct",
        TypeKind.Class => "a class",
        _ => "an interface",
    };

    void RequireBody(FunctionDecl function)
    {
        if (function.Body is null)
            Error(function.NameLocation.Span, $"'{function.Name}' must have a body");
    }

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
                TokenKind.VirtualKeyword => Modifiers.Virtual,
                TokenKind.OverrideKeyword => Modifiers.Override,
                TokenKind.AbstractKeyword => Modifiers.Abstract,
                TokenKind.FinalKeyword => Modifiers.Final,
                TokenKind.WeakKeyword => Modifiers.Weak,
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

    sealed class Members
    {
        public readonly List<FieldDecl> Fields = [];
        public readonly List<FunctionDecl> Methods = [];
        public DestructorDecl? Destructor;
    }

    TypeDecl ParseTypeDecl(int start, Modifiers modifiers, TypeKind kind)
    {
        Advance();
        var name = Expect(TokenKind.Identifier, kind switch
        {
            TypeKind.Struct => "a struct name",
            TypeKind.Class => "a class name",
            _ => "an interface name",
        });
        var typeParameters = ParseTypeParameters();

        TypeSyntax? baseType = null;
        var interfaces = new List<TypeSyntax>();
        if (At(TokenKind.ExtendsKeyword))
        {
            var extends = Advance();
            if (kind == TypeKind.Interface)
            {
                do
                    interfaces.Add(ParseType());
                while (Accept(TokenKind.Comma));
            }
            else
            {
                if (kind == TypeKind.Struct)
                    Error(extends.Span, "structs cannot inherit; they may only implement interfaces");
                baseType = ParseType();
            }
        }
        if (At(TokenKind.ImplementsKeyword))
        {
            var implements = Advance();
            if (kind == TypeKind.Interface)
                Error(implements.Span, "interfaces extend other interfaces with 'extends'");
            do
                interfaces.Add(ParseType());
            while (Accept(TokenKind.Comma));
        }

        Expect(TokenKind.OpenBrace, "'{'");
        var members = new Members();
        while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
        {
            int before = position;
            ParseMember(kind, name.Text, members);
            if (position == before)
                Advance();
        }
        Expect(TokenKind.CloseBrace, "'}'");
        return new TypeDecl(LocationFrom(start), modifiers, kind, name.Text, Loc(name.Span), typeParameters, baseType, interfaces,
            members.Fields, members.Methods, members.Destructor);
    }

    List<TypeParameterSyntax> ParseTypeParameters()
    {
        var parameters = new List<TypeParameterSyntax>();
        if (!Accept(TokenKind.Less))
            return parameters;
        do
        {
            int start = position;
            var name = Expect(TokenKind.Identifier, "a type parameter name");
            var bounds = new List<TypeSyntax>();
            if (Accept(TokenKind.ExtendsKeyword))
            {
                do
                    bounds.Add(ParseType());
                while (Accept(TokenKind.Ampersand));
            }
            parameters.Add(new TypeParameterSyntax(bounds.Count == 0 ? Loc(name.Span) : LocationFrom(start), name.Text, bounds));
        } while (Accept(TokenKind.Comma));
        ExpectCloseAngle();
        return parameters;
    }

    List<TypeSyntax> ParseTypeArguments()
    {
        var arguments = new List<TypeSyntax>();
        Advance();
        do
            arguments.Add(ParseType());
        while (Accept(TokenKind.Comma));
        ExpectCloseAngle();
        return arguments;
    }

    void ExpectCloseAngle()
    {
        // `Box<Box<int>>` lexes its end as '>>'; split it so each type argument list closes once.
        if (At(TokenKind.GreaterGreater))
        {
            int start = Current.Span.Start;
            tokens[position] = new Token(TokenKind.Greater, new TextSpan(start, 1), ">", null);
            tokens.Insert(position + 1, new Token(TokenKind.Greater, new TextSpan(start + 1, 1), ">", null));
        }
        Expect(TokenKind.Greater, "'>'");
    }

    /// <summary>
    /// Checks without consuming whether a type argument list starts at token <paramref name="index"/>,
    /// and returns the index just past it, or -1. Like C#, <c>a &lt; b &gt; (c)</c> is read as a generic call.
    /// </summary>
    int ScanTypeArguments(int index)
    {
        if (index >= tokens.Count || tokens[index].Kind != TokenKind.Less)
            return -1;
        int depth = 0;
        for (int i = index; i < tokens.Count; i++)
        {
            var kind = tokens[i].Kind;
            if (kind == TokenKind.Less)
                depth++;
            else if (kind == TokenKind.Greater)
                depth--;
            else if (kind == TokenKind.GreaterGreater)
                depth -= 2;
            else if (kind is not (TokenKind.Comma or TokenKind.Question) && !SyntaxFacts.IsTypeStart(kind))
                return -1;

            if (depth == 0)
                return i + 1;
            if (depth < 0)
                return -1;
        }
        return -1;
    }

    /// <summary>Type arguments directly after a name in an expression, only when followed by '(', '.' or '{'.</summary>
    List<TypeSyntax> ParseExpressionTypeArguments()
    {
        int end = ScanTypeArguments(position);
        if (end < 0 || tokens[Math.Min(end, tokens.Count - 1)].Kind is not (TokenKind.OpenParen or TokenKind.Dot or TokenKind.OpenBrace))
            return [];
        return ParseTypeArguments();
    }

    const Modifiers InheritanceModifiers = Modifiers.Virtual | Modifiers.Override | Modifiers.Abstract | Modifiers.Final;

    void ParseMember(TypeKind kind, string typeName, Members members)
    {
        int start = position;
        var modifiers = ParseModifiers();
        switch (kind)
        {
            case TypeKind.Struct:
                Disallow(modifiers, Modifiers.Protected | InheritanceModifiers,
                    token => $"'{token.Text}' is not valid on struct members; structs cannot be inherited");
                Disallow(modifiers, Modifiers.Weak, _ => "'weak' is only valid on class fields");
                break;
            case TypeKind.Class:
                Disallow(modifiers, Modifiers.ReadOnly, _ => "'readonly' is only valid on struct methods");
                break;
            case TypeKind.Interface:
                Disallow(modifiers, ~Modifiers.None, _ => "interface members cannot have modifiers");
                break;
        }

        if (At(TokenKind.Tilde) && Peek(1).Kind == TokenKind.Identifier && Peek(2).Kind == TokenKind.OpenParen)
        {
            ParseDestructor(start, kind, typeName, modifiers, members);
            return;
        }

        if (At(TokenKind.Identifier) && Current.Text == typeName && Peek(1).Kind == TokenKind.OpenParen)
        {
            Disallow(modifiers, Modifiers.Static | Modifiers.ReadOnly | Modifiers.Weak | InheritanceModifiers,
                token => $"'{token.Text}' is not valid on a constructor");
            var constructorName = Advance();
            if (kind == TypeKind.Interface)
                Error(constructorName.Span, "interfaces cannot have constructors");
            var constructor = ParseFunctionRest(start, modifiers.Flags, null, constructorName);
            RequireBody(constructor);
            members.Methods.Add(constructor);
            return;
        }

        if (!SyntaxFacts.IsTypeStart(Current.Kind))
        {
            Error(Current.Span, At(TokenKind.ReservedKeyword)
                ? $"'{Current.Text}' is not supported yet"
                : "expected a field, method, or constructor");
            SkipDeclaration();
            return;
        }

        var type = ParseType();
        var name = Expect(TokenKind.Identifier, "a member name");
        if (At(TokenKind.OpenParen) || At(TokenKind.Less))
        {
            CheckMethodModifiers(modifiers);
            var method = ParseFunctionRest(start, modifiers.Flags, type, name);
            if (kind == TypeKind.Interface)
            {
                if (method.Body is not null)
                    Error(method.NameLocation.Span, "interface methods cannot have a body");
            }
            else if ((modifiers.Flags & Modifiers.Abstract) != 0)
            {
                if (method.Body is not null)
                    Error(method.NameLocation.Span, "abstract methods cannot have a body");
            }
            else
            {
                RequireBody(method);
            }
            members.Methods.Add(method);
            return;
        }

        if (kind == TypeKind.Interface)
            Error(name.Span, "interfaces cannot have fields");
        Disallow(modifiers, Modifiers.Static, _ => "static fields are not supported yet");
        Disallow(modifiers, Modifiers.ReadOnly, _ => "'readonly' is only valid on methods");
        Disallow(modifiers, InheritanceModifiers, token => $"'{token.Text}' is not valid on a field");
        int errors = diagnostics.Count;
        while (true)
        {
            var initializer = Accept(TokenKind.Assign) ? ParseExpression() : null;
            members.Fields.Add(new FieldDecl(LocationFrom(start), modifiers.Flags, type, name.Text, Loc(name.Span), initializer));
            if (!Accept(TokenKind.Comma))
                break;
            name = Expect(TokenKind.Identifier, "a field name");
        }
        FinishStatement(errors);
    }

    void CheckMethodModifiers(ModifierList modifiers)
    {
        var flags = modifiers.Flags;
        Disallow(modifiers, Modifiers.Weak, _ => "'weak' is only valid on fields");
        if ((flags & Modifiers.Static) != 0)
        {
            Disallow(modifiers, Modifiers.ReadOnly | InheritanceModifiers, token => $"a static method cannot be '{token.Text}'");
            return;
        }
        if ((flags & Modifiers.Private) != 0)
            Disallow(modifiers, InheritanceModifiers, token => $"a private method cannot be '{token.Text}'");

        bool seenDispatch = false;
        foreach (var (flag, token) in modifiers.Tokens)
        {
            if (flag is not (Modifiers.Virtual or Modifiers.Override or Modifiers.Abstract))
                continue;
            if (seenDispatch)
                Error(token.Span, "only one of 'virtual', 'override', and 'abstract' is allowed");
            seenDispatch = true;
        }
        if ((flags & Modifiers.Final) != 0 && (flags & Modifiers.Override) == 0)
            Disallow(modifiers, Modifiers.Final, _ => "'final' can only be used on an 'override' method");
    }

    void ParseDestructor(int start, TypeKind kind, string typeName, ModifierList modifiers, Members members)
    {
        Disallow(modifiers, ~Modifiers.None, token => $"'{token.Text}' is not valid on a destructor");
        Advance();
        var name = Advance();
        if (kind != TypeKind.Class)
            Error(name.Span, kind == TypeKind.Struct ? "structs cannot have destructors" : "interfaces cannot have destructors");
        else if (name.Text != typeName)
            Error(name.Span, $"a destructor must be named after its class, as in '~{typeName}()'");
        else if (members.Destructor is not null)
            Error(name.Span, "a class can have only one destructor");
        Advance();
        if (!At(TokenKind.CloseParen))
        {
            Error(Current.Span, "destructors cannot have parameters");
            while (!At(TokenKind.CloseParen) && !At(TokenKind.OpenBrace) && !At(TokenKind.EndOfFile))
                Advance();
        }
        Expect(TokenKind.CloseParen, "')'");
        var body = ParseBlock();
        if (kind == TypeKind.Class && name.Text == typeName && members.Destructor is null)
            members.Destructor = new DestructorDecl(LocationFrom(start), Loc(name.Span), body);
    }

    FunctionDecl ParseFunctionRest(int start, Modifiers modifiers, TypeSyntax? returnType, Token name)
    {
        var typeParameters = ParseTypeParameters();
        if (returnType is null && typeParameters.Count > 0)
            Error(typeParameters[0].Location.Span, "constructors cannot have type parameters");
        Expect(TokenKind.OpenParen, "'('");
        var parameters = new List<ParameterSyntax>();
        if (!At(TokenKind.CloseParen))
        {
            do
            {
                int parameterStart = position;
                var refKind = Accept(TokenKind.RefKeyword) ? RefKind.Ref
                    : Accept(TokenKind.InKeyword) ? RefKind.In
                    : RefKind.None;
                var type = ParseType();
                var parameterName = Expect(TokenKind.Identifier, "a parameter name");
                parameters.Add(new ParameterSyntax(LocationFrom(parameterStart), refKind, type, parameterName.Text));
            } while (Accept(TokenKind.Comma));
        }
        Expect(TokenKind.CloseParen, "')'");
        var body = Accept(TokenKind.Semicolon) ? null : ParseBlock();
        return new FunctionDecl(LocationFrom(start), modifiers, returnType, name.Text, Loc(name.Span), typeParameters, parameters, body);
    }

    TypeSyntax ParseType(bool allowNullable = true)
    {
        if (SyntaxFacts.IsTypeStart(Current.Kind))
        {
            int start = position;
            var token = Advance();
            var arguments = At(TokenKind.Less) ? ParseTypeArguments() : [];
            bool nullable = allowNullable && Accept(TokenKind.Question);
            return new TypeSyntax(LocationFrom(start), token.Text, arguments, nullable);
        }
        Error(Current.Span, "expected a type");
        return new TypeSyntax(Loc(new TextSpan(Current.Span.Start, 0)), "?", []);
    }

    BlockStmt ParseBlock()
    {
        int start = position;
        Expect(TokenKind.OpenBrace, "'{'");
        var statements = new List<Stmt>();
        while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
        {
            int before = position;
            statements.Add(ParseStatement());
            if (position == before)
                Advance();
        }
        Expect(TokenKind.CloseBrace, "'}'");
        return new BlockStmt(LocationFrom(start), statements);
    }

    BlockStmt ParseBody(string keyword)
    {
        if (At(TokenKind.OpenBrace))
            return ParseBlock();
        Error(Current.Span, $"the body of '{keyword}' must be enclosed in braces");
        int start = position;
        var statement = ParseStatement();
        return new BlockStmt(LocationFrom(start), [statement]);
    }

    Stmt ParseStatement()
    {
        int start = position;
        switch (Current.Kind)
        {
            case TokenKind.OpenBrace:
                return ParseBlock();
            case TokenKind.IfKeyword:
                return ParseIf();
            case TokenKind.WhileKeyword:
            {
                Advance();
                Expect(TokenKind.OpenParen, "'('");
                var condition = ParseExpression();
                Expect(TokenKind.CloseParen, "')'");
                var body = ParseBody("while");
                return new WhileStmt(LocationFrom(start), condition, body);
            }
            case TokenKind.ForKeyword:
                return ParseFor();
            case TokenKind.ReturnKeyword:
            {
                Advance();
                int errors = diagnostics.Count;
                var value = At(TokenKind.Semicolon) ? null : ParseExpression();
                FinishStatement(errors);
                return new ReturnStmt(LocationFrom(start), value);
            }
            case TokenKind.BreakKeyword:
                Advance();
                Expect(TokenKind.Semicolon, "';'");
                return new BreakStmt(LocationFrom(start));
            case TokenKind.ContinueKeyword:
                Advance();
                Expect(TokenKind.Semicolon, "';'");
                return new ContinueStmt(LocationFrom(start));
            case TokenKind.ReservedKeyword or TokenKind.RefKeyword:
                Error(Current.Span, At(TokenKind.RefKeyword)
                    ? "'ref' locals are not supported yet"
                    : $"'{Current.Text}' is not supported yet");
                SkipStatement();
                return new BlockStmt(LocationFrom(start), []);
            default:
            {
                int errors = diagnostics.Count;
                var statement = ParseSimpleStatement(allowDeclaration: true);
                FinishStatement(errors);
                return statement;
            }
        }
    }

    void FinishStatement(int errorsBefore)
    {
        if (diagnostics.Count > errorsBefore)
            SkipStatement();
        else
            Expect(TokenKind.Semicolon, "';'");
    }

    void SkipStatement()
    {
        while (!At(TokenKind.EndOfFile) && !At(TokenKind.CloseBrace))
        {
            if (Accept(TokenKind.Semicolon))
                return;
            Advance();
        }
    }

    IfStmt ParseIf()
    {
        int start = position;
        Advance();
        Expect(TokenKind.OpenParen, "'('");
        var condition = ParseExpression();
        Expect(TokenKind.CloseParen, "')'");
        var then = ParseBody("if");
        Stmt? elseStatement = null;
        if (Accept(TokenKind.ElseKeyword))
            elseStatement = At(TokenKind.IfKeyword) ? ParseIf() : ParseBody("else");
        return new IfStmt(LocationFrom(start), condition, then, elseStatement);
    }

    ForStmt ParseFor()
    {
        int start = position;
        Advance();
        Expect(TokenKind.OpenParen, "'('");
        var initializer = At(TokenKind.Semicolon) ? null : ParseSimpleStatement(allowDeclaration: true);
        Expect(TokenKind.Semicolon, "';'");
        var condition = At(TokenKind.Semicolon) ? null : ParseExpression();
        Expect(TokenKind.Semicolon, "';'");
        var update = At(TokenKind.CloseParen) ? null : ParseSimpleStatement(allowDeclaration: false);
        Expect(TokenKind.CloseParen, "')'");
        var body = ParseBody("for");
        return new ForStmt(LocationFrom(start), initializer, condition, update, body);
    }

    Stmt ParseSimpleStatement(bool allowDeclaration)
    {
        int start = position;
        if (IsDeclarationStart())
        {
            if (!allowDeclaration)
                Error(Current.Span, "a variable cannot be declared here");
            return ParseVariableDeclaration();
        }

        if (At(TokenKind.PlusPlus) || At(TokenKind.MinusMinus))
        {
            bool increment = Advance().Kind == TokenKind.PlusPlus;
            var target = ParseUnary();
            return new IncDecStmt(LocationFrom(start), target, increment);
        }

        var expression = ParseExpression();
        if (SyntaxFacts.IsAssignmentOperator(Current.Kind))
        {
            var op = Advance();
            var value = ParseExpression();
            return new AssignStmt(LocationFrom(start), expression, op.Kind, Loc(op.Span), value);
        }
        if (At(TokenKind.PlusPlus) || At(TokenKind.MinusMinus))
        {
            bool increment = Advance().Kind == TokenKind.PlusPlus;
            return new IncDecStmt(LocationFrom(start), expression, increment);
        }
        return new ExprStmt(LocationFrom(start), expression);
    }

    bool IsDeclarationStart() => Current.Kind switch
    {
        TokenKind.VarKeyword => true,
        _ when SyntaxFacts.IsPrimitiveTypeKeyword(Current.Kind) => true,
        TokenKind.Identifier => IsDeclarationAfterTypeName(ScanTypeArguments(position + 1) is > 0 and var end ? end : position + 1),
        _ => false,
    };

    /// <summary>
    /// Whether a variable name follows the type name ending before <paramref name="index"/>. After a
    /// <c>?</c>, the name must be followed by '=' or ';', since <c>a ? b : c</c> is a conditional.
    /// </summary>
    bool IsDeclarationAfterTypeName(int index)
    {
        if (Peek(index - position).Kind == TokenKind.Identifier)
            return true;
        return Peek(index - position).Kind == TokenKind.Question
            && Peek(index - position + 1).Kind == TokenKind.Identifier
            && Peek(index - position + 2).Kind is TokenKind.Assign or TokenKind.Semicolon;
    }

    VarDeclStmt ParseVariableDeclaration()
    {
        int start = position;
        TypeSyntax? type = Accept(TokenKind.VarKeyword) ? null : ParseType();
        var name = Expect(TokenKind.Identifier, "a variable name");
        var initializer = Accept(TokenKind.Assign) ? ParseExpression() : null;
        return new VarDeclStmt(LocationFrom(start), type, name.Text, Loc(name.Span), initializer);
    }

    Expr ParseExpression() => ParseConditional();

    Expr ParseConditional()
    {
        int start = position;
        var condition = ParseCoalesce();
        if (!Accept(TokenKind.Question))
            return condition;
        var whenTrue = ParseExpression();
        Expect(TokenKind.Colon, "':'");
        var whenFalse = ParseConditional();
        return new ConditionalExpr(LocationFrom(start), condition, whenTrue, whenFalse);
    }

    // `??` binds looser than `||` and groups to the right, as in C#.
    Expr ParseCoalesce()
    {
        int start = position;
        var left = ParseBinary(0);
        if (!At(TokenKind.QuestionQuestion))
            return left;
        var op = Advance();
        var right = ParseCoalesce();
        return new CoalesceExpr(LocationFrom(start), left, Loc(op.Span), right);
    }

    Expr ParseBinary(int parentPrecedence)
    {
        int start = position;
        var left = ParseUnary();
        while (true)
        {
            if (At(TokenKind.IsKeyword))
            {
                // `is` has the precedence of the relational operators, like Java's instanceof.
                if (SyntaxFacts.RelationalPrecedence <= parentPrecedence)
                    return left;
                Advance();
                var type = ParseType(allowNullable: false);
                string? name = null;
                var nameLocation = type.Location;
                if (At(TokenKind.Identifier))
                {
                    var nameToken = Advance();
                    name = nameToken.Text;
                    nameLocation = Loc(nameToken.Span);
                }
                left = new IsExpr(LocationFrom(start), left, type, name, nameLocation);
                continue;
            }

            int precedence = SyntaxFacts.BinaryPrecedence(Current.Kind);
            if (precedence <= parentPrecedence)
                return left;
            var op = Advance();
            var right = ParseBinary(precedence);
            left = new BinaryExpr(LocationFrom(start), left, op.Kind, Loc(op.Span), right);
        }
    }

    Expr ParseUnary()
    {
        int start = position;
        if (At(TokenKind.Minus) || At(TokenKind.Bang) || At(TokenKind.Tilde))
        {
            var op = Advance();
            var operand = ParseUnary();
            return new UnaryExpr(LocationFrom(start), op.Kind, operand);
        }
        if (At(TokenKind.OpenParen) && SyntaxFacts.IsPrimitiveTypeKeyword(Peek(1).Kind) && Peek(2).Kind == TokenKind.CloseParen)
        {
            Advance();
            var type = ParseType();
            Advance();
            var operand = ParseUnary();
            return new CastExpr(LocationFrom(start), type, operand);
        }
        return ParsePostfix();
    }

    Expr ParsePostfix()
    {
        int start = position;
        var expression = ParsePrimary();
        while (true)
        {
            if (At(TokenKind.OpenParen))
            {
                var arguments = ParseArgumentList();
                expression = new CallExpr(LocationFrom(start), expression, arguments);
            }
            else if (At(TokenKind.Dot))
            {
                Advance();
                var name = Expect(TokenKind.Identifier, "a member name");
                var typeArguments = ParseExpressionTypeArguments();
                expression = new MemberAccessExpr(LocationFrom(start), expression, name.Text, Loc(name.Span), typeArguments);
            }
            else if (At(TokenKind.QuestionDot))
            {
                Advance();
                var name = Expect(TokenKind.Identifier, "a member name");
                expression = new ConditionalAccessExpr(LocationFrom(start), expression, name.Text, Loc(name.Span));
            }
            else if (At(TokenKind.Bang))
            {
                Advance();
                expression = new NonNullExpr(LocationFrom(start), expression);
            }
            // `Name {` can only be an initializer: conditions are parenthesized and braces are mandatory,
            // so a block never directly follows an identifier or call.
            else if (At(TokenKind.OpenBrace) && expression is NameExpr or CallExpr { Callee: NameExpr })
            {
                expression = ParseInitializer(start, expression);
            }
            else
            {
                return expression;
            }
        }
    }

    List<Expr> ParseArgumentList()
    {
        Expect(TokenKind.OpenParen, "'('");
        var arguments = new List<Expr>();
        if (!At(TokenKind.CloseParen))
        {
            do
                arguments.Add(ParseArgument());
            while (Accept(TokenKind.Comma));
        }
        Expect(TokenKind.CloseParen, "')'");
        return arguments;
    }

    Expr ParseArgument()
    {
        int start = position;
        if (!Accept(TokenKind.RefKeyword))
            return ParseExpression();
        var operand = ParseExpression();
        return new RefArgExpr(LocationFrom(start), operand);
    }

    InitializerExpr ParseInitializer(int start, Expr target)
    {
        Advance();
        var fields = new List<FieldInit>();
        while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
        {
            int fieldStart = position;
            var name = Expect(TokenKind.Identifier, "a field name");
            Expect(TokenKind.Assign, "'='");
            var value = ParseExpression();
            fields.Add(new FieldInit(LocationFrom(fieldStart), name.Text, value));
            if (!Accept(TokenKind.Comma) || position == fieldStart)
                break;
        }
        Expect(TokenKind.CloseBrace, "'}'");
        return new InitializerExpr(LocationFrom(start), target, fields);
    }

    Expr ParsePrimary()
    {
        var token = Current;
        var location = Loc(token.Span);
        switch (token.Kind)
        {
            case TokenKind.IntLiteral:
            {
                Advance();
                var value = (IntegerLiteralValue)token.Value!;
                return new IntLiteralExpr(location, value.Value, value.IsLong);
            }
            case TokenKind.FloatLiteral:
            {
                Advance();
                var value = (FloatLiteralValue)token.Value!;
                return new FloatLiteralExpr(location, value.Value, value.IsDouble);
            }
            case TokenKind.StringLiteral:
                Advance();
                return new StringLiteralExpr(location, (string)token.Value!);
            case TokenKind.TrueKeyword or TokenKind.FalseKeyword:
                Advance();
                return new BoolLiteralExpr(location, token.Kind == TokenKind.TrueKeyword);
            case TokenKind.Identifier:
            {
                int start = position;
                Advance();
                var typeArguments = ParseExpressionTypeArguments();
                return new NameExpr(LocationFrom(start), token.Text, typeArguments);
            }
            case TokenKind.ThisKeyword:
                Advance();
                return new ThisExpr(location);
            case TokenKind.SuperKeyword:
                Advance();
                return new SuperExpr(location);
            case TokenKind.NullKeyword:
                Advance();
                return new NullLiteralExpr(location);
            case TokenKind.NewKeyword:
            {
                int start = position;
                Advance();
                var type = ParseType(allowNullable: false);
                var arguments = ParseArgumentList();
                return new NewExpr(LocationFrom(start), type, arguments);
            }
            case TokenKind.OpenParen:
            {
                Advance();
                var inner = ParseExpression();
                Expect(TokenKind.CloseParen, "')'");
                return inner;
            }
            case TokenKind.ReservedKeyword:
                Error(token.Span, $"'{token.Text}' is not supported yet");
                Advance();
                return new ErrorExpr(location);
            default:
                Error(token.Span, "expected an expression");
                return new ErrorExpr(location);
        }
    }
}
