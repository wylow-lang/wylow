using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    /// <summary>
    /// A case's patterns: over a nullable value, <c>null</c> matches the value and the others its
    /// non-null type. Sets <paramref name="broken"/> if a pattern has an error, which would make
    /// coverage errors misleading.
    /// </summary>
    List<BoundPattern> BindCasePatterns(SwitchCase c, BoundExpression value, ref bool broken)
    {
        int errors = diagnostics.Count;
        var place = MatchPlace.Of(this, value);
        List<BoundPattern> patterns = [.. c.Patterns.Select(p => BindPattern(p, IsNullPattern(p) ? value.Type : value.Type.NonNull, canBind: c.Patterns.Count == 1, place))];
        if (c.Default is { } listed && !c.Patterns.All(IsNullPattern))
            Error(Catalog.DefaultListedWithPattern(listed));
        broken |= diagnostics.Count > errors;
        return patterns;
    }

    /// <summary>
    /// The value a switch matches. With a <c>case null</c> it is taken as it is, nullable or not;
    /// without one, patterns match values, not null, so a nullable local is narrowed, as for a member access.
    /// </summary>
    BoundExpression BindSwitchValue(Expr syntax, IReadOnlyList<SwitchCase> cases)
    {
        var value = BindExpression(syntax);
        if (value.Type == WylType.Void)
            return ErrorExpression(Catalog.SwitchOnVoid(syntax.Location));
        if (value.Type == WylType.Null)
            return ErrorExpression(Catalog.SwitchOnNull(syntax.Location));
        if (cases.Any(c => c.Patterns.Any(IsNullPattern)))
        {
            if (value.Type.IsNullableReference)
                return ErrorExpression(MayReferToNothing(syntax.Location, value));
            return CheckMatchable(value);
        }
        if (value.Type.MayBeNull)
        {
            value = AssumeNonNull(value);
            if (value.Type.IsNullableReference)
                return ErrorExpression(Catalog.SwitchOnNullableReference(syntax.Location, value.Type.ToString()));
            if (value.Type.IsNullable)
                return ErrorExpression(Catalog.SwitchOnNullable(syntax.Location, value.Type.ToString()));
        }
        return CheckMatchable(value);
    }

    /// <summary>A matched value is read while patterns bind its parts, which a span's view could not be held through.</summary>
    BoundExpression CheckMatchable(BoundExpression value) =>
        value.Type.IsRefStruct ? ErrorExpression(Catalog.MatchViewType(value.Location, value.Type.ToString())) : value;

    BoundStatement BindSwitch(SwitchStmt s)
    {
        var value = BindSwitchValue(s.Value, s.Cases);
        var cases = new List<BoundSwitchCase<BoundStatement>>();
        bool broken = value.Type == WylType.Error;
        foreach (var c in s.Cases)
        {
            scope = new Scope(scope);
            var patterns = BindCasePatterns(c, value, ref broken);
            var guard = BindGuard(c);
            cases.Add(new BoundSwitchCase<BoundStatement>(c.Location, patterns, c.IsDefault, guard, BindStatement(c.Body!)));
            scope = scope.Parent;
        }
        CheckCases(s.Location, value.Type, s.Cases, [.. cases.Select(c => c.Patterns)], broken);
        return new BoundSwitch(s.Location, value, cases);
    }

    /// <summary>
    /// A case's guard, bound in the case's scope after its patterns, so that it sees the names they
    /// bind and the case sees the names an <c>is</c> in it binds.
    /// </summary>
    BoundExpression? BindGuard(SwitchCase c)
    {
        if (c.Guard is not { } syntax)
            return null;
        if (c.IsDefault)
            Error(Catalog.GuardOnDefault(syntax.Location));
        else if (c.Patterns.Any(IsNullPattern))
            Error(Catalog.GuardOnNull(syntax.Location));
        return BindCondition(syntax);
    }

    /// <summary>
    /// Whether a case counts toward exhaustiveness and hides later cases: one with a guard doesn't,
    /// since its condition may be false. One whose guard is an error counts, so as to add no more errors.
    /// </summary>
    static bool CountsAsMatching(SwitchCase c) => c.Guard is null || c.IsDefault || c.Patterns.Any(IsNullPattern);

    static bool IsNoReturn(BoundExpression expression) => expression is BoundCall { Method.Symbol.IsNoReturn: true };

    /// <summary>
    /// <paramref name="mayWait"/>: a call argument whose cases include lambdas without a function type
    /// is left pending until overload resolution gives it one (see <see cref="Choose"/>).
    /// </summary>
    BoundExpression BindSwitchExpression(SwitchExpr e, WylType? expected, bool mayWait = false)
    {
        var value = BindSwitchValue(e.Value, e.Cases);
        var options = new List<(SwitchCase Syntax, List<BoundPattern> Patterns, BoundExpression? Guard, BoundExpression Value, Scope Scope)>();
        bool broken = value.Type == WylType.Error;
        foreach (var c in e.Cases)
        {
            scope = new Scope(scope);
            var patterns = BindCasePatterns(c, value, ref broken);
            var guard = BindGuard(c);
            options.Add((c, patterns, guard, BindOption(c.Value!, expected), scope));
            scope = scope.Parent!;
        }
        CheckCases(e.Location, value.Type, e.Cases, [.. options.Select(o => o.Patterns)], broken);
        return Choose(e.Location, [.. options.Select(o => o.Value)], expected, mayWait, type =>
        {
            // Lambdas are bound in their case's scope, so that using a name it declares is reported as such.
            var enclosing = scope;
            var bound = options.Select(o =>
            {
                scope = o.Scope;
                return new BoundSwitchCase<BoundExpression>(o.Syntax.Location, o.Patterns, o.Syntax.IsDefault, o.Guard, FinishOption(o.Value, type));
            }).ToList();
            scope = enclosing;
            return MakeSwitchExpression(e.Location, value, bound, expected ?? type);
        });
    }

    BoundExpression MakeSwitchExpression(
        Location location,
        BoundExpression value,
        List<BoundSwitchCase<BoundExpression>> bound,
        WylType? expected)
    {
        // A case that cannot complete, such as `default -> panic(...)`, needs no value.
        var producing = bound.Select(b => b.Body).Where(v => !IsNoReturn(v)).ToList();
        foreach (var caseValue in producing.Where(v => v.Type == WylType.Void))
            Error(Catalog.VoidSwitchCase(caseValue.Location));
        if (value.Type == WylType.Error || producing.Any(v => v.Type == WylType.Error || v.Type == WylType.Void))
            return new BoundErrorExpression(location);
        if (producing.Count == 0)
            return ErrorExpression(Catalog.NoProducingCase(location));

        var type = expected ?? CommonType(producing);
        if (type is null)
        {
            return ErrorExpression(Catalog.IncompatibleCases(location, producing.Select(v => v.Type.ToString()).Distinct()));
        }
        if (type.IsRefStruct)
            return ErrorExpression(Catalog.SwitchExpressionView(location, type.ToString()));
        if (type.IsNullableReference)
            return ErrorExpression(Catalog.SwitchExpressionReference(location));
        // A nullable non-escaping choice would refer to a function made only for the choice.
        if (type.IsNullableNonEscapingFunction)
            type = EscapingForm(type);
        var cases = bound.Select(b => IsNoReturn(b.Body) ? b : b with { Body = Convert(b.Body, type) }).ToList();
        if (type.IsNonEscapingFunction)
            cases.ForEach(CheckCaseCaptures);
        return new BoundSwitchExpression(location, value, cases, type);
    }

    /// <summary>
    /// A switch expression that chooses a non-escaping function makes the functions it chooses from
    /// outside its cases, so their lambdas can't use the names a case declares.
    /// </summary>
    void CheckCaseCaptures(BoundSwitchCase<BoundExpression> c)
    {
        var declared = c.Patterns.SelectMany(Patterns.Bindings).Concat(c.Guard is { } guard ? IsBindings(guard) : []).Concat(IsBindings(c.Body)).ToHashSet();
        var use = BoundTree.FunctionOptions(c.Body).OfType<BoundLambda>()
            .SelectMany(BoundTree.AllExpressions).OfType<BoundLocalAccess>()
            .FirstOrDefault(a => declared.Contains(a.Local));
        if (use is not null)
            Error(Catalog.CaseNameInChosenLambda(use.Location, use.Local.Name));
    }

    /// <summary>The names declared by <c>is</c> patterns in an expression, but not inside its lambdas.</summary>
    static IEnumerable<LocalSymbol> IsBindings(BoundExpression expression) => expression switch
    {
        BoundLambda => [],
        BoundIs pattern => [.. IsBindings(pattern.Operand), .. Patterns.Bindings(pattern.Pattern)],
        _ => BoundTree.Children(expression).SelectMany(IsBindings),
    };

    /// <summary>The first case's type, or a later one, that every case converts to; a type may become nullable for a <c>null</c> case.</summary>
    WylType? CommonType(IReadOnlyList<BoundExpression> values)
    {
        var candidates = values
            .Select(v => v.Type)
            .Where(t => t != WylType.Null)
            .SelectMany(t => t.CanBeNullable ? [t, t.Nullable] : new[] { t })
            .Distinct();
        // A non-escaping function that can't be converted is reported as that, once the type is chosen.
        return candidates.FirstOrDefault(t => values.All(v => CanConvert(v, t) || IsEscapingMismatch(v.Type, t)));
    }

    /// <summary>Checks that <c>default</c> comes last, that every case can match something, and that the cases cover every value.</summary>
    void CheckCases(Location location, WylType type, IReadOnlyList<SwitchCase> syntax, IReadOnlyList<IReadOnlyList<BoundPattern>> patterns, bool broken)
    {
        for (int i = 0; i < syntax.Count - 1; i++)
        {
            if (syntax[i].IsDefault)
                Error(Catalog.DefaultNotLast(syntax[i].Location));
        }
        if (broken)
            return;

        var space = new MatchSpace(type, currentFunction);
        for (int i = 0; i < syntax.Count; i++)
        {
            foreach (var pattern in patterns[i])
            {
                if (!space.Add(pattern, counts: CountsAsMatching(syntax[i])))
                    Error(Catalog.UnreachableCase(pattern.Location));
            }
            if (syntax[i].Default is { } written && !space.Add(null))
                Error(Catalog.UnreachableDefault(written));
        }
        if (space.Missing() is { } missing)
        {
            Error(Catalog.NotExhaustive(location, type.ToString(), missing == "_" ? null : missing));
        }
    }
}
