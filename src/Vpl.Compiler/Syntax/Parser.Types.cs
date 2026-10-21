using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Syntax;

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
            else if (kind == TokenKind.OpenParen)
            {
                int end = ScanType(i);
                if (end < 0)
                    return -1;
                i = end - 1;
            }
            else if (ScanArraySuffix(i) is >= 0 and var next)
                i = next - 1;
            else if (kind is not (TokenKind.Comma or TokenKind.Question or TokenKind.Star) && !SyntaxFacts.IsTypeStart(kind))
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
    TypeSyntax ParseType(bool allowNullable = true, bool allowArrays = true)
    {
        if (!SyntaxFacts.IsTypeStart(Current.Kind))
        {
            Error(Current.Span, "expected a type");
            return new TypeSyntax(Loc(new TextSpan(Current.Span.Start, 0)), "?", []);
        }

        int start = position;
        TypeSyntax type;
        if (At(TokenKind.OpenParen))
        {
            var parameters = ParseFunctionTypeParameters();
            if (Accept(TokenKind.Arrow))
            {
                // The return type takes the suffixes: `(int) -> int?` returns an `int?`.
                var returnType = ParseType(allowNullable, allowArrays);
                return new TypeSyntax(LocationFrom(start), "", []) { Shape = TypeShape.Function, Element = returnType, Parameters = parameters };
            }
            // Otherwise the parentheses only group a function type for a suffix, as in `((int) -> int)?`.
            if (parameters is not [{ RefKind: RefKind.None, Type.Shape: TypeShape.Function } parenthesized])
            {
                Error(new TextSpan(Previous.Span.End, 0), "expected '->' after a function type's parameters");
                return new TypeSyntax(LocationFrom(start), "?", []);
            }
            type = parenthesized.Type with { Location = LocationFrom(start) };
        }
        else
        {
            var token = Advance();
            var arguments = At(TokenKind.Less) ? ParseTypeArguments() : [];
            type = new TypeSyntax(LocationFrom(start), token.Text, arguments);
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

    List<FunctionTypeParameter> ParseFunctionTypeParameters()
    {
        Advance();
        var parameters = new List<FunctionTypeParameter>();
        if (!At(TokenKind.CloseParen))
        {
            do
            {
                var refKind = ParseRefKind();
                parameters.Add(new FunctionTypeParameter(refKind, ParseType()));
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
        if (index >= tokens.Count || !SyntaxFacts.IsTypeStart(tokens[index].Kind))
            return -1;
        if (KindAt(index) == TokenKind.OpenParen)
        {
            int end = ScanFunctionTypeParameters(index, out bool groupsFunctionType);
            if (end < 0)
                return -1;
            if (KindAt(end) == TokenKind.Arrow)
                return ScanType(end + 1);
            if (!groupsFunctionType)
                return -1;
            index = end;
        }
        else
        {
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

    /// <summary>
    /// The index just past a function type's parenthesized parameters at <paramref name="index"/>, or -1.
    /// <paramref name="groupsFunctionType"/> is whether they could instead be parentheses around one
    /// function type, as in <c>((int) -> int)?</c>.
    /// </summary>
    int ScanFunctionTypeParameters(int index, out bool groupsFunctionType)
    {
        groupsFunctionType = false;
        index++;
        if (KindAt(index) == TokenKind.CloseParen)
            return index + 1;
        for (int count = 1; ; count++)
        {
            bool hasRefKind = KindAt(index) is TokenKind.RefKeyword or TokenKind.InKeyword;
            if (hasRefKind)
                index++;
            groupsFunctionType = count == 1 && !hasRefKind && KindAt(index) == TokenKind.OpenParen;
            index = ScanType(index);
            if (index < 0)
                return -1;
            if (KindAt(index) == TokenKind.Question)
                index++;
            if (KindAt(index) == TokenKind.CloseParen)
                return index + 1;
            if (KindAt(index) != TokenKind.Comma)
                return -1;
            groupsFunctionType = false;
            index++;
        }
    }
}
