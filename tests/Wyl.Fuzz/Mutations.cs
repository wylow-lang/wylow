using System.Text;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Fuzz;

/// <summary>One replacement of source text, half-open as <see cref="TextSpan"/> is.</summary>
sealed record Edit(int Start, int End, string Text);

sealed record Candidate(string Name, IReadOnlyList<Edit> Edits);

/// <summary>A program the fuzzer made, and the mutations that made it.</summary>
/// <param name="UnsafeCode">Whether a mutation edited <c>unsafe</c> code, where the program may do what the language calls undefined.</param>
sealed record Mutant(int Index, string File, string Description, string Text, bool UnsafeCode = false);

sealed record Mutation(
    string Name,
    SiteKind Kind,
    Func<Site, bool> Applies,
    Func<MutationContext, Site, Candidate?> Build);

sealed class MutationContext(string text, Random random)
{
    public string Text { get; } = text;
    public Random Random { get; } = random;

    public string Slice(TextSpan span) => Text[span.Start..span.End];

    /// <summary>A name no identifier in the program has, for a declaration a mutation adds.</summary>
    public string UniqueName(string prefix)
    {
        if (!Text.Contains(prefix, StringComparison.Ordinal))
            return prefix;
        for (int i = 0; ; i++)
        {
            var name = prefix + i;
            if (!Text.Contains(name, StringComparison.Ordinal))
                return name;
        }
    }

    /// <summary>Appends a declaration, which may follow any other since declarations have no order.</summary>
    public static Edit Append(string declaration, string text) =>
        new(text.Length, text.Length, "\n\n" + declaration + "\n");
}

/// <summary>
/// The mutation catalog. Each mutation crosses a feature boundary or a value edge, so the program it
/// makes is near-valid: the interesting ones are where the surrounding code still works.
/// </summary>
static class Mutations
{
    static readonly Dictionary<string, string[]> IntegerNeighbors = new(StringComparer.Ordinal)
    {
        ["sbyte"] = ["short", "int"],
        ["byte"] = ["ushort", "int"],
        ["short"] = ["int", "long", "byte"],
        ["ushort"] = ["uint", "ulong", "byte"],
        ["int"] = ["long", "short", "uint"],
        ["uint"] = ["ulong", "ushort", "int"],
        ["long"] = ["nint", "int"],
        ["ulong"] = ["nuint", "uint"],
        ["nint"] = ["long", "int"],
        ["nuint"] = ["ulong", "uint"],
        ["char"] = ["int"],
    };

    static readonly Dictionary<TokenKind, TokenKind> OperatorNeighbors = new()
    {
        [TokenKind.Plus] = TokenKind.Minus,
        [TokenKind.Minus] = TokenKind.Plus,
        [TokenKind.Star] = TokenKind.Slash,
        [TokenKind.Slash] = TokenKind.Star,
        [TokenKind.Percent] = TokenKind.Slash,
        [TokenKind.Less] = TokenKind.LessEqual,
        [TokenKind.LessEqual] = TokenKind.Less,
        [TokenKind.Greater] = TokenKind.GreaterEqual,
        [TokenKind.GreaterEqual] = TokenKind.Greater,
        [TokenKind.EqualEqual] = TokenKind.BangEqual,
        [TokenKind.BangEqual] = TokenKind.EqualEqual,
        [TokenKind.AmpersandAmpersand] = TokenKind.PipePipe,
        [TokenKind.PipePipe] = TokenKind.AmpersandAmpersand,
        [TokenKind.Ampersand] = TokenKind.Pipe,
        [TokenKind.Pipe] = TokenKind.Caret,
        [TokenKind.Caret] = TokenKind.Ampersand,
        [TokenKind.LessLess] = TokenKind.GreaterGreater,
        [TokenKind.GreaterGreater] = TokenKind.LessLess,
    };

    static readonly Dictionary<TokenKind, TokenKind> AssignmentNeighbors = new()
    {
        [TokenKind.PlusAssign] = TokenKind.MinusAssign,
        [TokenKind.MinusAssign] = TokenKind.PlusAssign,
        [TokenKind.StarAssign] = TokenKind.SlashAssign,
        [TokenKind.SlashAssign] = TokenKind.StarAssign,
        [TokenKind.PercentAssign] = TokenKind.SlashAssign,
        [TokenKind.AmpersandAssign] = TokenKind.PipeAssign,
        [TokenKind.PipeAssign] = TokenKind.CaretAssign,
        [TokenKind.CaretAssign] = TokenKind.AmpersandAssign,
        [TokenKind.LessLessAssign] = TokenKind.GreaterGreaterAssign,
        [TokenKind.GreaterGreaterAssign] = TokenKind.LessLessAssign,
    };

    public static readonly Mutation[] All =
    [
        new("type-nullable", SiteKind.Type, NullableApplies, NullableBuild),
        new("type-integer", SiteKind.Type, IntegerApplies, IntegerBuild),
        new("type-fixed-array-length", SiteKind.Type, FixedArrayApplies, FixedArrayBuild),
        new("type-fixed-array", SiteKind.Type, ArrayApplies, TypeArrayBuild),
        new("type-kind", SiteKind.TypeDecl, KindApplies, KindBuild),
        new("parameter-in", SiteKind.Parameter, ParameterInApplies, ParameterInBuild),
        new("local-mut", SiteKind.Local, LocalMutApplies, LocalMutBuild),
        new("local-ref-nullable", SiteKind.Local, LocalRefNullableApplies, LocalRefNullableBuild),
        new("local-ref", SiteKind.Local, LocalRefApplies, LocalRefBuild),
        new("local-null", SiteKind.Local, LocalNullApplies, LocalNullBuild),
        new("statement-if", SiteKind.Statement, _ => true, StatementIfBuild),
        new("statement-loop", SiteKind.Statement, _ => true, StatementLoopBuild),
        new("statement-array-loop", SiteKind.Statement, _ => true, StatementArrayLoopBuild),
        new("statement-lambda", SiteKind.Statement, _ => true, StatementLambdaBuild),
        new("expression-conditional", SiteKind.Expression, Wrappable, ConditionalBuild),
        new("expression-switch", SiteKind.Expression, Wrappable, SwitchBuild),
        new("expression-array", SiteKind.Expression, Wrappable, ExpressionArrayBuild),
        new("expression-eval", SiteKind.Expression, Wrappable, EvalBuild),
        new("expression-read", SiteKind.Expression, Wrappable, ReadBuild),
        new("integer-edge", SiteKind.IntegerLiteral, IntegerEdgeApplies, IntegerEdgeBuild),
        new("float-edge", SiteKind.FloatLiteral, FloatEdgeApplies, FloatEdgeBuild),
        new("char-edge", SiteKind.CharLiteral, CharEdgeApplies, CharEdgeBuild),
        new("string-edge", SiteKind.StringLiteral, StringEdgeApplies, StringEdgeBuild),
        new("bool-flip", SiteKind.BoolLiteral, _ => true, BoolFlipBuild),
        new("binary-operator", SiteKind.BinaryOperator, BinaryOperatorApplies, BinaryOperatorBuild),
        new("assign-operator", SiteKind.AssignOperator, AssignOperatorApplies, AssignOperatorBuild),
        new("index-last", SiteKind.IndexArgument, _ => true, IndexLastBuild),
        new("index-zero", SiteKind.IndexArgument, _ => true, (_, site) => Single("index-zero", site, "(0)")),
        new("array-length", SiteKind.ArrayLength, _ => true, ArrayLengthBuild),
        new("array-elements", SiteKind.ArrayLiteral, _ => true, ArrayElementsBuild),
    ];

    static bool Wrappable(Site site) => !site.Place && !site.InPattern;

    static Candidate? Single(string name, Site site, string text) =>
        new(name, [new Edit(site.Span.Start, site.Span.End, text)]);

    static Candidate? Insert(string name, Site site, int at, string text) =>
        new(name, [new Edit(at, at, text)]);

    static bool NullableApplies(Site site) =>
        site.Node is TypeSyntax { IsNullable: false, Shape: TypeShape.Named or TypeShape.Function };

    static Candidate? NullableBuild(MutationContext context, Site site)
    {
        var type = (TypeSyntax)site.Node;
        var text = context.Slice(site.Span);
        return Single("type-nullable", site, type.Shape == TypeShape.Function ? $"({text})?" : text + "?");
    }

    static bool IntegerApplies(Site site) =>
        site.Node is TypeSyntax { Shape: TypeShape.Named, IsNullable: false, TypeArguments.Count: 0 } type
        && IntegerNeighbors.ContainsKey(type.Name);

    static Candidate? IntegerBuild(MutationContext context, Site site)
    {
        var type = (TypeSyntax)site.Node;
        var neighbors = IntegerNeighbors[type.Name];
        return Single("type-integer", site, neighbors[context.Random.Next(neighbors.Length)]);
    }

    static bool FixedArrayApplies(Site site) => site.Node is TypeSyntax { Shape: TypeShape.FixedArray };

    static Candidate? FixedArrayBuild(MutationContext context, Site site)
    {
        var text = context.Slice(site.Span);
        int open = text.IndexOf('[');
        int close = open < 0 ? -1 : text.IndexOf(']', open);
        if (close < 0)
            return null;
        return Single("type-fixed-array-length", site, text[..(open + 1)] + context.Random.Next(2) + text[close..]);
    }

    static bool ArrayApplies(Site site) =>
        site.Node is TypeSyntax { Shape: TypeShape.Named, IsNullable: false } type && type.Name != "void";

    static Candidate? TypeArrayBuild(MutationContext context, Site site) =>
        Single("type-fixed-array", site, context.Slice(site.Span) + "[1]");

    static bool KindApplies(Site site) =>
        site.Node is TypeDecl { Kind: TypeKind.Class or TypeKind.Struct } declaration
        && (declaration.Modifiers & Modifiers.Ref) == 0
        && (declaration.Modifiers & (Modifiers.Abstract | Modifiers.Final)) == 0;

    static Candidate? KindBuild(MutationContext context, Site site)
    {
        var declaration = (TypeDecl)site.Node;
        var keyword = declaration.Kind == TypeKind.Class ? "class" : "struct";
        var replacement = declaration.Kind == TypeKind.Class ? "struct" : "class";
        var text = context.Slice(site.Span);
        int index = IndexOfWord(text, keyword);
        return index < 0
            ? null
            : Single("type-kind", site, text[..index] + replacement + text[(index + keyword.Length)..]);
    }

    static bool ParameterInApplies(Site site) =>
        site.Node is ParameterSyntax { RefKind: RefKind.None } parameter
        && !(parameter.Type.Shape == TypeShape.Named && parameter.Type.Name == "void");

    static Candidate? ParameterInBuild(MutationContext context, Site site) => Insert("parameter-in", site, site.Span.Start, "in ");

    static bool LocalMutApplies(Site site) =>
        site.Node is VarDeclStmt { IsMutable: false, RefKind: RefKind.None };

    static Candidate? LocalMutBuild(MutationContext context, Site site)
    {
        var local = (VarDeclStmt)site.Node;
        int at = TypeStart(context, local);
        return at < 0 ? null : Insert("local-mut", site, at, "mut ");
    }

    static bool LocalRefNullableApplies(Site site) =>
        site.Node is VarDeclStmt { RefKind: RefKind.Ref or RefKind.In } local
        && local.Type is not { Shape: TypeShape.NullableReference };

    static Candidate? LocalRefNullableBuild(MutationContext context, Site site)
    {
        var local = (VarDeclStmt)site.Node;
        var word = local.RefKind == RefKind.Ref ? "ref" : "in";
        var text = context.Slice(local.Location.Span);
        int index = IndexOfWord(text, word);
        if (index < 0)
            return null;
        int at = local.Location.Span.Start + index + word.Length;
        if (at < context.Text.Length && context.Text[at] == '?')
            return null;
        return Insert("local-ref-nullable", site, at, "?");
    }

    static bool LocalRefApplies(Site site) =>
        site.Node is VarDeclStmt { RefKind: RefKind.None, IsMutable: false, Initializer: { } initializer }
        && IsPlace(initializer);

    static Candidate? LocalRefBuild(MutationContext context, Site site)
    {
        var local = (VarDeclStmt)site.Node;
        int at = TypeStart(context, local);
        return at < 0 ? null : Insert("local-ref", site, at, "ref ");
    }

    static bool LocalNullApplies(Site site) =>
        site.Node is VarDeclStmt { Initializer: { } initializer } local
        && (local.Type is { IsNullable: true } || local.Type is { Shape: TypeShape.NullableReference })
        && !(initializer is NullLiteralExpr);

    static Candidate? LocalNullBuild(MutationContext context, Site site)
    {
        var span = ((VarDeclStmt)site.Node).Initializer!.Location.Span;
        return span.Length == 0 ? null : new Candidate("local-null", [new Edit(span.Start, span.End, "null")]);
    }

    static bool IsPlace(Expr expression) => expression switch
    {
        NameExpr or ThisExpr => true,
        MemberAccessExpr member => IsPlace(member.Target),
        IndexExpr index => IsPlace(index.Target),
        NonNullExpr nonNull => IsPlace(nonNull.Operand),
        _ => false,
    };

    /// <summary>Where the type or <c>var</c> of a declaration starts, or -1 if it can't be found.</summary>
    static int TypeStart(MutationContext context, VarDeclStmt local)
    {
        if (local.Type is { } type)
            return type.Location.Span.Start;
        var text = context.Slice(local.Location.Span);
        int index = IndexOfWord(text, "var");
        return index < 0 ? -1 : local.Location.Span.Start + index;
    }

    static Candidate? StatementIfBuild(MutationContext context, Site site) =>
        Single("statement-if", site, "{ if (true) {\n" + context.Slice(site.Span) + "\n} }");

    static Candidate? StatementLoopBuild(MutationContext context, Site site) =>
        Single("statement-loop", site, "{ for (int wylq : 0..1) {\n" + context.Slice(site.Span) + "\n} }");

    static Candidate? StatementArrayLoopBuild(MutationContext context, Site site) =>
        Single("statement-array-loop", site, "{ for (int wylq : [0]) {\n" + context.Slice(site.Span) + "\n} }");

    static Candidate? StatementLambdaBuild(MutationContext context, Site site)
    {
        var name = context.UniqueName("WylFuzzRun");
        var helper = "void " + name + "(() -> void f) {\n    f();\n}";
        return new Candidate("statement-lambda",
        [
            new Edit(site.Span.Start, site.Span.End, "{ " + name + "(() -> {\n" + context.Slice(site.Span) + "\n}); }"),
            MutationContext.Append(helper, context.Text),
        ]);
    }

    static Candidate? ConditionalBuild(MutationContext context, Site site)
    {
        var text = context.Slice(site.Span);
        return Single("expression-conditional", site, "(true ? (" + text + ") : (" + text + "))");
    }

    static Candidate? SwitchBuild(MutationContext context, Site site)
    {
        var text = context.Slice(site.Span);
        return Single("expression-switch", site, "(switch (" + text + ") { default -> " + text + "; })");
    }

    static Candidate? ExpressionArrayBuild(MutationContext context, Site site) =>
        Single("expression-array", site, "[" + context.Slice(site.Span) + "][0]");

    static Candidate? EvalBuild(MutationContext context, Site site)
    {
        var name = context.UniqueName("WylFuzzEval");
        var helper = "T " + name + "<T>(() -> T f) {\n    return f();\n}";
        return new Candidate("expression-eval",
        [
            new Edit(site.Span.Start, site.Span.End, name + "(() -> " + context.Slice(site.Span) + ")"),
            MutationContext.Append(helper, context.Text),
        ]);
    }

    static Candidate? ReadBuild(MutationContext context, Site site)
    {
        var name = context.UniqueName("WylFuzzRead");
        var helper = "T " + name + "<T>(in T value) {\n    return value;\n}";
        return new Candidate("expression-read",
        [
            new Edit(site.Span.Start, site.Span.End, name + "(" + context.Slice(site.Span) + ")"),
            MutationContext.Append(helper, context.Text),
        ]);
    }

    static bool IntegerEdgeApplies(Site site) => !site.InPattern;

    static Candidate? IntegerEdgeBuild(MutationContext context, Site site)
    {
        var literal = (IntLiteralExpr)site.Node;
        string[] edges = literal.Suffix switch
        {
            IntegerSuffix.Unsigned => ["(0)", "(1)", "(4294967295U)", "(4294967294U)"],
            IntegerSuffix.Long => ["(0L)", "(1L)", "(-1L)", "(9223372036854775807L)", "(-9223372036854775807L - 1)"],
            IntegerSuffix.UnsignedLong => ["(0UL)", "(1UL)", "(18446744073709551615UL)", "(9223372036854775807UL)"],
            _ => ["(0)", "(1)", "(-1)", "(2147483647)", "(-2147483647 - 1)"],
        };
        return Single("integer-edge", site, edges[context.Random.Next(edges.Length)]);
    }

    static bool FloatEdgeApplies(Site site) => !site.InPattern;

    static Candidate? FloatEdgeBuild(MutationContext context, Site site)
    {
        var literal = (FloatLiteralExpr)site.Node;
        var suffix = literal.IsDouble ? "d" : "";
        string[] edges = ["(0.0" + suffix + ")", "(-0.0" + suffix + ")", "(0.1" + suffix + ")", "(1e30" + suffix + ")"];
        return Single("float-edge", site, edges[context.Random.Next(edges.Length)]);
    }

    static bool CharEdgeApplies(Site site) => !site.InPattern;

    static Candidate? CharEdgeBuild(MutationContext context, Site site)
    {
        string[] edges = ["('\\0')", "('a')", "('A')", "('\\r')", "('é')"];
        return Single("char-edge", site, edges[context.Random.Next(edges.Length)]);
    }

    static bool StringEdgeApplies(Site site) => !site.InPattern;

    static Candidate? StringEdgeBuild(MutationContext context, Site site)
    {
        string[] edges = ["(\"\")", "(\"a\")", "(\"é\")", "(\"a\\nb\")", "(\"\\\\\")"];
        return Single("string-edge", site, edges[context.Random.Next(edges.Length)]);
    }

    static Candidate? BoolFlipBuild(MutationContext context, Site site) =>
        Single("bool-flip", site, ((BoolLiteralExpr)site.Node).Value ? "false" : "true");

    static bool BinaryOperatorApplies(Site site) => site.Node is BinaryExpr binary && OperatorNeighbors.ContainsKey(binary.Operator);

    static Candidate? BinaryOperatorBuild(MutationContext context, Site site) =>
        Single("binary-operator", site, SyntaxFacts.GetText(OperatorNeighbors[((BinaryExpr)site.Node).Operator]));

    static bool AssignOperatorApplies(Site site) =>
        site.Node is AssignStmt assign && AssignmentNeighbors.ContainsKey(assign.Operator);

    static Candidate? AssignOperatorBuild(MutationContext context, Site site) =>
        Single("assign-operator", site, SyntaxFacts.GetText(AssignmentNeighbors[((AssignStmt)site.Node).Operator]));

    static Candidate? IndexLastBuild(MutationContext context, Site site)
    {
        var index = (IndexExpr)site.Node;
        var target = context.Slice(index.Target.Location.Span);
        return Single("index-last", site, "(" + target + ".length - 1)");
    }

    static Candidate? ArrayLengthBuild(MutationContext context, Site site) =>
        Single("array-length", site, context.Random.Next(2) == 0 ? "(0)" : "(1)");

    static Candidate? ArrayElementsBuild(MutationContext context, Site site)
    {
        var array = (ArrayLiteralExpr)site.Node;
        var first = context.Slice(array.Elements[0].Location.Span);
        return Single("array-elements", site, "[" + first + "]");
    }

    /// <summary>The index of <paramref name="word"/> in <paramref name="text"/> as a whole identifier, or -1.</summary>
    public static int IndexOfWord(string text, string word)
    {
        for (int index = text.IndexOf(word, StringComparison.Ordinal); index >= 0; index = text.IndexOf(word, index + 1, StringComparison.Ordinal))
        {
            bool before = index == 0 || !IsIdentifierChar(text[index - 1]);
            bool after = index + word.Length == text.Length || !IsIdentifierChar(text[index + word.Length]);
            if (before && after)
                return index;
        }
        return -1;
    }

    static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Applies edits to <paramref name="text"/>; they must not overlap.</summary>
    public static string Apply(string text, IReadOnlyList<Edit> edits)
    {
        var ordered = edits.OrderBy(e => e.Start).ThenBy(e => e.End).ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Start < ordered[i - 1].End)
                throw new InvalidOperationException($"overlapping edits {ordered[i - 1]} and {ordered[i]}");
        }
        var builder = new StringBuilder(text);
        for (int i = ordered.Count - 1; i >= 0; i--)
        {
            var edit = ordered[i];
            if (edit.Start < 0 || edit.End > text.Length || edit.End < edit.Start)
                throw new InvalidOperationException($"edit {edit} is outside the text");
            builder.Remove(edit.Start, edit.End - edit.Start);
            builder.Insert(edit.Start, edit.Text);
        }
        return builder.ToString();
    }
}
