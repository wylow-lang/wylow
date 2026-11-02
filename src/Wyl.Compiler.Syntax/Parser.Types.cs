using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Syntax;

public sealed partial class Parser
{
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
            else if (kind is TokenKind.OpenParen or TokenKind.FnKeyword)
            {
                int end = ScanType(i);
                if (end < 0)
                    return -1;
                i = end - 1;
            }
            else if (ScanArraySuffix(i) is >= 0 and var next)
                i = next - 1;
            else if (kind is not (TokenKind.Comma or TokenKind.Question or TokenKind.Star or TokenKind.ColonColon) && !SyntaxFacts.IsTypeStart(kind))
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

    /// <summary>
    /// A type with its suffixes. Without <paramref name="allowArrays"/>, brackets are left for the
    /// caller, as in <c>new T[n]</c>.
    /// </summary>
    /// <summary>Whether a type starts at token <paramref name="index"/>, including <c>(fn(int) -> int)?</c>.</summary>
    bool StartsType(int index) =>
        SyntaxFacts.IsTypeStart(KindAt(index)) || KindAt(index) == TokenKind.OpenParen && KindAt(index + 1) == TokenKind.FnKeyword;

    TypeSyntax ParseType(bool allowNullable = true, bool allowArrays = true)
    {
        // Parameters and locals read `escaping` before their type; anywhere else a function is escaping already.
        if (At(TokenKind.EscapingKeyword))
        {
            Report(Catalog.EscapingElsewhere(Loc(Current.Span)));
            Advance();
        }
        int start = position;
        if (Accept(TokenKind.FnKeyword))
            return ParseFunctionTypeRest(start, allowNullable, allowArrays);

        TypeSyntax type;
        if (At(TokenKind.OpenParen) && StartsType(position))
        {
            // Parentheses only group a function type for a suffix, as in `(fn(int) -> int)?`.
            Advance();
            type = ParseType();
            Expect(TokenKind.CloseParen, "')'");
            type = type with { Location = LocationFrom(start) };
            if (!At(TokenKind.Question) && !At(TokenKind.Star) && ScanArraySuffix(position) < 0)
                Report(Catalog.RedundantFunctionTypeParentheses(type.Location));
        }
        else if (At(TokenKind.OpenParen))
        {
            // Read the old spelling `(int) -> int` as a function type, so its error doesn't cascade.
            Report(Catalog.FunctionTypeWithoutFn(Loc(Current.Span)));
            type = ParseFunctionTypeRest(start, allowNullable, allowArrays, reported: true);
            if (type.Shape == TypeShape.Function)
                return type;
        }
        else if (!SyntaxFacts.IsTypeStart(Current.Kind))
        {
            Report(Catalog.Expected(Loc(Current.Span), "a type"));
            return new TypeSyntax(Loc(new TextSpan(Current.Span.Start, 0)), "?", []);
        }
        else
        {
            var modules = ParseModulePath();
            var token = modules.Count > 0 ? Expect(TokenKind.Identifier, "a type name") : Advance();
            var arguments = At(TokenKind.Less) ? ParseTypeArguments() : [];
            type = new TypeSyntax(LocationFrom(start), token.Text, arguments) { Modules = modules };
        }
        while (true)
        {
            // `T?[]` is an array of nullable elements; a final `?` makes the whole type nullable.
            if (At(TokenKind.Question) && Peek(1).Kind is TokenKind.OpenBracket or TokenKind.Star)
            {
                Advance();
                type = type with { Location = LocationFrom(start), IsNullable = true };
            }
            if (At(TokenKind.Star))
            {
                Advance();
                type = new TypeSyntax(LocationFrom(start), "", []) { Shape = TypeShape.Pointer, Element = type };
                continue;
            }
            if (!allowArrays || ScanArraySuffix(position) < 0)
                break;

            // Like C, the first dimension is the outermost: `int[4][2]` is four `int[2]`s.
            var dimensions = new List<(TypeShape Shape, ulong Length)>();
            while (ScanArraySuffix(position) >= 0)
            {
                Advance();
                if (At(TokenKind.IntLiteral))
                    dimensions.Add((TypeShape.FixedArray, ((IntegerLiteralValue)Advance().Value!).Value));
                else
                    dimensions.Add((TypeShape.Array, 0));
                Advance();
            }
            var location = LocationFrom(start);
            for (int i = dimensions.Count - 1; i >= 0; i--)
                type = new TypeSyntax(location, "", []) { Shape = dimensions[i].Shape, Element = type, Length = dimensions[i].Length };
        }
        if (allowNullable && Accept(TokenKind.Question))
            type = type with { Location = LocationFrom(start), IsNullable = true };
        return type;
    }

    /// <summary>
    /// A function type's parameters and return type, after its <c>fn</c>; <paramref name="reported"/>
    /// when the type already has an error, so a missing '->' isn't another.
    /// </summary>
    TypeSyntax ParseFunctionTypeRest(int start, bool allowNullable, bool allowArrays, bool reported = false)
    {
        var parameters = ParseFunctionTypeParameters();
        if (!Accept(TokenKind.Arrow))
        {
            if (!reported)
                Report(Catalog.Expected(Loc(new TextSpan(Previous.Span.End, 0)), "'->' after a function type's parameters"));
            return new TypeSyntax(LocationFrom(start), "?", []);
        }
        // The return type takes the suffixes: `fn(int) -> int?` returns an `int?`.
        var returnType = ParseType(allowNullable, allowArrays);
        return new TypeSyntax(LocationFrom(start), "", []) { Shape = TypeShape.Function, Element = returnType, Parameters = parameters };
    }

    List<FunctionTypeParameter> ParseFunctionTypeParameters()
    {
        var parameters = new List<FunctionTypeParameter>();
        if (!Accept(TokenKind.OpenParen))
        {
            Report(Catalog.FunctionTypeWithoutParentheses(Loc(Current.Span)));
            return parameters;
        }
        if (!At(TokenKind.CloseParen))
        {
            do
            {
                var annotations = ParseAnnotations();
                int start = position;
                var (refKind, mayBeNull) = ParseRefKind();
                var escaping = ParseEscaping();
                var type = ParseType();
                parameters.Add(new FunctionTypeParameter(refKind, mayBeNull ? NullableReferenceType(start, refKind, type) : type) { Annotations = annotations, Escaping = escaping });
            } while (Accept(TokenKind.Comma));
        }
        Expect(TokenKind.CloseParen, "')'");
        return parameters;
    }

    /// <summary>The index just past <c>[]</c> or <c>[N]</c> at <paramref name="index"/>, or -1.</summary>
    int ScanArraySuffix(int index)
    {
        TokenKind At(int i) => tokens[Math.Min(i, tokens.Count - 1)].Kind;
        if (At(index) != TokenKind.OpenBracket)
            return -1;
        if (At(index + 1) == TokenKind.CloseBracket)
            return index + 2;
        return At(index + 1) == TokenKind.IntLiteral && At(index + 2) == TokenKind.CloseBracket ? index + 3 : -1;
    }

    /// <summary>The index just past a type starting at <paramref name="index"/>, not counting a final '?', or -1.</summary>
    int ScanType(int index)
    {
        if (index >= tokens.Count || !StartsType(index))
            return -1;
        if (KindAt(index) == TokenKind.FnKeyword)
        {
            int end = ScanFunctionTypeParameters(index + 1);
            return end >= 0 && KindAt(end) == TokenKind.Arrow ? ScanType(end + 1) : -1;
        }
        if (KindAt(index) == TokenKind.OpenParen)
        {
            int end = ScanType(index + 1);
            if (end >= 0 && KindAt(end) == TokenKind.Question)
                end++;
            if (end < 0 || KindAt(end) != TokenKind.CloseParen)
                return -1;
            index = end + 1;
        }
        else
        {
            while (KindAt(index) == TokenKind.Identifier && KindAt(index + 1) == TokenKind.ColonColon)
                index += 2;
            if (KindAt(index) is not TokenKind.Identifier && !SyntaxFacts.IsPrimitiveTypeKeyword(KindAt(index)))
                return -1;
            index++;
            if (ScanTypeArguments(index) is > 0 and var end)
                index = end;
        }
        while (true)
        {
            if (index + 1 < tokens.Count && tokens[index].Kind == TokenKind.Question && tokens[index + 1].Kind is TokenKind.OpenBracket or TokenKind.Star)
                index++;
            if (index < tokens.Count && tokens[index].Kind == TokenKind.Star)
                index++;
            else if (ScanArraySuffix(index) is >= 0 and var next)
                index = next;
            else
                return index;
        }
    }

    /// <summary>The index just past a function type's parenthesized parameters at <paramref name="index"/>, or -1.</summary>
    int ScanFunctionTypeParameters(int index)
    {
        if (KindAt(index) != TokenKind.OpenParen)
            return -1;
        index++;
        if (KindAt(index) == TokenKind.CloseParen)
            return index + 1;
        while (true)
        {
            while (KindAt(index) == TokenKind.At && KindAt(index + 1) == TokenKind.Identifier)
                index += 2;
            if (KindAt(index) is TokenKind.RefKeyword or TokenKind.InKeyword)
                index += KindAt(index + 1) == TokenKind.Question ? 2 : 1;
            index = ScanType(index);
            if (index < 0)
                return -1;
            if (KindAt(index) == TokenKind.Question)
                index++;
            if (KindAt(index) == TokenKind.CloseParen)
                return index + 1;
            if (KindAt(index) != TokenKind.Comma)
                return -1;
            index++;
        }
    }
}
