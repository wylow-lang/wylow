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
            if (!Accept(TokenKind.DefaultKeyword))
            {
                if (!Accept(TokenKind.CaseKeyword))
                {
                    Error(Current.Span, "expected 'case' or 'default'");
                    SkipStatement();
                    continue;
                }
                do
                    patterns.Add(ParsePattern());
                while (Accept(TokenKind.Comma));
            }

            if (At(TokenKind.Colon))
                Error(Current.Span, "cases are written with '->', as in 'case X -> ...'; there is no fallthrough");
            else
                Expect(TokenKind.Arrow, "'->'");
            Accept(TokenKind.Colon);

            if (isExpression)
            {
                if (At(TokenKind.OpenBrace))
                {
                    Error(Current.Span, "the cases of a switch expression must be expressions; use a switch statement for blocks");
                    ParseBlock();
                    cases.Add(new SwitchCase(LocationFrom(start), patterns, null, new ErrorExpr(LocationFrom(start))));
                    continue;
                }
                int errors = diagnostics.Count;
                var caseValue = ParseExpression();
                FinishStatement(errors);
                cases.Add(new SwitchCase(LocationFrom(start), patterns, null, caseValue));
            }
            else
            {
                if (IsDeclarationStart())
                    Error(Current.Span, "a variable cannot be declared here; use a block, as in 'case X -> { ... }'");
                cases.Add(new SwitchCase(LocationFrom(start), patterns, ParseStatement(), null));
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
                Error(type.Location.Span, "a type pattern cannot name a function type; check a nullable function with a null check");
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
                Error(Current.Span, "expected a pattern");
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
