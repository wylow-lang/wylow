using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Syntax;

public sealed partial class Parser
{
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
            case TokenKind.UnsafeKeyword when Peek(1).Kind == TokenKind.OpenBrace:
                Advance();
                return new UnsafeStmt(LocationFrom(start), ParseBlock());
            case TokenKind.SwitchKeyword:
            {
                var (value, cases) = ParseSwitch(isExpression: false);
                return new SwitchStmt(LocationFrom(start), value, cases);
            }
            case TokenKind.ReturnKeyword:
            {
                Advance();
                int errors = diagnostics.Count;
                bool isRef = Accept(TokenKind.RefKeyword);
                var value = At(TokenKind.Semicolon) && !isRef ? null : ParseExpression();
                FinishStatement(errors);
                return new ReturnStmt(LocationFrom(start), value, isRef);
            }
            case TokenKind.BreakKeyword:
                Advance();
                Expect(TokenKind.Semicolon, "';'");
                return new BreakStmt(LocationFrom(start));
            case TokenKind.ContinueKeyword:
                Advance();
                Expect(TokenKind.Semicolon, "';'");
                return new ContinueStmt(LocationFrom(start));
            case TokenKind.RefKeyword or TokenKind.InKeyword:
            {
                int errors = diagnostics.Count;
                var declaration = ParseVariableDeclaration();
                FinishStatement(errors);
                return declaration;
            }
            case TokenKind.ReservedKeyword:
                Error(Current.Span, $"'{Current.Text}' is not supported yet");
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

    Stmt ParseFor()
    {
        int start = position;
        Advance();
        Expect(TokenKind.OpenParen, "'('");
        if (IsForEachHeader())
            return ParseForEach(start);
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

        if (At(TokenKind.Underscore))
        {
            Advance();
            Expect(TokenKind.Assign, "'=' after '_'");
            var value = ParseExpression();
            return new DiscardStmt(LocationFrom(start), value);
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
        TokenKind.Identifier => IsDeclarationAfterTypeName(ScanType(position)),
        TokenKind.OpenParen => ScanType(position) is > 0 and var end && IsDeclarationAfterTypeName(end),
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
        var refKind = ParseRefKind();
        TypeSyntax? type = Accept(TokenKind.VarKeyword) ? null : ParseType();
        var name = Expect(TokenKind.Identifier, "a variable name");
        var initializer = Accept(TokenKind.Assign) ? ParseExpression() : null;
        return new VarDeclStmt(LocationFrom(start), type, name.Text, Loc(name.Span), initializer, refKind);
    }

    RefKind ParseRefKind() =>
        Accept(TokenKind.RefKeyword) ? RefKind.Ref : Accept(TokenKind.InKeyword) ? RefKind.In : RefKind.None;

    /// <summary>Whether the header starts with a variable followed by ':' or ',', as in <c>for (ref Enemy e : enemies)</c>.</summary>
    bool IsForEachHeader()
    {
        int index = position;
        if (KindAt(index) is TokenKind.RefKeyword or TokenKind.InKeyword)
            return true;
        if (KindAt(index) == TokenKind.VarKeyword)
            index++;
        else if ((index = ScanType(index)) < 0)
            return false;
        else if (KindAt(index) == TokenKind.Question)
            index++;
        return KindAt(index) == TokenKind.Identifier && KindAt(index + 1) is TokenKind.Colon or TokenKind.Comma;
    }

    ForEachStmt ParseForEach(int start)
    {
        var element = ParseForEachVariable();
        ForEachVariable? index = null;
        if (Accept(TokenKind.Comma))
        {
            index = element;
            element = ParseForEachVariable();
        }
        Expect(TokenKind.Colon, "':'");
        var iterable = ParseExpression();
        Expr? end = null;
        bool inclusive = false;
        if (At(TokenKind.DotDot) || At(TokenKind.DotDotEqual))
        {
            inclusive = Advance().Kind == TokenKind.DotDotEqual;
            end = ParseExpression();
        }
        Expect(TokenKind.CloseParen, "')'");
        var body = ParseBody("for");
        return new ForEachStmt(LocationFrom(start), index, element, iterable, end, inclusive, body);
    }

    ForEachVariable ParseForEachVariable()
    {
        int start = position;
        var refKind = ParseRefKind();
        TypeSyntax? type = Accept(TokenKind.VarKeyword) ? null : ParseType();
        var name = Expect(TokenKind.Identifier, "a variable name");
        return new ForEachVariable(LocationFrom(start), refKind, type, name.Text, Loc(name.Span));
    }
}
