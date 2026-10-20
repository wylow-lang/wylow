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
            case TokenKind.SwitchKeyword:
            {
                var (value, cases) = ParseSwitch(isExpression: false);
                return new SwitchStmt(LocationFrom(start), value, cases);
            }
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
}
