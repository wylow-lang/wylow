using System.Text;
using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Syntax;

public sealed partial class Parser
{
    // As in Java, a lambda is never an operand: its body extends as far as an expression can.
    Expr ParseExpression() => IsLambdaStart() ? ParseLambda() : ParseConditional();

    Expr ParseConditional()
    {
        int start = position;
        var condition = ParseCoalesce();
        if (!Accept(TokenKind.Question))
            return condition;
        var whenTrue = ParseExpression();
        Expect(TokenKind.Colon, "':'");
        var whenFalse = ParseExpression();
        return new ConditionalExpr(LocationFrom(start), condition, whenTrue, whenFalse);
    }

    /// <summary><c>x -></c>, or parentheses whose match is followed by <c>-></c>.</summary>
    bool IsLambdaStart()
    {
        if (At(TokenKind.Identifier))
            return Peek(1).Kind == TokenKind.Arrow;
        if (!At(TokenKind.OpenParen))
            return false;
        int depth = 0;
        for (int i = position; i < tokens.Count; i++)
        {
            switch (tokens[i].Kind)
            {
                case TokenKind.OpenParen:
                    depth++;
                    break;
                case TokenKind.CloseParen when --depth == 0:
                    return KindAt(i + 1) == TokenKind.Arrow;
                case TokenKind.Semicolon or TokenKind.OpenBrace or TokenKind.CloseBrace or TokenKind.EndOfFile:
                    return false;
            }
        }
        return false;
    }

    LambdaExpr ParseLambda()
    {
        int start = position;
        var parameters = new List<LambdaParameterSyntax>();
        if (At(TokenKind.Identifier))
        {
            var name = Advance();
            parameters.Add(new LambdaParameterSyntax(Loc(name.Span), RefKind.None, null, name.Text, Loc(name.Span)));
        }
        else
        {
            Advance();
            if (!At(TokenKind.CloseParen))
            {
                do
                    parameters.Add(ParseLambdaParameter());
                while (Accept(TokenKind.Comma));
            }
            Expect(TokenKind.CloseParen, "')'");
            if (parameters.FirstOrDefault(p => (p.Type is null) != (parameters[0].Type is null)) is { } mixed)
                Error(mixed.Location.Span, "a lambda's parameters must either all have types or all leave them out");
        }
        Expect(TokenKind.Arrow, "'->'");
        if (At(TokenKind.OpenBrace))
            return new LambdaExpr(LocationFrom(start), parameters, null, ParseBlock());
        var value = ParseExpression();
        return new LambdaExpr(LocationFrom(start), parameters, value, null);
    }

    LambdaParameterSyntax ParseLambdaParameter()
    {
        int start = position;
        var annotations = ParseAnnotations();
        var (isMutable, refKind) = ParseBindingMode("parameter");
        if (refKind == RefKind.None && At(TokenKind.Identifier) && Peek(1).Kind is TokenKind.Comma or TokenKind.CloseParen)
        {
            var untyped = Advance();
            return new LambdaParameterSyntax(LocationFrom(start), RefKind.None, null, untyped.Text, Loc(untyped.Span))
            {
                Annotations = annotations,
                IsMutable = isMutable,
            };
        }
        var type = ParseType();
        var name = Expect(TokenKind.Identifier, "a parameter name");
        return new LambdaParameterSyntax(LocationFrom(start), refKind, type, name.Text, Loc(name.Span))
        {
            Annotations = annotations,
            IsMutable = isMutable,
        };
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
                var pattern = ParsePattern();
                left = new IsExpr(LocationFrom(start), left, pattern);
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
        if (At(TokenKind.TryKeyword))
        {
            Advance();
            var operand = ParseUnary();
            return new TryExpr(LocationFrom(start), operand);
        }
        if (IsCastStart())
        {
            Advance();
            var type = ParseType(allowNullable: false);
            Expect(TokenKind.CloseParen, "')'");
            var operand = ParseUnary();
            return new CastExpr(LocationFrom(start), type, operand);
        }
        return ParsePostfix();
    }

    /// <summary>
    /// A cast to a primitive type, or as in Java, a parenthesized name followed by something that
    /// can only start an operand, so that <c>(a) - b</c> stays a subtraction.
    /// </summary>
    bool IsCastStart()
    {
        if (!At(TokenKind.OpenParen))
            return false;
        if (SyntaxFacts.IsPrimitiveTypeKeyword(Peek(1).Kind))
            return Peek(2).Kind == TokenKind.CloseParen;
        if (Peek(1).Kind != TokenKind.Identifier)
            return false;
        int end = ScanTypeArguments(position + 2) is > 0 and var afterArguments ? afterArguments : position + 2;
        if (Peek(end - position).Kind != TokenKind.CloseParen)
            return false;
        return Peek(end - position + 1).Kind is TokenKind.Identifier or TokenKind.IntLiteral or TokenKind.FloatLiteral
            or TokenKind.CharLiteral or TokenKind.StringLiteral or TokenKind.TrueKeyword or TokenKind.FalseKeyword or TokenKind.NullKeyword
            or TokenKind.ThisKeyword or TokenKind.SuperKeyword or TokenKind.NewKeyword or TokenKind.OpenParen
            or TokenKind.Tilde or TokenKind.SwitchKeyword;
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
            else if (At(TokenKind.OpenBracket))
            {
                var arguments = ParseDelimitedExpressions(TokenKind.OpenBracket, TokenKind.CloseBracket, "']'", ParseArgument);
                expression = new IndexExpr(LocationFrom(start), expression, arguments);
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

    List<Expr> ParseArgumentList() => ParseDelimitedExpressions(TokenKind.OpenParen, TokenKind.CloseParen, "')'", ParseArgument);

    List<Expr> ParseDelimitedExpressions(TokenKind open, TokenKind close, string closeDescription, Func<Expr> parse)
    {
        Expect(open, open == TokenKind.OpenParen ? "'('" : "'['");
        var expressions = new List<Expr>();
        if (!At(close))
        {
            do
                expressions.Add(parse());
            while (Accept(TokenKind.Comma));
        }
        Expect(close, closeDescription);
        return expressions;
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

    Expr ParseHole(IReadOnlyList<Token> holeTokens)
    {
        var parser = new Parser(source, diagnostics, [.. holeTokens]);
        var value = parser.ParseExpression();
        if (!parser.At(TokenKind.EndOfFile))
            parser.Error(parser.Current.Span, "expected '}' to end the interpolated value");
        return value;
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
                return new IntLiteralExpr(location, value.Value, value.Suffix);
            }
            case TokenKind.FloatLiteral:
            {
                Advance();
                var value = (FloatLiteralValue)token.Value!;
                return new FloatLiteralExpr(location, value.Value, value.IsDouble);
            }
            case TokenKind.CharLiteral:
                Advance();
                return new CharLiteralExpr(location, (Rune)token.Value!);
            case TokenKind.StringLiteral:
                Advance();
                if (token.Value is InterpolatedText interpolated)
                    return new InterpolatedStringExpr(location, interpolated.Texts, [.. interpolated.Holes.Select(h => h is null ? new ErrorExpr(location) : ParseHole(h))]);
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
                var type = ParseType(allowNullable: false, allowArrays: false);
                if (Accept(TokenKind.OpenBracket))
                {
                    var length = ParseExpression();
                    Expect(TokenKind.CloseBracket, "']'");
                    if (At(TokenKind.OpenBracket))
                        Error(Current.Span, "arrays of arrays cannot be created with one 'new' yet; create the outer array, then each inner one");
                    return new NewArrayExpr(LocationFrom(start), type, length);
                }
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
            case TokenKind.OpenBracket:
            {
                int start = position;
                var elements = ParseDelimitedExpressions(TokenKind.OpenBracket, TokenKind.CloseBracket, "']'", ParseExpression);
                return new ArrayLiteralExpr(LocationFrom(start), elements);
            }
            case TokenKind.SwitchKeyword:
            {
                int start = position;
                var (value, cases) = ParseSwitch(isExpression: true);
                return new SwitchExpr(LocationFrom(start), value, cases);
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
