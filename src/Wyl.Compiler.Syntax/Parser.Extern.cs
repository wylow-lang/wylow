using Wyl.Compiler.Diagnostics;

namespace Wyl.Compiler.Syntax;

public sealed partial class Parser
{
    /// <summary>Whether the parser is inside an extern block, where declarations take its forms (spec <c>interop.extern</c>).</summary>
    bool inExternBlock;

    /// <summary>
    /// <c>extern { ... }</c>, from the keyword on, with the annotations before it. Each member is
    /// added to <paramref name="declarations"/> with the block as its <see cref="Declaration.Block"/>.
    /// </summary>
    ExternBlock ParseExternBlock(List<AnnotationSyntax> annotations, List<Declaration> declarations)
    {
        var keyword = Advance();
        var block = new ExternBlock(Loc(keyword.Span), annotations);
        Expect(TokenKind.OpenBrace, "'{'");
        inExternBlock = true;
        while (!At(TokenKind.CloseBrace) && !At(TokenKind.EndOfFile))
        {
            int before = position;
            if (ParseExternMember() is { } member)
                declarations.Add(member with { Block = block });
            if (position == before)
                Advance();
        }
        inExternBlock = false;
        Expect(TokenKind.CloseBrace, "'}'");
        return block;
    }

    /// <summary>A constant, a struct, a union, or a function of an extern block, or null after an error.</summary>
    Declaration? ParseExternMember()
    {
        int start = position;
        var annotations = ParseAnnotations();
        var modifiers = ParseModifiers();
        DisallowShared(modifiers);
        if (At(TokenKind.RefKeyword) && Peek(1).Kind == TokenKind.StructKeyword)
        {
            Advance();
            modifiers = modifiers with { Flags = modifiers.Flags | Modifiers.Ref };
        }
        // `union` is a word with this meaning only here (spec interop.extern).
        bool isUnion = At(TokenKind.Identifier) && Current.Text == "union" && Peek(1).Kind == TokenKind.Identifier;
        if (At(TokenKind.StructKeyword) || isUnion)
        {
            // The binder reports `ref` and `unique`, since the standard library may write `unique`.
            Disallow(modifiers, ~(Modifiers.Ref | Modifiers.Unique), token =>
                Catalog.ModifierOnType(Loc(token.Span), token.Text, isUnion ? "a union" : "a struct"));
            return ParseTypeDecl(start, modifiers, TypeKind.Struct, isUnion) with { Annotations = annotations };
        }
        if (TypeKeywordKind(Current.Kind) is not null || !(StartsType(position) || At(TokenKind.OpenParen)))
        {
            Report(At(TokenKind.ReservedKeyword)
                ? Catalog.ReservedKeyword(Loc(Current.Span), Current.Text)
                : Catalog.Expected(Loc(Current.Span), "a constant, struct, union, or function in an extern block"));
            SkipDeclaration();
            return null;
        }

        bool isStatic = (modifiers.Flags & Modifiers.Static) != 0;
        var type = ParseType();
        var name = Expect(TokenKind.Identifier, isStatic ? "a constant name" : "a function name");
        if (isStatic && !At(TokenKind.OpenParen) && !At(TokenKind.Less))
        {
            Disallow(modifiers, ~Modifiers.Static, token => Catalog.ModifierOnExternConstant(Loc(token.Span), token.Text));
            var value = Accept(TokenKind.Assign) ? ParseExpression() : null;
            Expect(TokenKind.Semicolon, "';'");
            return new ExternConstantDecl(LocationFrom(start), modifiers.Flags, type, name.Text, Loc(name.Span), value)
            {
                Annotations = annotations,
                Visibility = modifiers.Visibility,
            };
        }
        if (name.Text != "" && (At(TokenKind.Assign) || At(TokenKind.Semicolon)))
        {
            SkipTopLevelVariable(name);
            return null;
        }
        Disallow(modifiers, ~Modifiers.Unsafe, token => Catalog.ModifierOnFunction(Loc(token.Span), token.Text));
        var function = ParseFunctionRest(start, modifiers, type, name);
        if (function.Body is not null)
            Report(Catalog.ExternBody(function.NameLocation, function.Name));
        return function with { Annotations = annotations };
    }
}
