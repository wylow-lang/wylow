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

    /// <summary>
    /// Locals a lambda in the function assigns. A null check can't narrow them: the lambda may run
    /// during any call after it is created.
    /// </summary>
    HashSet<LocalSymbol> lambdaAssigned = [];

    int lambdaDepth;

    /// <summary>Inside a lambda, the locals narrowed where it was created, for explaining why they aren't inside.</summary>
    HashSet<LocalSymbol> narrowedOutside = [];

    public void Analyze(BoundFunction function)
    {
        var symbol = function.Symbol;
        // The implicit constructor of a class with such fields is reported by the binder.
        requiredFields = symbol.IsConstructor && !symbol.IsImplicit ? [.. symbol.ContainingType!.Fields.Where(f => f.RequiresAssignment)] : [];
        lambdaAssigned = [.. LambdasIn(function.Body).SelectMany(l => AssignedLocals(l.Body))];

        var state = new State();
        state.Assigned.UnionWith(symbol.Parameters);
        state = Statement(function.Body, state);

        var end = EndOf(function.Body.Location);
        var returnType = symbol.ReturnType;
        if (state.Reachable && !symbol.IsConstructor && returnType != VplType.Void && returnType != VplType.Error)
            diagnostics.Error(end, $"'{symbol.Name}' must return a value on every path");
        if (state.Reachable)
            CheckFieldsAssigned(state, end);
        requiredFields = [];
    }

    static Location EndOf(Location body) =>
        new(body.Source, new TextSpan(Math.Max(body.Span.End - 1, body.Span.Start), body.Span.Length > 0 ? 1 : 0));

    /// <summary>
    /// A lambda runs only after it is created, so what is assigned there stays assigned. Narrowing
    /// is not trusted inside it, since the code before it runs could undo it.
    /// </summary>
    void Lambda(BoundLambda lambda, State outer)
    {
        var state = new State { Reachable = outer.Reachable, Assigned = [.. outer.Assigned], AssignedFields = [.. outer.AssignedFields] };
        state.Assigned.UnionWith(lambda.Function.Parameters);
        var enclosingNarrowed = narrowedOutside;
        narrowedOutside = [.. enclosingNarrowed, .. outer.NonNull];
        lambdaDepth++;
        state = Statement(lambda.Body, state);
        lambdaDepth--;
        narrowedOutside = enclosingNarrowed;
        var returnType = lambda.Function.ReturnType;
        if (state.Reachable && returnType != VplType.Void && returnType != VplType.Error)
            diagnostics.Error(EndOf(lambda.Body.Location), "this lambda must return a value on every path");
    }

    static IEnumerable<BoundLambda> LambdasIn(BoundStatement statement) =>
        OwnExpressions(statement).SelectMany(LambdasIn).Concat(InnerStatements(statement).SelectMany(LambdasIn));

    static IEnumerable<BoundLambda> LambdasIn(BoundExpression expression) => expression is BoundLambda lambda
        ? [lambda, .. LambdasIn(lambda.Body)]
        : BoundTree.Children(expression).SelectMany(LambdasIn);

    static IEnumerable<BoundStatement> InnerStatements(BoundStatement statement) => statement switch
    {
        BoundBlock block => block.Statements,
        BoundIf ifStatement => ifStatement.Else is null ? [ifStatement.Then] : [ifStatement.Then, ifStatement.Else],
        BoundWhile whileStatement => [whileStatement.Body],
        BoundFor forStatement => [.. new[] { forStatement.Initializer, forStatement.Update }.OfType<BoundStatement>(), forStatement.Body],
        BoundForRange range => [range.Body],
        BoundSwitch switchStatement => switchStatement.Cases.Select(c => c.Body),
        _ => [],
    };

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
                // Fixed arrays are often too large to initialize element by element.
                else if (declaration.Local.Type.IsFixedArray && Conversions.IsDefaultable(declaration.Local.Type))
                {
                    state.Assigned.Add(declaration.Local);
                }
                return state;

            case BoundAssignment assignment:
            {
                // Assigning a whole local initializes it; assigning a field requires its root to be
                // initialized. Like evaluation, analysis finds the target before the value.
                bool initializesField = assignment.Target is BoundFieldAccess { Receiver: BoundThis } && requiredFields.Count > 0;
                if (assignment.Target is not BoundLocalAccess && !initializesField)
                {
                    CheckNullableValueKept(assignment.Target, RefPassedLocals(assignment.Value));
                    state = Expression(assignment.Target, state);
                }
                state = Expression(assignment.Value, state);
                if (assignment.Target is BoundLocalAccess target)
                {
                    state.Assigned.Add(target.Local);
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
                foreach (var local in RefArguments(super.Constructor, super.Arguments))
                    state.NonNull.Remove(local);
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

            case BoundForRange range:
            {
                state = Expression(range.End, Expression(range.Start, state));
                var entry = LoopEntry(state, range.Body);
                var body = entry.Clone();
                body.Assigned.Add(range.Local);
                var loop = AnalyzeLoopBody(range.Body, body);
                return loop.Breaks.Aggregate(entry, State.Join);
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

    void SetNullability(State state, LocalSymbol local, BoundExpression value)
    {
        if (IsNonNull(value, state))
            Narrow(state, local);
        else
            state.NonNull.Remove(local);
    }

    /// <summary>
    /// Only a variable that nothing else can assign can be narrowed: through another reference to
    /// the same variable, a 'ref' or 'in' one could become null after the check, and so could one
    /// a lambda assigns.
    /// </summary>
    void Narrow(State state, LocalSymbol local)
    {
        if (local.RefKind == RefKind.None && !lambdaAssigned.Contains(local))
            state.NonNull.Add(local);
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

    /// <summary>The locals a statement may assign, directly or by passing them by 'ref'.</summary>
    static IEnumerable<LocalSymbol> AssignedLocals(BoundStatement statement) =>
        OwnExpressions(statement).SelectMany(RefPassedLocals).Concat(DirectlyAssignedLocals(statement));

    static IEnumerable<LocalSymbol> DirectlyAssignedLocals(BoundStatement statement) => statement switch
    {
        BoundBlock block => block.Statements.SelectMany(AssignedLocals),
        BoundAssignment { Target: BoundLocalAccess target } => [target.Local],
        BoundSuperCall super => RefArguments(super.Constructor, super.Arguments),
        BoundIf ifStatement => [.. AssignedLocals(ifStatement.Then), .. ifStatement.Else is null ? [] : AssignedLocals(ifStatement.Else)],
        BoundWhile whileStatement => AssignedLocals(whileStatement.Body),
        BoundForRange range => AssignedLocals(range.Body),
        BoundSwitch switchStatement => switchStatement.Cases.SelectMany(c => AssignedLocals(c.Body)),
        BoundFor forStatement => [
            .. forStatement.Initializer is null ? [] : AssignedLocals(forStatement.Initializer),
            .. AssignedLocals(forStatement.Body),
            .. forStatement.Update is null ? [] : AssignedLocals(forStatement.Update),
        ],
        _ => [],
    };

    /// <summary>The expressions a statement evaluates itself, not those of the statements inside it.</summary>
    static IEnumerable<BoundExpression> OwnExpressions(BoundStatement statement) => statement switch
    {
        BoundVariableDeclaration { Initializer: { } initializer } => [initializer],
        BoundAssignment assignment => [assignment.Target, assignment.Value],
        BoundExpressionStatement expressionStatement => [expressionStatement.Expression],
        BoundSuperCall super => super.Arguments,
        BoundIf ifStatement => [ifStatement.Condition],
        BoundWhile whileStatement => [whileStatement.Condition],
        BoundFor { Condition: { } condition } => [condition],
        BoundForRange range => [range.Start, range.End],
        BoundReturn { Value: { } value } => [value],
        BoundSwitch switchStatement => [switchStatement.Value],
        _ => [],
    };

    static IEnumerable<LocalSymbol> RefPassedLocals(BoundExpression expression) =>
        RefArguments(expression).Concat(BoundTree.Children(expression).SelectMany(RefPassedLocals));

    /// <summary>The locals a call, construction, or 'new' itself passes by 'ref'.</summary>
    static IEnumerable<LocalSymbol> RefArguments(BoundExpression expression) => expression switch
    {
        BoundCall call => RefArguments(call.Method, call.Arguments),
        BoundInvocation invocation => RefArguments(invocation.Signature, invocation.Arguments),
        BoundNew creation => RefArguments(creation.Constructor, creation.Arguments),
        BoundConstruction construction => RefArguments(construction.Constructor, construction.Arguments),
        _ => [],
    };

    static IEnumerable<LocalSymbol> RefArguments(MethodReference method, IReadOnlyList<BoundExpression> arguments) =>
        arguments.Where((a, i) => method.ParameterRefKind(i) == RefKind.Ref).OfType<BoundLocalAccess>().Select(a => a.Local);

    /// <summary>
    /// A place inside a narrowed nullable value is referred to while the rest of an operation runs,
    /// as the receiver or a 'ref' or 'in' argument of a call, or the target of an assignment.
    /// Passing the nullable by 'ref' meanwhile could set it to null and destroy the place.
    /// </summary>
    void CheckNullableValueKept(BoundExpression place, IEnumerable<LocalSymbol> passedByRef)
    {
        var root = place.Type.IsRefStruct ? BoundTree.ViewedNullableValue(place) : BoundTree.NullableValueRoot(place);
        if (root is not null && passedByRef.Contains(root))
        {
            ReportOnce(place, place.Location,
                $"'{root.Name}' is passed by 'ref' while its value is in use here, so it could become null and destroy that value; copy the value to a variable first");
        }
    }

    /// <summary>Checks the operands of a call or index that refer to places against the others evaluated with them.</summary>
    void CheckNullableValuesKept(BoundExpression expression)
    {
        var (method, receiver, arguments) = expression switch
        {
            BoundCall call => (call.Method, call.Receiver, call.Arguments),
            BoundInvocation invocation => (invocation.Signature, null, invocation.Arguments),
            BoundNew creation => (creation.Constructor, null, creation.Arguments),
            BoundConstruction construction => (construction.Constructor, null, construction.Arguments),
            BoundIndex { Target.Type.IsFixedArray: true } index => (null, index.Target, [index.Index]),
            _ => ((MethodReference?)null, (BoundExpression?)null, (IReadOnlyList<BoundExpression>)[]),
        };
        List<BoundExpression> operands = receiver is null ? [.. arguments] : [receiver, .. arguments];
        for (int i = 0; i < operands.Count; i++)
        {
            int argument = receiver is null ? i : i - 1;
            bool kept = argument < 0
                ? !operands[i].Type.IsReference
                : method is not null && (operands[i].Type.IsRefStruct || method.ParameterRefKind(argument) != RefKind.None);
            if (kept)
                CheckNullableValueKept(operands[i], RefArguments(expression).Concat(operands.Where((_, j) => j != i).SelectMany(RefPassedLocals)));
        }
    }

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
                    var local = access.Local;
                    ReportOnce(access, access.Location, local.RefKind != RefKind.None
                        ? $"'{local.Name}' might be null here: a '{FunctionSymbol.RefPrefix(local.RefKind).Trim()}' {(local.IsParameter ? "parameter" : "variable")} is not narrowed by null checks, since another reference to its variable could change it; copy it to a local first, or use '!'"
                        : lambdaAssigned.Contains(local)
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
            default:
                CheckNullableValuesKept(expression);
                foreach (var child in Children(expression))
                    state = Expression(child, state);
                foreach (var local in RefArguments(expression))
                    state.NonNull.Remove(local);
                return state;
        }
    }

    static IEnumerable<BoundExpression> Children(BoundExpression expression) => expression switch
    {
        BoundUnary unary => [unary.Operand],
        BoundBinary binary => [binary.Left, binary.Right],
        BoundCall call => call.Receiver is null ? call.Arguments : [call.Receiver, .. call.Arguments],
        BoundInvocation invocation => [invocation.Callee, .. invocation.Arguments],
        BoundConstruction construction => [.. construction.Arguments, .. construction.Initializers.Select(i => i.Value)],
        BoundNew creation => creation.Arguments,
        BoundFieldAccess field => [field.Receiver],
        BoundIndex index => [index.Target, index.Index],
        BoundArrayLength length => [length.Operand],
        BoundArrayData data => [data.Array],
        BoundArrayLiteral literal => literal.Elements,
        BoundConversion conversion => [conversion.Operand],
        BoundNonNull nonNull => [nonNull.Operand],
        BoundVariant variant => variant.Arguments,
        BoundTry attempt => [attempt.Operand],
        _ => [],
    };
}
