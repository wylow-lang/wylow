using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Wyl.Mutation;

/// <summary>
/// One place a mutant can change: a span of the source text, what to put there, and what a person
/// reads. A mutation is a text splice, so every site records the line the original sat on.
/// <paramref name="Context"/> is the statement the site is in, which names a site independently of
/// where it sits, so a recorded equivalence still finds it after unrelated lines move.
/// </summary>
sealed record Site(
    string Kind,
    string Description,
    int Position,
    int Length,
    string Replacement,
    int Line,
    int Column,
    string Original,
    string Context)
{
    public string Where(string file) => $"{file}:{Line}:{Column}";
}

/// <summary>
/// The mutation catalog: every place one operator, constant, pattern, negated condition, or simple
/// statement can change. Roslyn finds the nodes, so an operator inside a string, a comment, or a
/// generic argument is never a site. A replacement that no longer compiles is an invalid mutant,
/// which the run reports and leaves out of the score.
/// </summary>
static class SiteFinder
{
    public static IReadOnlyList<Site> Find(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview));
        var root = tree.GetRoot();
        List<Site> sites = [];

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case BinaryExpressionSyntax binary:
                    AddBinary(sites, tree, binary);
                    break;
                case PrefixUnaryExpressionSyntax prefix:
                    AddPrefix(sites, tree, prefix);
                    break;
                case LiteralExpressionSyntax literal:
                    AddLiteral(sites, tree, literal);
                    break;
                case IsPatternExpressionSyntax pattern:
                    AddPattern(sites, tree, pattern);
                    break;
                case ExpressionStatementSyntax statement:
                    AddStatementDeletion(sites, tree, statement, text);
                    break;
            }
        }

        foreach (var condition in Conditions(root))
            AddSite(sites, tree, condition.Span.Start, condition.Span.Length,
                "!(" + condition + ")", "condition", "negate condition", condition.ToString(), Context(condition));

        return [.. sites
            .OrderBy(site => site.Position)
            .ThenBy(site => site.Description, StringComparer.Ordinal)
            .DistinctBy(site => (site.Position, site.Length, site.Replacement))];
    }

    static void AddBinary(List<Site> sites, SyntaxTree tree, BinaryExpressionSyntax node)
    {
        var replacement = node.Kind() switch
        {
            SyntaxKind.EqualsExpression => "!=",
            SyntaxKind.NotEqualsExpression => "==",
            SyntaxKind.LessThanExpression => "<=",
            SyntaxKind.LessThanOrEqualExpression => "<",
            SyntaxKind.GreaterThanExpression => ">=",
            SyntaxKind.GreaterThanOrEqualExpression => ">",
            SyntaxKind.LogicalAndExpression => "||",
            SyntaxKind.LogicalOrExpression => "&&",
            SyntaxKind.AddExpression => "-",
            SyntaxKind.SubtractExpression => "+",
            SyntaxKind.MultiplyExpression => "/",
            SyntaxKind.DivideExpression => "*",
            SyntaxKind.AddAssignmentExpression => "-=",
            SyntaxKind.SubtractAssignmentExpression => "+=",
            // `x is T` is a binary `is` expression; only a pattern form is an IsPatternExpression.
            SyntaxKind.IsExpression => "is not",
            _ => null,
        };
        if (replacement is null)
            return;
        var token = node.OperatorToken;
        // `x is T` is a binary `is` expression; only a pattern form is an IsPatternExpression.
        if (node.Kind() == SyntaxKind.IsExpression)
        {
            AddSite(sites, tree, token.SpanStart, token.Span.Length, replacement, "pattern",
                "is T -> is not T", node.ToString(), Context(node));
            return;
        }
        // "a" - "b" is not an expression; an arithmetic mutant with a string operand is only noise.
        var arithmetic = node.Kind() is SyntaxKind.AddExpression or SyntaxKind.SubtractExpression
            or SyntaxKind.MultiplyExpression or SyntaxKind.DivideExpression;
        if (arithmetic && (IsStringLike(node.Left) || IsStringLike(node.Right)))
            return;
        AddSite(sites, tree, token.SpanStart, token.Span.Length, replacement, "operator",
            $"{token.Text} -> {replacement}", token.Text, Context(node));
    }

    static bool IsStringLike(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression }
        || expression is InterpolatedStringExpressionSyntax;

    static void AddPrefix(List<Site> sites, SyntaxTree tree, PrefixUnaryExpressionSyntax node)
    {
        var token = node.OperatorToken;
        switch (node.Kind())
        {
            case SyntaxKind.LogicalNotExpression:
                AddSite(sites, tree, token.SpanStart, token.Span.Length, "", "unary", "remove !", "!", Context(node));
                break;
            case SyntaxKind.UnaryMinusExpression:
                AddSite(sites, tree, token.SpanStart, token.Span.Length, "", "unary", "remove -", "-", Context(node));
                break;
        }
    }

    static void AddLiteral(List<Site> sites, SyntaxTree tree, LiteralExpressionSyntax node)
    {
        // An optional parameter's default is compiled into every caller, so a test built against the
        // old value can't see the change; mutating it tests the harness, not the compiler.
        if (node.Parent is EqualsValueClauseSyntax { Parent: ParameterSyntax })
            return;
        switch (node.Kind())
        {
            case SyntaxKind.TrueLiteralExpression:
                AddSite(sites, tree, node.SpanStart, node.Span.Length, "false", "literal", "true -> false", "true", Context(node));
                break;
            case SyntaxKind.FalseLiteralExpression:
                AddSite(sites, tree, node.SpanStart, node.Span.Length, "true", "literal", "false -> true", "false", Context(node));
                break;
            case SyntaxKind.NumericLiteralExpression:
                if (NumericReplacement(node.Token.Text) is { } replacement)
                    AddSite(sites, tree, node.SpanStart, node.Span.Length, replacement, "literal",
                        $"{node.Token.Text} -> {replacement}", node.Token.Text, Context(node));
                break;
        }
    }

    /// <summary>Zero and one swap, keeping an integer suffix; any other integer literal becomes zero.</summary>
    static string? NumericReplacement(string text)
    {
        int digits = 0;
        while (digits < text.Length && (char.IsDigit(text[digits]) || text[digits] == '_'))
            digits++;
        if (digits == 0)
            return null; // hexadecimal, binary, or floating point
        var suffix = text[digits..];
        if (!suffix.All(c => c is 'u' or 'U' or 'l' or 'L'))
            return null;
        var value = text[..digits].All(c => c == '0') ? "1" : "0";
        return value + suffix;
    }

    static void AddPattern(List<Site> sites, SyntaxTree tree, IsPatternExpressionSyntax node)
    {
        var pattern = node.Pattern;
        if (pattern is UnaryPatternSyntax unary && unary.OperatorToken.IsKind(SyntaxKind.NotKeyword)
            && unary.Pattern is ConstantPatternSyntax { Expression.RawKind: (int)SyntaxKind.NullLiteralExpression })
        {
            AddSite(sites, tree, unary.SpanStart, unary.Span.Length, "null", "pattern",
                "is not null -> is null", unary.ToString(), Context(node));
        }
        else if (pattern is ConstantPatternSyntax { Expression.RawKind: (int)SyntaxKind.NullLiteralExpression } constant)
        {
            AddSite(sites, tree, constant.SpanStart, constant.Span.Length, "not null", "pattern",
                "is null -> is not null", constant.ToString(), Context(node));
        }
        else if (pattern is TypePatternSyntax type)
        {
            AddSite(sites, tree, type.SpanStart, type.Span.Length, "not " + type, "pattern",
                "is T -> is not T", type.ToString(), Context(node));
        }
        else if (pattern is UnaryPatternSyntax { Pattern: TypePatternSyntax negated })
        {
            AddSite(sites, tree, pattern.SpanStart, pattern.Span.Length, negated.ToString(), "pattern",
                "is not T -> is T", pattern.ToString(), Context(node));
        }
    }

    /// <summary>
    /// A statement that is alone on its lines can go. One that is a compound statement's only body
    /// would hand the body to the next statement, so it stays.
    /// </summary>
    static void AddStatementDeletion(List<Site> sites, SyntaxTree tree, ExpressionStatementSyntax statement, string text)
    {
        if (statement.Parent is IfStatementSyntax or WhileStatementSyntax or ForStatementSyntax or DoStatementSyntax
            or UsingStatementSyntax or LockStatementSyntax or FixedStatementSyntax or ElseClauseSyntax or LabeledStatementSyntax)
            return;
        // Indentation is not behavior, so deleting it can never be caught: it is not a missing test.
        if (statement.Expression is PostfixUnaryExpressionSyntax { Operand: IdentifierNameSyntax { Identifier.Text: "indent" } })
            return;
        var span = statement.Span;
        var lines = tree.GetText().Lines;
        var start = statement.GetLocation().GetLineSpan();
        if (start.StartLinePosition.Line != start.EndLinePosition.Line)
            return;
        var line = lines[start.StartLinePosition.Line];
        if (text[line.Start..span.Start].Trim().Length != 0)
            return;
        var after = text[span.End..line.End].Trim();
        if (after.Length != 0 && !after.StartsWith("//", StringComparison.Ordinal))
            return;
        AddSite(sites, tree, span.Start, span.Length, "", "statement", "delete statement", statement.ToString(),
            Normalize(statement.ToString()));
    }

    /// <summary>
    /// The smallest unit around a site that names it: its switch arm, else its statement, else its
    /// member declaration, with whitespace flattened. A recorded equivalence names a site by this,
    /// so it survives unrelated lines moving; an arm is what tells two sites in the same switch apart.
    /// </summary>
    static string Context(SyntaxNode node)
    {
        var around = node.AncestorsAndSelf().FirstOrDefault(n => n is SwitchExpressionArmSyntax or SwitchSectionSyntax or StatementSyntax)
            ?? (SyntaxNode?)node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault()
            ?? node;
        var text = Normalize(around.ToString());
        return text.Length <= 300 ? text : text[..300];
    }

    static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static IEnumerable<ExpressionSyntax> Conditions(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            var condition = node switch
            {
                IfStatementSyntax statement => statement.Condition,
                WhileStatementSyntax statement => statement.Condition,
                DoStatementSyntax statement => statement.Condition,
                ForStatementSyntax statement => statement.Condition,
                ConditionalExpressionSyntax statement => statement.Condition,
                _ => null,
            };
            if (condition is not null)
                yield return condition;
        }
    }

    static void AddSite(List<Site> sites, SyntaxTree tree, int position, int length, string replacement,
        string kind, string description, string original, string context)
    {
        var point = tree.GetLineSpan(new TextSpan(position, length)).StartLinePosition;
        var shown = original.Replace("\r", " ").Replace("\n", " ").Trim();
        if (shown.Length > 80)
            shown = shown[..77] + "...";
        sites.Add(new Site(kind, description, position, length, replacement, point.Line + 1, point.Character + 1, shown, context));
    }
}
