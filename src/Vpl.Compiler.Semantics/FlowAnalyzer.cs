using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Syntax;
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

        /// <summary>The locals a non-escaping lambda made so far assigns, which are never narrowed again: any call may run it.</summary>
        public HashSet<LocalSymbol> LambdaAssigned = [];

        /// <summary>The locals assigned on some path here, which an immutable local must not be again.</summary>
        public HashSet<LocalSymbol> MaybeAssigned = [];

        public static State Unreachable() => new() { Reachable = false };

        public State Clone() => new()
        {
            Reachable = Reachable,
            Assigned = [.. Assigned],
            NonNull = [.. NonNull],
            AssignedFields = [.. AssignedFields],
            MaybeAssigned = [.. MaybeAssigned],
            LambdaAssigned = [.. LambdaAssigned],
        };

        public bool SameAs(State other) =>
            Reachable == other.Reachable && Assigned.SetEquals(other.Assigned) && NonNull.SetEquals(other.NonNull)
            && AssignedFields.SetEquals(other.AssignedFields) && MaybeAssigned.SetEquals(other.MaybeAssigned) && LambdaAssigned.SetEquals(other.LambdaAssigned);

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
            result.MaybeAssigned.UnionWith(b.MaybeAssigned);
            result.LambdaAssigned.UnionWith(b.LambdaAssigned);
            return result;
        }
    }

    sealed class Loop
    {
        public readonly List<State> Breaks = [];
        public readonly List<State> Continues = [];
    }

    /// <summary>One pass over a loop: the state where an iteration ends and goes back to the top, and where the loop is left without a <c>break</c>.</summary>
    sealed record Iteration(Loop Loop, State Back, State Exit)
    {
        public State Left => Loop.Breaks.Aggregate(Exit, State.Join);
    }

    readonly Stack<Loop> loops = new();
    readonly HashSet<LocalSymbol> reported = [];
    readonly HashSet<object> reportedAt = [];

    /// <summary>Above zero while a loop is analyzed again to find what its top state is, which must not report anything twice.</summary>
    int quiet;

    /// <summary>The non-escaping lambdas being analyzed, innermost last, with the locals around each that its body assigns.</summary>
    readonly List<(BoundLambda Lambda, HashSet<LocalSymbol> Assigned)> lambdas = [];

    /// <summary>In a constructor, the fields it must assign; empty elsewhere.</summary>
    List<FieldSymbol> requiredFields = [];

    int lambdaDepth;

    /// <summary>Inside a lambda, the locals narrowed where it was created, for explaining why they aren't inside.</summary>
    HashSet<LocalSymbol> narrowedOutside = [];

    void Error(Location location, string message)
    {
        if (quiet == 0)
            diagnostics.Error(location, message);
    }

    public void Analyze(BoundFunction function)
    {
        var symbol = function.Symbol;
        // The implicit constructor of a class with such fields is reported by the binder.
        requiredFields = symbol.IsConstructor && !symbol.IsImplicit ? [.. symbol.ContainingType!.Fields.Where(f => f.RequiresAssignment)] : [];

        var state = new State();
        state.Assigned.UnionWith(symbol.Parameters);
        state = Statement(function.Body, state);

        var end = EndOf(function.Body.Location);
        var returnType = symbol.ReturnType;
        if (state.Reachable && !symbol.IsConstructor && returnType != VplType.Void && returnType != VplType.Error)
            Error(end, $"'{symbol.Name}' must return a value on every path");
        if (state.Reachable)
            CheckFieldsAssigned(state, end);
        requiredFields = [];
    }

    /// <summary>A field initializer, which can't use locals, but may contain lambdas with bodies to check.</summary>
    public void Analyze(BoundExpression initializer)
    {
        Expression(initializer, new State());
    }

    static Location EndOf(Location body) =>
        new(body.Source, new TextSpan(Math.Max(body.Span.End - 1, body.Span.Start), body.Span.Length > 0 ? 1 : 0));

    /// <summary>
    /// A lambda runs only after it is created, so what is assigned there stays assigned. Narrowing
    /// is not trusted inside it, since the code before it runs could undo it, except a 'ref?'
    /// variable's, which nothing can make refer elsewhere.
    /// </summary>
    void Lambda(BoundLambda lambda, State outer)
    {
        var state = new State
        {
            Reachable = outer.Reachable,
            Assigned = [.. outer.Assigned],
            NonNull = [.. outer.NonNull.Where(l => l.Type.IsNullableReference)],
            AssignedFields = [.. outer.AssignedFields],
            MaybeAssigned = [.. outer.MaybeAssigned],
            LambdaAssigned = [.. outer.LambdaAssigned],
        };
        state.Assigned.UnionWith(lambda.Function.Parameters);
        var enclosingNarrowed = narrowedOutside;
        narrowedOutside = [.. enclosingNarrowed, .. outer.NonNull];
        lambdaDepth++;
        var assigned = new HashSet<LocalSymbol>();
        if (!lambda.Function.IsEscaping)
            lambdas.Add((lambda, assigned));
        state = Statement(lambda.Body, state);
        lambdas.RemoveAll(l => l.Assigned == assigned);
        lambdaDepth--;
        narrowedOutside = enclosingNarrowed;
        outer.LambdaAssigned.UnionWith(assigned);
        Unnarrow(outer, assigned);
        var returnType = lambda.Function.ReturnType;
        if (state.Reachable && returnType != VplType.Void && returnType != VplType.Error)
            Error(EndOf(lambda.Body.Location), "this lambda must return a value on every path");
    }

    void CheckFieldsAssigned(State state, Location location)
    {
        if (requiredFields.FirstOrDefault(f => !state.IsAssigned(f)) is { } field)
            Error(location, $"field '{field.Name}' must be assigned before the constructor returns");
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
                // A loop body declares a new variable on each iteration.
                state.MaybeAssigned.Remove(declaration.Local);
                if (declaration.Initializer is not null)
                {
                    state = Expression(declaration.Initializer, state);
                    state.Assigned.Add(declaration.Local);
                    state.MaybeAssigned.Add(declaration.Local);
                    SetNullability(state, declaration.Local, declaration.Initializer);
                }
                // Fixed arrays are often too large to initialize element by element.
                else if (declaration.Local.Type.IsFixedArray && Conversions.IsDefaultable(declaration.Local.Type))
                {
                    state.Assigned.Add(declaration.Local);
                    state.MaybeAssigned.Add(declaration.Local);
                }
                return state;

            case BoundAssignment assignment:
            {
                // Assigning a whole local initializes it; assigning a field requires its root to be
                // initialized. Like evaluation, analysis finds the target before the value. Assigning
                // a 'ref?' variable assigns the place it refers to, so it must refer to one.
                bool initializesField = assignment.Target is BoundFieldAccess { Receiver: BoundThis } && requiredFields.Count > 0;
                bool assignsReferenced = assignment.Target is BoundLocalAccess { Local.Type.IsNullableReference: true };
                if ((assignment.Target is not BoundLocalAccess || assignsReferenced) && !initializesField)
                {
                    state = Expression(assignment.Target, state);
                }
                state = Expression(assignment.Value, state);
                if (assignment.Target is BoundLocalAccess target && !assignsReferenced)
                {
                    // Assigning a 'ref' variable assigns the place it refers to.
                    if (target.Local is { IsMutable: false, RefKind: RefKind.None } && state.Reachable && state.MaybeAssigned.Contains(target.Local))
                    {
                        ReportOnce(assignment, target.Location,
                            $"'{target.Local.Name}' might already have been assigned; declare it 'mut' to assign it more than once");
                    }
                    NoteAssigned(target.Local);
                    state.Assigned.Add(target.Local);
                    state.MaybeAssigned.Add(target.Local);
                    SetNullability(state, target.Local, assignment.Value);
                }
                else if (initializesField)
                {
                    state.AssignedFields.Add(((BoundFieldAccess)assignment.Target).Field);
                }
                return state;
            }

            case BoundExpressionStatement expressionStatement:
                state = Expression(expressionStatement.Expression, state);
                return expressionStatement.Expression is BoundCall { Method.Symbol.IsNoReturn: true } ? State.Unreachable() : state;

            case BoundSuperCall super:
                foreach (var argument in super.Arguments)
                    state = Expression(argument, state);
                Unnarrow(state, RefArguments(super.Constructor, super.Arguments));
                return state;

            case BoundIf ifStatement:
            {
                var (whenTrue, whenFalse) = Condition(ifStatement.Condition, state);
                var afterThen = Statement(ifStatement.Then, whenTrue);
                var afterElse = ifStatement.Else is null ? whenFalse : Statement(ifStatement.Else, whenFalse);
                return State.Join(afterThen, afterElse);
            }

            case BoundWhile whileStatement:
                return Iterate(state, entry =>
                {
                    var (whenTrue, whenFalse) = Condition(whileStatement.Condition, entry);
                    var exit = IsConstantTrue(whileStatement.Condition) ? State.Unreachable() : whenFalse;
                    return Body(whileStatement.Body, whenTrue, exit);
                }).Left;

            case BoundFor forStatement:
            {
                if (forStatement.Initializer is not null)
                    state = Statement(forStatement.Initializer, state);
                return Iterate(state, entry =>
                {
                    var (whenTrue, whenFalse) = forStatement.Condition is null ? (entry, State.Unreachable()) : Condition(forStatement.Condition, entry);
                    var exit = forStatement.Condition is null || IsConstantTrue(forStatement.Condition) ? State.Unreachable() : whenFalse;
                    var iteration = Body(forStatement.Body, whenTrue, exit);
                    var back = forStatement.Update is null ? iteration.Back : Statement(forStatement.Update, iteration.Back);
                    return iteration with { Back = back };
                }).Left;
            }

            case BoundForRange range:
            {
                state = Expression(range.End, Expression(range.Start, state));
                return Iterate(state, entry =>
                {
                    var body = entry.Clone();
                    body.Assigned.Add(range.Local);
                    return Body(range.Body, body, entry);
                }).Left;
            }

            case BoundReturn returnStatement:
                if (returnStatement.Value is not null)
                    state = Expression(returnStatement.Value, state);
                if (state.Reachable && lambdaDepth == 0)
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
            Narrow(state, local);
        else
            state.NonNull.Remove(local);
    }

    /// <summary>
    /// Only a variable that nothing else can assign can be narrowed: through another reference to
    /// the same variable, a 'ref' or 'in' one could become null after the check, and so could one
    /// a lambda assigns. A 'ref?' variable always can, since nothing can make it refer elsewhere.
    /// </summary>
    static void Narrow(State state, LocalSymbol local)
    {
        if (local.Type.IsNullableReference || (local.RefKind == RefKind.None && !state.LambdaAssigned.Contains(local)))
            state.NonNull.Add(local);
    }

    /// <summary>A lambda being analyzed that has the local around it may assign it whenever it runs.</summary>
    void NoteAssigned(LocalSymbol local)
    {
        foreach (var (lambda, assigned) in lambdas)
        {
            if (BoundTree.IsDeclaredAround(local, lambda.Function))
                assigned.Add(local);
        }
    }

    /// <summary>Ends the narrowing of locals that may have been assigned. Assigning a 'ref?' variable assigns the place it refers to.</summary>
    void Unnarrow(State state, IEnumerable<LocalSymbol> locals)
    {
        foreach (var local in locals)
        {
            NoteAssigned(local);
            if (!local.Type.IsNullableReference)
                state.NonNull.Remove(local);
        }
    }

    static bool IsNonNull(BoundExpression value, State state) => value switch
    {
        BoundConversion conversion => IsNonNull(conversion.Operand, state),
        BoundLocalAccess access => !access.Type.MayBeNull || state.IsNonNull(access.Local),
        _ => !value.Type.MayBeNull && value.Type != VplType.Null,
    };

    /// <summary>The locals a call, construction, or 'new' itself passes by 'ref'.</summary>
    static IEnumerable<LocalSymbol> RefArguments(BoundExpression expression) =>
        BoundTree.CallOf(expression) is { } call ? RefArguments(call.Method, call.Arguments) : [];

    static IEnumerable<LocalSymbol> RefArguments(MethodReference method, IReadOnlyList<BoundExpression> arguments) =>
        arguments.Where((a, i) => method.ParameterRefKind(i) == RefKind.Ref).OfType<BoundLocalAccess>().Select(a => a.Local);

    /// <summary>
    /// Analyzes a loop from the state before it. The state at the top of an iteration is the one
    /// before the loop joined with the one where each iteration ends, so the pass repeats, saying
    /// nothing, until it stops changing, and then runs once more to report. Narrowing and what
    /// might be assigned only ever shrink and grow, so it ends.
    /// </summary>
    Iteration Iterate(State start, Func<State, Iteration> pass)
    {
        if (!start.Reachable)
            return pass(start.Clone());
        var entry = start.Clone();
        quiet++;
        while (true)
        {
            var next = State.Join(start, pass(entry.Clone()).Back);
            if (next.SameAs(entry))
                break;
            entry = next;
        }
        quiet--;
        return pass(entry.Clone());
    }

    /// <summary>An iteration's body, where a <c>continue</c> goes back to the top like the end of the body.</summary>
    Iteration Body(BoundStatement body, State entry, State exit)
    {
        var loop = new Loop();
        loops.Push(loop);
        var end = Statement(body, entry);
        loops.Pop();
        return new Iteration(loop, loop.Continues.Aggregate(end, State.Join), exit);
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
                Narrow(nonNull, local);
                return comparison.Operator == BoundBinaryOperator.NotEqual ? (nonNull, state) : (state, nonNull);
            }
            case BoundIs pattern:
            {
                state = Expression(pattern.Operand, state);
                var matched = state.Clone();
                matched.Assigned.UnionWith(Patterns.Bindings(pattern.Pattern));
                if (pattern.Operand is BoundLocalAccess access)
                    Narrow(matched, access.Local);
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
        if (quiet == 0 && reportedAt.Add(key))
            Error(location, message);
    }

    /// <summary>Checks an expression and returns the state after it; the given state may be updated in place.</summary>
    State Expression(BoundExpression expression, State state)
    {
        switch (expression)
        {
            case BoundLocalAccess access:
                if (!state.IsAssigned(access.Local) && quiet == 0 && reported.Add(access.Local))
                    Error(access.Location, $"'{access.Local.Name}' might not have been assigned");
                else if (access.AssumesNonNull && !state.IsNonNull(access.Local) && !access.Local.IsReferenceBinding)
                {
                    var local = access.Local;
                    ReportOnce(access, access.Location, local.Type.IsNullableReference
                        ? $"'{local.Name}' might refer to nothing here; check it with 'if ({local.Name} != null)', or use '!'"
                        : local.RefKind != RefKind.None
                        ? $"'{local.Name}' might be null here: a '{FunctionSymbol.RefPrefix(local.RefKind).Trim()}' {(local.IsParameter ? "parameter" : "variable")} is not narrowed by null checks, since another reference to its variable could change it; copy it to a local first, or use '!'"
                        : state.LambdaAssigned.Contains(local)
                        ? $"'{local.Name}' might be null here: a lambda assigns it, so null checks don't narrow it; copy it to a local first, or use '!'"
                        : narrowedOutside.Contains(local)
                        ? $"'{local.Name}' might be null here: null checks outside a lambda don't narrow inside it; check it inside, or copy it to a local first"
                        : $"'{local.Name}' might be null here; check it with 'if ({local.Name} != null)', or use '?.' or '!'");
                }
                return state;
            case BoundLambda lambda:
                Lambda(lambda, state);
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
            // The expressions with their own cases above are the only ones that don't run every child in order.
            default:
                foreach (var child in BoundTree.Children(expression))
                    state = Expression(child, state);
                Unnarrow(state, RefArguments(expression));
                return state;
        }
    }
}
