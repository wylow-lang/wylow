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
            if (At(TokenKind.StructKeyword))
            {
                Disallow(modifiers, ~(Modifiers.Public | Modifiers.Private), token => $"'{token.Text}' is not valid on a struct");
                declarations.Add(ParseStruct(start, modifiers.Flags));
                continue;
            }
            if (SyntaxFacts.IsTypeStart(Current.Kind))
            {
                Disallow(modifiers, ~(Modifiers.Public | Modifiers.Private), token => $"'{token.Text}' is not valid on a function");
                var returnType = ParseType();
                var name = Expect(TokenKind.Identifier, "a function name");
                declarations.Add(ParseFunctionRest(start, modifiers.Flags, returnType, name));
                continue;
            }
            Error(Current.Span, At(TokenKind.ReservedKeyword)
                ? $"'{Current.Text}' is not supported yet"
                : "expected a function or struct declaration");
            SkipDeclaration();
            if (position == start)
                Advance();
        }
        return new CompilationUnit(source, declarations);
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

    StructDecl ParseStruct(int start, Modifiers modifiers)
    {
        Advance();
        var name = Expect(TokenKind.Identifier, "a struct name");
        Expect(TokenKind.OpenBrace, "'{'");
        var fields = new List<FieldDecl>();
        var methods = new List<FunctionDecl>();
        while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
        {
            int before = position;
            ParseMember(name.Text, fields, methods);
            if (position == before)
                Advance();
        }
        Expect(TokenKind.CloseBrace, "'}'");
        return new StructDecl(LocationFrom(start), modifiers, name.Text, Loc(name.Span), fields, methods);
    }

    void ParseMember(string structName, List<FieldDecl> fields, List<FunctionDecl> methods)
    {
        int start = position;
        var modifiers = ParseModifiers();
        Disallow(modifiers, Modifiers.Protected, _ => "'protected' is not valid on struct members; structs cannot be inherited");

        if (At(TokenKind.Identifier) && Current.Text == structName && Peek(1).Kind == TokenKind.OpenParen)
        {
            Disallow(modifiers, Modifiers.Static | Modifiers.ReadOnly, token => $"'{token.Text}' is not valid on a constructor");
            var constructorName = Advance();
            methods.Add(ParseFunctionRest(start, modifiers.Flags, null, constructorName));
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
        if (At(TokenKind.OpenParen))
        {
            if ((modifiers.Flags & Modifiers.Static) != 0)
                Disallow(modifiers, Modifiers.ReadOnly, _ => "a static method cannot be 'readonly'");
            methods.Add(ParseFunctionRest(start, modifiers.Flags, type, name));
            return;
        }

        Disallow(modifiers, Modifiers.Static, _ => "static fields are not supported yet");
        Disallow(modifiers, Modifiers.ReadOnly, _ => "'readonly' is only valid on methods");
        int errors = diagnostics.Count;
        while (true)
        {
            var initializer = Accept(TokenKind.Assign) ? ParseExpression() : null;
            fields.Add(new FieldDecl(LocationFrom(start), modifiers.Flags, type, name.Text, Loc(name.Span), initializer));
            if (!Accept(TokenKind.Comma))
                break;
            name = Expect(TokenKind.Identifier, "a field name");
        }
        FinishStatement(errors);
    }

    FunctionDecl ParseFunctionRest(int start, Modifiers modifiers, TypeSyntax? returnType, Token name)
    {
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
        var body = ParseBlock();
        return new FunctionDecl(LocationFrom(start), modifiers, returnType, name.Text, Loc(name.Span), parameters, body);
    }

    TypeSyntax ParseType()
    {
        if (SyntaxFacts.IsTypeStart(Current.Kind))
        {
            var token = Advance();
            return new TypeSyntax(Loc(token.Span), token.Text);
        }
        Error(Current.Span, "expected a type");
        return new TypeSyntax(Loc(new TextSpan(Current.Span.Start, 0)), "?");
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
        TokenKind.Identifier => Peek(1).Kind == TokenKind.Identifier,
        _ => false,
    };

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
        var condition = ParseBinary(0);
        if (!Accept(TokenKind.Question))
            return condition;
        var whenTrue = ParseExpression();
        Expect(TokenKind.Colon, "':'");
        var whenFalse = ParseConditional();
        return new ConditionalExpr(LocationFrom(start), condition, whenTrue, whenFalse);
    }

    Expr ParseBinary(int parentPrecedence)
    {
        int start = position;
        var left = ParseUnary();
        while (true)
        {
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
                Advance();
                var arguments = new List<Expr>();
                if (!At(TokenKind.CloseParen))
                {
                    do
                        arguments.Add(ParseArgument());
                    while (Accept(TokenKind.Comma));
                }
                Expect(TokenKind.CloseParen, "')'");
                expression = new CallExpr(LocationFrom(start), expression, arguments);
            }
            else if (At(TokenKind.Dot))
            {
                Advance();
                var name = Expect(TokenKind.Identifier, "a member name");
                expression = new MemberAccessExpr(LocationFrom(start), expression, name.Text, Loc(name.Span));
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
                Advance();
                return new NameExpr(location, token.Text);
            case TokenKind.ThisKeyword:
                Advance();
                return new ThisExpr(location);
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
