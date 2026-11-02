using System.Text;
using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Syntax;

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
        var (isMutable, refKind, mayBeNull) = ParseBindingMode("parameter");
        var escaping = ParseEscaping();
        if (refKind == RefKind.None && At(TokenKind.Identifier) && Peek(1).Kind is TokenKind.Comma or TokenKind.CloseParen)
        {
            var untyped = Advance();
            return new LambdaParameterSyntax(LocationFrom(start), RefKind.None, null, untyped.Text, Loc(untyped.Span))
            {
                Annotations = annotations,
                IsMutable = isMutable,
                Escaping = escaping,
            };
        }
        var type = ParseType();
        if (mayBeNull)
            type = NullableReferenceType(start, refKind, type);
        var name = Expect(TokenKind.Identifier, "a parameter name");
        return new LambdaParameterSyntax(LocationFrom(start), refKind, type, name.Text, Loc(name.Span))
        {
            Annotations = annotations,
            IsMutable = isMutable,
            Escaping = escaping,
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
        int name = position + 1;
        while (KindAt(name) == TokenKind.Identifier && KindAt(name + 1) == TokenKind.ColonColon)
            name += 2;
        if (KindAt(name) != TokenKind.Identifier)
            return false;
        int end = ScanTypeArguments(name + 1) is > 0 and var afterArguments ? afterArguments : name + 1;
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
            // `Name {` can only be meant as C#'s initializer: conditions are parenthesized and braces
            // are mandatory, so a block never directly follows an identifier or call.
            else if (At(TokenKind.OpenBrace) && expression is NameExpr or CallExpr { Callee: NameExpr })
            {
                expression = SkipInitializer(start, expression);
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
        if (At(TokenKind.Identifier) && Peek(1).Kind == TokenKind.Colon)
        {
            var name = Advance();
            Advance();
            var value = ParseArgumentValue();
            return new NamedArgExpr(LocationFrom(start), name.Text, Loc(name.Span), value);
        }
        return ParseArgumentValue();
    }

    Expr ParseArgumentValue()
    {
        int start = position;
        if (!Accept(TokenKind.RefKeyword))
            return ParseExpression();
        var operand = ParseExpression();
        return new RefArgExpr(LocationFrom(start), operand);
    }

    /// <summary>Reports a C#-style <c>T { field = value }</c> initializer, which a constructor's named arguments replace, and skips it.</summary>
    Expr SkipInitializer(int start, Expr target)
    {
        var name = target is CallExpr { Callee: NameExpr callee } ? callee.Name : ((NameExpr)target).Name;
        Error(Current.Span, $"fields are set by naming them in the constructor call, as in '{name}(field: value)'");
        int depth = 0;
        do
        {
            if (At(TokenKind.OpenBrace))
                depth++;
            else if (At(TokenKind.CloseBrace))
                depth--;
            Advance();
        } while (depth > 0 && !At(TokenKind.EndOfFile));
        return new ErrorExpr(LocationFrom(start));
    }

    InterpolatedValue ParseHole(Location location, HoleText? hole)
    {
        if (hole is null)
            return new InterpolatedValue(location, new ErrorExpr(location), null, null, location);
        var parser = new Parser(source, diagnostics, [.. hole.Tokens]);
        var value = parser.ParseExpression();
        // The alignment is parsed rather than lexed, since a ',' may also separate type arguments.
        int? alignment = parser.Accept(TokenKind.Comma) ? parser.ParseAlignment() : null;
        if (!parser.At(TokenKind.EndOfFile))
            parser.Error(parser.Current.Span, "expected '}' to end the interpolated value");
        return new InterpolatedValue(value.Location, value, alignment, hole.Format, Loc(hole.FormatSpan));
    }

    int? ParseAlignment()
    {
        int start = position;
        bool negative = Accept(TokenKind.Minus);
        if (!At(TokenKind.IntLiteral) || Current.Value is not IntegerLiteralValue { Suffix: IntegerSuffix.None } literal)
        {
            Error(Current.Span, "expected an integer alignment, as in '{value,8}'");
            return null;
        }
        Advance();
        if (literal.Value > int.MaxValue)
        {
            Error(LocationFrom(start).Span, "alignment is too large");
            return null;
        }
        return negative ? -(int)literal.Value : (int)literal.Value;
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
                    return new InterpolatedStringExpr(location, interpolated.Texts, [.. interpolated.Holes.Select(h => ParseHole(location, h))]);
                return new StringLiteralExpr(location, (string)token.Value!);
            case TokenKind.TrueKeyword or TokenKind.FalseKeyword:
                Advance();
                return new BoolLiteralExpr(location, token.Kind == TokenKind.TrueKeyword);
            case TokenKind.Identifier:
            {
                int start = position;
                var modules = ParseModulePath();
                int nameStart = position;
                var name = modules.Count > 0 ? Expect(TokenKind.Identifier, "a name") : Advance();
                var typeArguments = ParseExpressionTypeArguments();
                var nameExpr = new NameExpr(modules.Count > 0 ? LocationFrom(nameStart) : LocationFrom(start), name.Text, typeArguments);
                return modules.Count > 0 ? new PathExpr(LocationFrom(start), modules, nameExpr) : nameExpr;
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
