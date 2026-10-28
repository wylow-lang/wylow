using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Fuzz;

enum SiteKind
{
    Type,
    TypeDecl,
    Parameter,
    Local,
    Statement,
    Expression,
    IntegerLiteral,
    FloatLiteral,
    CharLiteral,
    StringLiteral,
    BoolLiteral,
    BinaryOperator,
    AssignOperator,
    IndexArgument,
    ArrayLength,
    ArrayLiteral,
}

/// <summary>
/// Where a mutation can start: <see cref="Span"/> is the text it edits (a type's text, an operator
/// token, a whole statement), and <see cref="Node"/> is the syntax it read it from. <see cref="Place"/>
/// marks an expression that is an assignment or reference target, which wrapping in a value would stop
/// being one; <see cref="InPattern"/> marks a literal a pattern must see as a single token.
/// </summary>
sealed record Site(SiteKind Kind, TextSpan Span, Node Node, bool InPattern = false, bool Place = false);

/// <summary>Every place a mutation may start from, and every span a reducer may delete.</summary>
sealed class AstIndex
{
    readonly List<Site> sites;
    readonly List<Site>?[] byKind = new List<Site>?[Enum.GetValues<SiteKind>().Length];

    AstIndex(List<Site> sites, List<TextSpan> deletable)
    {
        this.sites = sites;
        Deletable = deletable;
    }

    public IReadOnlyList<TextSpan> Deletable { get; }

    public IReadOnlyList<Site> Of(SiteKind kind) =>
        byKind[(int)kind] ??= [.. sites.Where(s => s.Kind == kind)];

    public static AstIndex Of(CompilationUnit unit)
    {
        var walker = new Walker();
        foreach (var declaration in unit.Declarations)
            walker.Declaration(declaration);
        return new AstIndex(walker.Sites, walker.Deletable);
    }

    sealed class Walker
    {
        public List<Site> Sites { get; } = [];
        public List<TextSpan> Deletable { get; } = [];

        public void Declaration(Declaration declaration)
        {
            Deletable.Add(declaration.Location.Span);
            switch (declaration)
            {
                case TypeDecl type:
                    Sites.Add(new Site(SiteKind.TypeDecl, type.Location.Span, type));
                    foreach (var parameter in type.TypeParameters)
                        foreach (var bound in parameter.Bounds)
                            Type(bound);
                    if (type.BaseType is { } baseType)
                        Type(baseType);
                    foreach (var @interface in type.Interfaces)
                        Type(@interface);
                    foreach (var field in type.Fields)
                        Declaration(field);
                    foreach (var method in type.Methods)
                        Declaration(method);
                    if (type.Destructor is { } destructor)
                        Statement(destructor.Body, inList: true);
                    foreach (var variant in type.Variants)
                    {
                        foreach (var field in variant.Fields ?? [])
                            Parameter(field);
                        if (variant.Value is { } value)
                            Expression(value);
                    }
                    break;
                case FieldDecl field:
                    Type(field.Type);
                    if (field.Initializer is { } initializer)
                        Expression(initializer);
                    break;
                case FunctionDecl function:
                    if (function.ReturnType is { } returnType)
                        Type(returnType);
                    foreach (var parameter in function.Parameters)
                        Parameter(parameter);
                    if (function.Body is { } body)
                        Statement(body, inList: true);
                    break;
            }
        }

        void Parameter(ParameterSyntax parameter)
        {
            Sites.Add(new Site(SiteKind.Parameter, parameter.Type.Location.Span, parameter));
            Type(parameter.Type);
            if (parameter.Default is { } value)
                Expression(value);
        }

        void Type(TypeSyntax type)
        {
            if (type.Location.Span.Length > 0)
                Sites.Add(new Site(SiteKind.Type, type.Location.Span, type));
            foreach (var argument in type.TypeArguments)
                Type(argument);
            if (type.Element is { } element)
                Type(element);
            foreach (var parameter in type.Parameters)
                Type(parameter.Type);
        }

        void Statement(Stmt statement, bool inList)
        {
            Deletable.Add(statement.Location.Span);
            // A block is a valid replacement anywhere a statement is expected, so it can always be
            // wrapped; other statements only where the grammar reads a statement rather than a header.
            if (inList || statement is BlockStmt)
                Sites.Add(new Site(SiteKind.Statement, statement.Location.Span, statement));

            switch (statement)
            {
                case BlockStmt block:
                    foreach (var child in block.Statements)
                        Statement(child, inList: true);
                    break;
                case UnsafeStmt @unsafe:
                    Statement(@unsafe.Body, inList: true);
                    break;
                case VarDeclStmt local:
                    if (local.Type is { } type)
                    {
                        Type(type);
                        Sites.Add(new Site(SiteKind.Local, type.Location.Span, local));
                    }
                    else
                    {
                        Sites.Add(new Site(SiteKind.Local, local.Location.Span, local));
                    }
                    if (local.Initializer is { } initializer)
                        Expression(initializer, place: local.RefKind != RefKind.None);
                    break;
                case IfStmt @if:
                    Expression(@if.Condition);
                    Statement(@if.Then, inList: false);
                    if (@if.Else is { } @else)
                        Statement(@else, inList: true);
                    break;
                case WhileStmt @while:
                    Expression(@while.Condition);
                    Statement(@while.Body, inList: false);
                    break;
                case ForStmt @for:
                    if (@for.Initializer is { } loopInitializer)
                        Statement(loopInitializer, inList: false);
                    if (@for.Condition is { } condition)
                        Expression(condition);
                    if (@for.Update is { } update)
                        Statement(update, inList: false);
                    Statement(@for.Body, inList: false);
                    break;
                case ForEachStmt @each:
                    if (@each.Index is { } index)
                        ForEachVariable(index);
                    ForEachVariable(@each.Element);
                    Expression(@each.Iterable);
                    if (@each.RangeEnd is { } end)
                        Expression(end);
                    Statement(@each.Body, inList: false);
                    break;
                case ReturnStmt @return:
                    if (@return.Value is { } value)
                        Expression(value);
                    break;
                case ExprStmt expression:
                    Expression(expression.Expression);
                    break;
                case AssignStmt assign:
                    Expression(assign.Target, place: true);
                    Sites.Add(new Site(SiteKind.AssignOperator, assign.OperatorLocation.Span, assign));
                    Expression(assign.Value);
                    break;
                case IncDecStmt increment:
                    Expression(increment.Target, place: true);
                    break;
                case DiscardStmt discard:
                    Expression(discard.Value);
                    break;
                case SwitchStmt @switch:
                    Switch(@switch.Value, @switch.Cases);
                    break;
            }
        }

        void ForEachVariable(ForEachVariable variable)
        {
            if (variable.Type is { } type)
                Type(type);
        }

        void Switch(Expr value, IReadOnlyList<SwitchCase> cases)
        {
            Expression(value);
            foreach (var @case in cases)
            {
                Deletable.Add(@case.Location.Span);
                foreach (var pattern in @case.Patterns)
                    Pattern(pattern);
                if (@case.Body is { } body)
                    Statement(body, inList: true);
                if (@case.Value is { } caseValue)
                    Expression(caseValue);
            }
        }

        void Pattern(PatternSyntax pattern)
        {
            switch (pattern)
            {
                case TypePatternSyntax typePattern:
                    Type(typePattern.Type);
                    break;
                case VariantPatternSyntax variant:
                    if (variant.Enum is { } @enum)
                        Type(@enum);
                    foreach (var field in variant.Fields ?? [])
                        Pattern(field);
                    break;
                case ConstantPatternSyntax constant:
                    Expression(constant.Value, inPattern: true);
                    break;
                case ReferencePatternSyntax reference:
                    if (reference.Type is { } type)
                        Type(type);
                    break;
            }
        }

        void Expression(Expr expression, bool inPattern = false, bool place = false)
        {
            // A lambda, a named argument, and a `ref` argument are only valid as themselves.
            if (expression is not (LambdaExpr or NamedArgExpr or RefArgExpr or ErrorExpr) && expression.Location.Span.Length > 0)
                Sites.Add(new Site(SiteKind.Expression, expression.Location.Span, expression, inPattern, place));

            switch (expression)
            {
                case IntLiteralExpr literal:
                    Literal(SiteKind.IntegerLiteral, literal, inPattern);
                    break;
                case FloatLiteralExpr literal:
                    Literal(SiteKind.FloatLiteral, literal, inPattern);
                    break;
                case CharLiteralExpr literal:
                    Literal(SiteKind.CharLiteral, literal, inPattern);
                    break;
                case StringLiteralExpr literal:
                    Literal(SiteKind.StringLiteral, literal, inPattern);
                    break;
                case BoolLiteralExpr literal:
                    Literal(SiteKind.BoolLiteral, literal, inPattern);
                    break;
                case InterpolatedStringExpr interpolated:
                    foreach (var value in interpolated.Values)
                        Expression(value.Value);
                    break;
                case MemberAccessExpr member:
                    Expression(member.Target);
                    break;
                case ConditionalAccessExpr conditional:
                    Expression(conditional.Target);
                    break;
                case UnaryExpr unary:
                    Expression(unary.Operand);
                    break;
                case NonNullExpr nonNull:
                    Expression(nonNull.Operand);
                    break;
                case BinaryExpr binary:
                    Sites.Add(new Site(SiteKind.BinaryOperator, binary.OperatorLocation.Span, binary));
                    Expression(binary.Left);
                    Expression(binary.Right);
                    break;
                case CoalesceExpr coalesce:
                    Expression(coalesce.Left);
                    Expression(coalesce.Right);
                    break;
                case IsExpr @is:
                    Expression(@is.Operand);
                    Pattern(@is.Pattern);
                    break;
                case SwitchExpr @switch:
                    Switch(@switch.Value, @switch.Cases);
                    break;
                case TryExpr @try:
                    Expression(@try.Operand);
                    break;
                case CallExpr call:
                    Expression(call.Callee);
                    foreach (var argument in call.Arguments)
                        Expression(argument);
                    break;
                case IndexExpr index:
                    Expression(index.Target);
                    foreach (var argument in index.Arguments)
                        Expression(argument);
                    if (index.Arguments is [var only] && only.Location.Span.Length > 0)
                        Sites.Add(new Site(SiteKind.IndexArgument, only.Location.Span, index));
                    break;
                case ArrayLiteralExpr array:
                    if (array.Elements.Count > 0)
                        Sites.Add(new Site(SiteKind.ArrayLiteral, array.Location.Span, array));
                    foreach (var element in array.Elements)
                        Expression(element);
                    break;
                case RefArgExpr refArgument:
                    Expression(refArgument.Operand, place: true);
                    break;
                case NamedArgExpr named:
                    Expression(named.Value);
                    break;
                case NewExpr @new:
                    Type(@new.Type);
                    foreach (var argument in @new.Arguments)
                        Expression(argument);
                    break;
                case NewArrayExpr newArray:
                    Type(newArray.ElementType);
                    Expression(newArray.Length);
                    Sites.Add(new Site(SiteKind.ArrayLength, newArray.Length.Location.Span, newArray));
                    break;
                case CastExpr cast:
                    Type(cast.Type);
                    Expression(cast.Operand);
                    break;
                case ConditionalExpr conditional:
                    Expression(conditional.Condition);
                    Expression(conditional.WhenTrue);
                    Expression(conditional.WhenFalse);
                    break;
                case LambdaExpr lambda:
                    foreach (var parameter in lambda.Parameters)
                    {
                        if (parameter.Type is { } type)
                            Type(type);
                    }
                    if (lambda.Value is { } lambdaValue)
                        Expression(lambdaValue);
                    if (lambda.Body is { } body)
                        Statement(body, inList: true);
                    break;
            }
        }

        void Literal(SiteKind kind, Expr literal, bool inPattern)
        {
            if (literal.Location.Span.Length > 0)
                Sites.Add(new Site(kind, literal.Location.Span, literal, inPattern));
        }
    }
}
