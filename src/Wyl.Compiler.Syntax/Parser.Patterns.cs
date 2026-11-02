using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Syntax;

public sealed partial class Parser
{
    (Expr Value, List<SwitchCase> Cases) ParseSwitch(bool isExpression)
    {
        Advance();
        Expect(TokenKind.OpenParen, "'('");
        var value = ParseExpression();
        Expect(TokenKind.CloseParen, "')'");
        Expect(TokenKind.OpenBrace, "'{'");
        var cases = new List<SwitchCase>();
        while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
        {
            int start = position;
            var patterns = new List<PatternSyntax>();
            Location? defaultLocation = null;
            if (At(TokenKind.DefaultKeyword))
                defaultLocation = Loc(Advance().Span);
            else
            {
                if (!Accept(TokenKind.CaseKeyword))
                {
                    Report(Catalog.Expected(Loc(Current.Span), "'case' or 'default'"));
                    SkipStatement();
                    continue;
                }
                do
                {
                    // `case null, default`: the binder checks that only `null` comes before it.
                    if (patterns.Count > 0 && At(TokenKind.DefaultKeyword))
                    {
                        defaultLocation = Loc(Advance().Span);
                        break;
                    }
                    patterns.Add(ParsePattern());
                }
                while (Accept(TokenKind.Comma));
            }
            // A guard is a condition, never a lambda, so `when ok -> ...` ends at its arrow.
            var guard = Accept(TokenKind.WhenKeyword) ? ParseConditional() : null;

            if (At(TokenKind.Colon))
                Report(Catalog.ColonCase(Loc(Current.Span)));
            else
                Expect(TokenKind.Arrow, "'->'");
            Accept(TokenKind.Colon);

            if (isExpression)
            {
                if (At(TokenKind.OpenBrace))
                {
                    Report(Catalog.BlockInSwitchExpression(Loc(Current.Span)));
                    ParseBlock();
                    cases.Add(new SwitchCase(LocationFrom(start), patterns, null, new ErrorExpr(LocationFrom(start))) { Default = defaultLocation, Guard = guard });
                    continue;
                }
                int errors = diagnostics.Count;
                var caseValue = ParseExpression();
                FinishStatement(errors);
                cases.Add(new SwitchCase(LocationFrom(start), patterns, null, caseValue) { Default = defaultLocation, Guard = guard });
            }
            else
            {
                if (IsDeclarationStart())
                    Report(Catalog.DeclarationInCase(Loc(Current.Span)));
                cases.Add(new SwitchCase(LocationFrom(start), patterns, ParseStatement(), null) { Default = defaultLocation, Guard = guard });
            }
        }
        Expect(TokenKind.CloseBrace, "'}'");
        return (value, cases);
    }

    PatternSyntax ParsePattern()
    {
        int start = position;
        switch (Current.Kind)
        {
            case TokenKind.Underscore:
                Advance();
                return new DiscardPatternSyntax(LocationFrom(start));
            case TokenKind.VarKeyword:
            {
                Advance();
                var name = Expect(TokenKind.Identifier, "a variable name");
                return new VarPatternSyntax(LocationFrom(start), name.Text, Loc(name.Span));
            }
            case TokenKind.IntLiteral or TokenKind.FloatLiteral or TokenKind.CharLiteral or TokenKind.StringLiteral or TokenKind.TrueKeyword
                or TokenKind.FalseKeyword or TokenKind.NullKeyword:
            {
                var literal = ParsePrimary();
                return new ConstantPatternSyntax(literal.Location, literal);
            }
            case TokenKind.Minus when Peek(1).Kind is TokenKind.IntLiteral or TokenKind.FloatLiteral:
            {
                Advance();
                var literal = ParsePrimary();
                return new ConstantPatternSyntax(LocationFrom(start), new UnaryExpr(LocationFrom(start), TokenKind.Minus, literal));
            }
            case TokenKind.Identifier:
                return ParseNamePattern();
            case TokenKind.RefKeyword or TokenKind.InKeyword:
            {
                var refKind = Advance().Kind == TokenKind.RefKeyword ? RefKind.Ref : RefKind.In;
                var type = Accept(TokenKind.VarKeyword) ? null : ParseType();
                var name = Expect(TokenKind.Identifier, "a name for the place it refers to");
                return new ReferencePatternSyntax(LocationFrom(start), refKind, type, name.Text, Loc(name.Span));
            }
            case TokenKind.FnKeyword:
            {
                var type = ParseType(allowNullable: false);
                Report(Catalog.FunctionTypePattern(type.Location));
                if (At(TokenKind.Identifier))
                    Advance();
                return new DiscardPatternSyntax(LocationFrom(start));
            }
            case var kind when SyntaxFacts.IsPrimitiveTypeKeyword(kind):
            {
                var type = ParseType(allowNullable: false);
                return FinishTypePattern(start, type);
            }
            default:
                Report(Catalog.Expected(Loc(Current.Span), "a pattern"));
                return new DiscardPatternSyntax(Loc(new TextSpan(Current.Span.Start, 0)));
        }
    }

    /// <summary>
    /// A pattern starting with a name: a variant, possibly qualified by its enum and followed by
    /// field patterns, or a type test. A lone name is left for the binder to resolve.
    /// </summary>
    PatternSyntax ParseNamePattern()
    {
        int start = position;
        var modules = ParseModulePath();
        var first = modules.Count > 0 ? Expect(TokenKind.Identifier, "a type name") : Advance();
        var typeArguments = ScanTypeArguments(position) > 0 ? ParseTypeArguments() : [];
        var type = new TypeSyntax(LocationFrom(start), first.Text, typeArguments) { Modules = modules };

        if (Accept(TokenKind.Dot))
        {
            var variant = Expect(TokenKind.Identifier, "a variant name");
            var qualifiedFields = At(TokenKind.OpenParen) ? ParsePatternList() : null;
            return new VariantPatternSyntax(LocationFrom(start), type, variant.Text, Loc(variant.Span), qualifiedFields);
        }
        if (At(TokenKind.OpenParen) && typeArguments.Count == 0 && modules.Count == 0)
        {
            var fields = ParsePatternList();
            return new VariantPatternSyntax(LocationFrom(start), null, first.Text, Loc(first.Span), fields);
        }
        // A variant is reached through its enum, so a name in a module, or with type arguments, is a type.
        if (At(TokenKind.OpenParen))
        {
            var fields = ParsePatternList();
            return new StructPatternSyntax(LocationFrom(start), type, fields);
        }
        if (modules.Count > 0)
            return FinishTypePattern(start, type);
        if (typeArguments.Count > 0 || At(TokenKind.Identifier))
            return FinishTypePattern(start, type);
        return new VariantPatternSyntax(LocationFrom(start), null, first.Text, Loc(first.Span), null);
    }

    TypePatternSyntax FinishTypePattern(int start, TypeSyntax type)
    {
        if (!At(TokenKind.Identifier))
            return new TypePatternSyntax(LocationFrom(start), type, null, type.Location);
        var name = Advance();
        return new TypePatternSyntax(LocationFrom(start), type, name.Text, Loc(name.Span));
    }

    List<PatternSyntax> ParsePatternList()
    {
        Expect(TokenKind.OpenParen, "'('");
        var patterns = new List<PatternSyntax>();
        if (!At(TokenKind.CloseParen))
        {
            do
                patterns.Add(ParsePattern());
            while (Accept(TokenKind.Comma));
        }
        Expect(TokenKind.CloseParen, "')'");
        return patterns;
    }
}
