using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

/// <summary>
/// Flow-sensitive checks: definite assignment, reachability, null narrowing of locals, and that a
/// constructor assigns every field without a default before using <c>this</c> or returning.
/// </summary>
public sealed class FlowAnalyzer(DiagnosticBag diagnostics)
{
    sealed class State
    {
        public bool Reachable = true;
        public HashSet<LocalSymbol> Assigned = [];
        public HashSet<LocalSymbol> NonNull = [];
        public HashSet<FieldSymbol> AssignedFields = [];

        public static State Unreachable() => new() { Reachable = false };

        public State Clone() => new() { Reachable = Reachable, Assigned = [.. Assigned], NonNull = [.. NonNull], AssignedFields = [.. AssignedFields] };

        // Unreachable code may assume anything.
        public bool IsAssigned(LocalSymbol local) => !Reachable || Assigned.Contains(local);
        public bool IsNonNull(LocalSymbol local) => !Reachable || NonNull.Contains(local);
        public bool IsAssigned(FieldSymbol field) => !Reachable || AssignedFields.Contains(field);

        public static State Join(State a, State b)
        {
            if (!a.Reachable)
                return b.Clone();
            if (!b.Reachable)
                return a.Clone();
            var result = a.Clone();
            result.Assigned.IntersectWith(b.Assigned);
            result.NonNull.IntersectWith(b.NonNull);
            result.AssignedFields.IntersectWith(b.AssignedFields);
            return result;
        }
    }

    sealed class Loop
    {
        public readonly List<State> Breaks = [];
        public readonly List<State> Continues = [];
    }

    readonly Stack<Loop> loops = new();
    readonly HashSet<LocalSymbol> reported = [];
    readonly HashSet<object> reportedAt = [];

    /// <summary>In a constructor, the fields it must assign; empty elsewhere.</summary>
    List<FieldSymbol> requiredFields = [];

    public void Analyze(BoundFunction function)
    {
        var symbol = function.Symbol;
        // The implicit constructor of a class with such fields is reported by the binder.
        requiredFields = symbol.IsConstructor && !symbol.IsImplicit ? [.. symbol.ContainingType!.Fields.Where(f => f.RequiresAssignment)] : [];

        var state = new State();
        state.Assigned.UnionWith(symbol.Parameters);
        state = Statement(function.Body, state);

        var body = function.Body.Location;
        var end = new Location(body.Source, new TextSpan(Math.Max(body.Span.End - 1, body.Span.Start), body.Span.Length > 0 ? 1 : 0));
        var returnType = symbol.ReturnType;
        if (state.Reachable && !symbol.IsConstructor && returnType != VplType.Void && returnType != VplType.Error)
            diagnostics.Error(end, $"'{symbol.Name}' must return a value on every path");
        if (state.Reachable)
            CheckFieldsAssigned(state, end);
        requiredFields = [];
    }

    void CheckFieldsAssigned(State state, Location location)
    {
        if (requiredFields.FirstOrDefault(f => !state.IsAssigned(f)) is { } field)
            diagnostics.Error(location, $"field '{field.Name}' must be assigned before the constructor returns");
    }

    State Statement(BoundStatement statement, State state)
    {
        switch (statement)
        {
            case BoundBlock block:
                foreach (var s in block.Statements)
                    state = Statement(s, state);
                return state;

            case BoundVariableDeclaration declaration:
                if (declaration.Initializer is not null)
                {
                    state = Expression(declaration.Initializer, state);
                    state.Assigned.Add(declaration.Local);
                    SetNullability(state, declaration.Local, declaration.Initializer);
                }
                return state;

            case BoundAssignment assignment:
                state = Expression(assignment.Value, state);
                // Assigning a whole local initializes it; assigning a field requires its root to be initialized.
                if (assignment.Target is BoundLocalAccess target)
                {
                    state.Assigned.Add(target.Local);
                    SetNullability(state, target.Local, assignment.Value);
                }
                else if (assignment.Target is BoundFieldAccess { Receiver: BoundThis } field && requiredFields.Count > 0)
                {
                    state.AssignedFields.Add(field.Field);
                }
                else
                {
                    state = Expression(assignment.Target, state);
                }
                return state;

            case BoundExpressionStatement expressionStatement:
                state = Expression(expressionStatement.Expression, state);
                return expressionStatement.Expression is BoundCall { Method.Symbol.IsNoReturn: true } ? State.Unreachable() : state;

            case BoundSuperCall super:
                foreach (var argument in super.Arguments)
                    state = Expression(argument, state);
                return state;

            case BoundIf ifStatement:
            {
                var (whenTrue, whenFalse) = Condition(ifStatement.Condition, state);
                var afterThen = Statement(ifStatement.Then, whenTrue);
                var afterElse = ifStatement.Else is null ? whenFalse : Statement(ifStatement.Else, whenFalse);
                return State.Join(afterThen, afterElse);
            }

            case BoundWhile whileStatement:
            {
                var entry = LoopEntry(state, whileStatement.Body);
                var (whenTrue, whenFalse) = Condition(whileStatement.Condition, entry);
                var loop = AnalyzeLoopBody(whileStatement.Body, whenTrue);
                var exit = IsConstantTrue(whileStatement.Condition) ? State.Unreachable() : whenFalse;
                return loop.Breaks.Aggregate(exit, State.Join);
            }

            case BoundFor forStatement:
            {
                if (forStatement.Initializer is not null)
                    state = Statement(forStatement.Initializer, state);
                var entry = forStatement.Update is null
                    ? LoopEntry(state, forStatement.Body)
                    : LoopEntry(LoopEntry(state, forStatement.Update), forStatement.Body);
                var (whenTrue, whenFalse) = forStatement.Condition is null ? (entry, State.Unreachable()) : Condition(forStatement.Condition, entry);

                var loop = new Loop();
                loops.Push(loop);
                var endOfBody = Statement(forStatement.Body, whenTrue.Clone());
                loops.Pop();
                if (forStatement.Update is not null)
                    Statement(forStatement.Update, loop.Continues.Aggregate(endOfBody, State.Join));

                var exit = forStatement.Condition is null || IsConstantTrue(forStatement.Condition) ? State.Unreachable() : whenFalse;
                return loop.Breaks.Aggregate(exit, State.Join);
            }

            case BoundReturn returnStatement:
                if (returnStatement.Value is not null)
                    state = Expression(returnStatement.Value, state);
                if (state.Reachable)
                    CheckFieldsAssigned(state, returnStatement.Location);
                return State.Unreachable();

            case BoundSwitch switchStatement:
            {
                state = Expression(switchStatement.Value, state);
                var exit = State.Unreachable();
                foreach (var switchCase in switchStatement.Cases)
                    exit = State.Join(exit, Statement(switchCase.Body, CaseEntry(state, switchCase.Patterns)));
                return exit;
            }

            case BoundBreak:
                if (loops.TryPeek(out var breakLoop))
                    breakLoop.Breaks.Add(state.Clone());
                return State.Unreachable();

            case BoundContinue:
                if (loops.TryPeek(out var continueLoop))
                    continueLoop.Continues.Add(state.Clone());
                return State.Unreachable();

            default:
                throw new InvalidOperationException($"unexpected statement {statement.GetType().Name}");
        }
    }

    /// <summary>A case starts where the switch value has been evaluated and its patterns' names are assigned.</summary>
    static State CaseEntry(State afterValue, IEnumerable<BoundPattern> patterns)
    {
        var entry = afterValue.Clone();
        entry.Assigned.UnionWith(patterns.SelectMany(Patterns.Bindings));
        return entry;
    }

    static void SetNullability(State state, LocalSymbol local, BoundExpression value)
    {
        if (IsNonNull(value, state))
            state.NonNull.Add(local);
        else
            state.NonNull.Remove(local);
    }

    static bool IsNonNull(BoundExpression value, State state) => value switch
    {
        BoundConversion conversion => IsNonNull(conversion.Operand, state),
        BoundLocalAccess access => !access.Type.IsNullable || state.IsNonNull(access.Local),
        _ => !value.Type.IsNullable && value.Type != VplType.Null,
    };

    /// <summary>
    /// The loop is analyzed once, so narrowing must not depend on the iteration: a local assigned
    /// anywhere in the loop might be null again when the next iteration starts.
    /// </summary>
    static State LoopEntry(State state, BoundStatement body)
    {
        var entry = state.Clone();
        foreach (var local in AssignedLocals(body))
            entry.NonNull.Remove(local);
        return entry;
    }

    static IEnumerable<LocalSymbol> AssignedLocals(BoundStatement statement) => statement switch
    {
        BoundBlock block => block.Statements.SelectMany(AssignedLocals),
        BoundAssignment { Target: BoundLocalAccess target } => [target.Local],
        BoundIf ifStatement => [.. AssignedLocals(ifStatement.Then), .. ifStatement.Else is null ? [] : AssignedLocals(ifStatement.Else)],
        BoundWhile whileStatement => AssignedLocals(whileStatement.Body),
        BoundSwitch switchStatement => switchStatement.Cases.SelectMany(c => AssignedLocals(c.Body)),
        BoundFor forStatement => [
            .. forStatement.Initializer is null ? [] : AssignedLocals(forStatement.Initializer),
            .. AssignedLocals(forStatement.Body),
            .. forStatement.Update is null ? [] : AssignedLocals(forStatement.Update),
        ],
        _ => [],
    };

    Loop AnalyzeLoopBody(BoundStatement body, State entry)
    {
        // Analyzing the body once is sound: assignments only ever add to the assigned sets, every
        // later iteration starts with at least what the first one had, and LoopEntry has removed
        // narrowing that an iteration could undo.
        var loop = new Loop();
        loops.Push(loop);
        Statement(body, entry.Clone());
        loops.Pop();
        return loop;
    }

    static bool IsConstantTrue(BoundExpression expression) => expression is BoundLiteral { Value: true };

    /// <summary>The states after a condition evaluates to true and to false.</summary>
    (State WhenTrue, State WhenFalse) Condition(BoundExpression condition, State state)
    {
        switch (condition)
        {
            case BoundUnary { Operator: BoundUnaryOperator.LogicalNot } not:
            {
                var (whenTrue, whenFalse) = Condition(not.Operand, state);
                return (whenFalse, whenTrue);
            }
            case BoundBinary { Operator: BoundBinaryOperator.LogicalAnd } and:
            {
                var (leftTrue, leftFalse) = Condition(and.Left, state);
                var (rightTrue, rightFalse) = Condition(and.Right, leftTrue);
                return (rightTrue, State.Join(leftFalse, rightFalse));
            }
            case BoundBinary { Operator: BoundBinaryOperator.LogicalOr } or:
            {
                var (leftTrue, leftFalse) = Condition(or.Left, state);
                var (rightTrue, rightFalse) = Condition(or.Right, leftFalse);
                return (State.Join(leftTrue, rightTrue), rightFalse);
            }
            case BoundBinary { Operator: BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual } comparison
                when NullTestedLocal(comparison) is { } local:
            {
                state = Expression(comparison, state);
                var nonNull = state.Clone();
                nonNull.NonNull.Add(local);
                return comparison.Operator == BoundBinaryOperator.NotEqual ? (nonNull, state) : (state, nonNull);
            }
            case BoundIs pattern:
            {
                state = Expression(pattern.Operand, state);
                var matched = state.Clone();
                matched.Assigned.UnionWith(Patterns.Bindings(pattern.Pattern));
                if (pattern.Operand is BoundLocalAccess access)
                    matched.NonNull.Add(access.Local);
                return (matched, state);
            }
            case BoundLiteral { Value: bool value }:
                return value ? (state, State.Unreachable()) : (State.Unreachable(), state);
            default:
            {
                state = Expression(condition, state);
                return (state, state.Clone());
            }
        }
    }

    static LocalSymbol? NullTestedLocal(BoundBinary comparison) => (comparison.Left, comparison.Right) switch
    {
        (BoundLocalAccess local, BoundNullLiteral) => local.Local,
        (BoundNullLiteral, BoundLocalAccess local) => local.Local,
        _ => null,
    };

    void ReportOnce(object key, Location location, string message)
    {
        if (reportedAt.Add(key))
            diagnostics.Error(location, message);
    }

    /// <summary>Checks an expression and returns the state after it; the given state may be updated in place.</summary>
    State Expression(BoundExpression expression, State state)
    {
        switch (expression)
        {
            case BoundLocalAccess access:
                if (!state.IsAssigned(access.Local) && reported.Add(access.Local))
                    diagnostics.Error(access.Location, $"'{access.Local.Name}' might not have been assigned");
                else if (access.AssumesNonNull && !state.IsNonNull(access.Local))
                {
                    ReportOnce(access, access.Location,
                        $"'{access.Local.Name}' might be null here; check it with 'if ({access.Local.Name} != null)', or use '?.' or '!'");
                }
                return state;
            case BoundThis:
                if (requiredFields.FirstOrDefault(f => !state.IsAssigned(f)) is { } unassigned)
                {
                    ReportOnce(expression, expression.Location,
                        $"'this' cannot be used until every field is assigned, and '{unassigned.Name}' might not be");
                }
                return state;
            // Reading a field is not a use of `this`; only reading an unassigned required field is an error.
            case BoundFieldAccess { Receiver: BoundThis } field:
                if (requiredFields.Contains(field.Field) && !state.IsAssigned(field.Field))
                    ReportOnce(field, field.Location, $"field '{field.Field.Name}' might not have been assigned");
                return state;
            case BoundBinary { Operator: BoundBinaryOperator.LogicalAnd or BoundBinaryOperator.LogicalOr }:
            case BoundIs:
            {
                var (whenTrue, whenFalse) = Condition(expression, state);
                return State.Join(whenTrue, whenFalse);
            }
            case BoundConditional conditional:
            {
                var (whenTrue, whenFalse) = Condition(conditional.Condition, state);
                return State.Join(Expression(conditional.WhenTrue, whenTrue), Expression(conditional.WhenFalse, whenFalse));
            }
            case BoundCoalesce coalesce:
            {
                state = Expression(coalesce.Left, state);
                return State.Join(state, Expression(coalesce.Right, state.Clone()));
            }
            case BoundSwitchExpression switchExpression:
            {
                state = Expression(switchExpression.Value, state);
                var exit = State.Unreachable();
                foreach (var switchCase in switchExpression.Cases)
                {
                    var afterCase = Expression(switchCase.Body, CaseEntry(state, switchCase.Patterns));
                    exit = State.Join(exit, switchCase.Body is BoundCall { Method.Symbol.IsNoReturn: true } ? State.Unreachable() : afterCase);
                }
                return exit;
            }
            case BoundConditionalAccess access:
            {
                state = Expression(access.Receiver, state);
                var whenNotNull = state.Clone();
                whenNotNull.Assigned.Add(access.Value);
                return State.Join(state, Expression(access.WhenNotNull, whenNotNull));
            }
            default:
                foreach (var child in Children(expression))
                    state = Expression(child, state);
                return state;
        }
    }

    static IEnumerable<BoundExpression> Children(BoundExpression expression) => expression switch
    {
        BoundUnary unary => [unary.Operand],
        BoundBinary binary => [binary.Left, binary.Right],
        BoundCall call => call.Receiver is null ? call.Arguments : [call.Receiver, .. call.Arguments],
        BoundConstruction construction => [.. construction.Arguments, .. construction.Initializers.Select(i => i.Value)],
        BoundNew creation => creation.Arguments,
        BoundFieldAccess field => [field.Receiver],
        BoundConversion conversion => [conversion.Operand],
        BoundNonNull nonNull => [nonNull.Operand],
        BoundVariant variant => variant.Arguments,
        BoundTry attempt => [attempt.Operand],
        _ => [],
    };
}
