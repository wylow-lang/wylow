using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

/// <summary>Definite assignment and reachability: no read of an unassigned local, no falling off a non-void function.</summary>
public sealed class FlowAnalyzer(DiagnosticBag diagnostics)
{
    sealed class State
    {
        public bool Reachable = true;
        public HashSet<LocalSymbol> Assigned = [];

        public static State Unreachable() => new() { Reachable = false };

        public State Clone() => new() { Reachable = Reachable, Assigned = [.. Assigned] };

        // Unreachable code may assume anything is assigned.
        public bool IsAssigned(LocalSymbol local) => !Reachable || Assigned.Contains(local);

        public static State Join(State a, State b)
        {
            if (!a.Reachable)
                return b.Clone();
            if (!b.Reachable)
                return a.Clone();
            var result = a.Clone();
            result.Assigned.IntersectWith(b.Assigned);
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

    public void Analyze(BoundFunction function)
    {
        var state = new State();
        state.Assigned.UnionWith(function.Symbol.Parameters);
        state = Statement(function.Body, state);

        var returnType = function.Symbol.ReturnType;
        if (state.Reachable && !function.Symbol.IsConstructor && returnType != VplType.Void && returnType != VplType.Error)
        {
            var body = function.Body.Location;
            diagnostics.Error(
                new Location(body.Source, new TextSpan(body.Span.End - 1, 1)),
                $"'{function.Symbol.Name}' must return a value on every path");
        }
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
                    Expression(declaration.Initializer, state);
                    state.Assigned.Add(declaration.Local);
                }
                return state;

            case BoundAssignment assignment:
                Expression(assignment.Value, state);
                // Assigning a whole local initializes it; assigning a field requires its root to be initialized.
                if (assignment.Target is BoundLocalAccess target)
                    state.Assigned.Add(target.Local);
                else
                    Expression(assignment.Target, state);
                return state;

            case BoundExpressionStatement expressionStatement:
                Expression(expressionStatement.Expression, state);
                return expressionStatement.Expression is BoundCall { Method.Symbol.IsNoReturn: true } ? State.Unreachable() : state;

            case BoundIf ifStatement:
            {
                Expression(ifStatement.Condition, state);
                var afterThen = Statement(ifStatement.Then, state.Clone());
                var afterElse = ifStatement.Else is null ? state : Statement(ifStatement.Else, state.Clone());
                return State.Join(afterThen, afterElse);
            }

            case BoundWhile whileStatement:
            {
                Expression(whileStatement.Condition, state);
                var loop = AnalyzeLoopBody(whileStatement.Body, state);
                var exit = IsConstantTrue(whileStatement.Condition) ? State.Unreachable() : state;
                return loop.Breaks.Aggregate(exit, State.Join);
            }

            case BoundFor forStatement:
            {
                if (forStatement.Initializer is not null)
                    state = Statement(forStatement.Initializer, state);
                if (forStatement.Condition is not null)
                    Expression(forStatement.Condition, state);

                var loop = new Loop();
                loops.Push(loop);
                var endOfBody = Statement(forStatement.Body, state.Clone());
                loops.Pop();
                if (forStatement.Update is not null)
                    Statement(forStatement.Update, loop.Continues.Aggregate(endOfBody, State.Join));

                bool infinite = forStatement.Condition is null || IsConstantTrue(forStatement.Condition);
                return loop.Breaks.Aggregate(infinite ? State.Unreachable() : state, State.Join);
            }

            case BoundReturn returnStatement:
                if (returnStatement.Value is not null)
                    Expression(returnStatement.Value, state);
                return State.Unreachable();

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

    Loop AnalyzeLoopBody(BoundStatement body, State entry)
    {
        // Analyzing the body once from the entry state is sound: assignments only ever add to the
        // set, so every later iteration starts with at least what the first one had.
        var loop = new Loop();
        loops.Push(loop);
        Statement(body, entry.Clone());
        loops.Pop();
        return loop;
    }

    static bool IsConstantTrue(BoundExpression expression) => expression is BoundLiteral { Value: true };

    void Expression(BoundExpression expression, State state)
    {
        switch (expression)
        {
            case BoundLocalAccess access:
                if (!state.IsAssigned(access.Local) && reported.Add(access.Local))
                    diagnostics.Error(access.Location, $"'{access.Local.Name}' might not have been assigned");
                break;
            case BoundUnary unary:
                Expression(unary.Operand, state);
                break;
            case BoundBinary binary:
                Expression(binary.Left, state);
                Expression(binary.Right, state);
                break;
            case BoundCall call:
                if (call.Receiver is not null)
                    Expression(call.Receiver, state);
                foreach (var argument in call.Arguments)
                    Expression(argument, state);
                break;
            case BoundConstruction construction:
                foreach (var argument in construction.Arguments)
                    Expression(argument, state);
                foreach (var initializer in construction.Initializers)
                    Expression(initializer.Value, state);
                break;
            case BoundFieldAccess field:
                Expression(field.Receiver, state);
                break;
            case BoundConversion conversion:
                Expression(conversion.Operand, state);
                break;
            case BoundConditional conditional:
                Expression(conditional.Condition, state);
                Expression(conditional.WhenTrue, state);
                Expression(conditional.WhenFalse, state);
                break;
        }
    }
}
