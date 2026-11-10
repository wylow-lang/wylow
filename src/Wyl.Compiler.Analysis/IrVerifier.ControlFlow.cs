using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Analysis;

public sealed partial class IrVerifier
{
    State If(IrIf conditional, State state)
    {
        pendingMoves.Clear();
        Operand(conditional.Condition, state);
        CommitMoves(state);
        if (conditional.Condition.Type != WylType.Bool)
            Error($"the condition is a '{conditional.Condition.Type}', not a 'bool'");
        var outcome = conditional.Condition is IrRead { Place: { IsDeref: false, Projections.Count: 0, Local: var condition } } && state.Outcomes.TryGetValue(condition, out var known) ? known : null;
        var thenState = state.Clone();
        var elseState = state.Clone();
        if (outcome?.WhenTrue is { } whenTrue)
        {
            thenState.Assigned.UnionWith(whenTrue.Assigned);
            thenState.NonNull.UnionWith(whenTrue.NonNull);
            thenState.TakeMoved(whenTrue.Moved);
        }
        if (outcome?.WhenFalse is { } whenFalse)
        {
            elseState.Assigned.UnionWith(whenFalse.Assigned);
            elseState.NonNull.UnionWith(whenFalse.NonNull);
            elseState.TakeMoved(whenFalse.Moved);
        }
        var then = Block(conditional.Then, thenState);
        // Where a reference's null test failed, nothing it points into needs to stay held.
        var isNull = conditional.Condition is IrRead { Place: { IsDeref: false, Projections.Count: 0, Local: var tested } } && nullTests.TryGetValue(tested, out var reference) ? reference : null;
        if (isNull is not null)
            knownNull.Add(isNull);
        var otherwise = conditional.Else is { } elseBlock ? Block(elseBlock, elseState) : elseState;
        if (isNull is not null)
            knownNull.Remove(isNull);
        return State.Merge(then, otherwise);
    }

    const int MaxLoopPasses = 1000;

    /// <summary>Runs the body until what holds at its start stops changing, so that what a later iteration sees is checked too.</summary>
    State Loop(IrLoop loop, State state)
    {
        var context = new LoopContext(loop);
        loops.Add(context);
        loopDepth++;
        var entry = state.Clone();
        int declaredBefore = declarationOrder.Count;
        int lambdasBefore = lambdaOrder.Count;
        for (int pass = 0; ; pass++)
        {
            // A later pass declares the body's locals and makes its lambdas again.
            foreach (var local in declarationOrder.Skip(declaredBefore))
                declarations.Remove(local);
            declarationOrder.RemoveRange(declaredBefore, declarationOrder.Count - declaredBefore);
            foreach (var lambda in lambdaOrder.Skip(lambdasBefore))
                verifiedLambdas.Remove(lambda);
            lambdaOrder.RemoveRange(lambdasBefore, lambdaOrder.Count - lambdasBefore);
            context.Breaks.Clear();
            context.Continues.Clear();
            var end = Block(loop.Body, entry.Clone());
            end = context.Continues.Aggregate(end, State.Merge);
            if (loop.Continue is { } next && end.Reachable)
            {
                context.InContinue = true;
                end = Block(next, end);
                context.InContinue = false;
            }
            var merged = State.Merge(entry.Clone(), end);
            if (merged.SameAs(entry))
                break;
            // Each pass only adds what may hold and removes what holds on every path, so the
            // passes settle. A fact may need a pass for each local it is copied through.
            if (pass >= MaxLoopPasses)
            {
                Error("the loop's flow does not settle");
                break;
            }
            entry = merged;
        }
        loopDepth--;
        loops.RemoveAt(loops.Count - 1);
        return context.Breaks.Aggregate(State.Unreachable, State.Merge);
    }

    void Return(IrReturn ret, State state)
    {
        var returnType = ResultType(function);
        // A constructor's own return, which isn't written, is at the end of its body.
        if (constructed is not null && !function.IsLambda && (ret.Origin ?? (function.Body.Origin is { } body ? EndOf(body) : null)) is { } returned)
            CheckFieldsAssigned(state, returned);
        if (ret.Value is null)
        {
            if (returnType != WylType.Void)
                Error($"returns nothing from a function returning '{returnType}'");
            return;
        }
        pendingMoves.Clear();
        bool returnsMade = function.Struct is { } made && ret.Value is IrRead { Place: { IsDeref: false, Projections.Count: 0 } place } && place.Local == made;
        // Returning what a struct constructor makes isn't a use of it.
        var making = constructed;
        if (returnsMade)
            constructed = null;
        Operand(ret.Value, state);
        constructed = making;
        pendingMoves.Clear();
        if (ret.Holds.Count > 0 && !function.Symbol.ReturnsHold)
            Error("gives holds from a method that returns none");
        foreach (var hold in ret.Holds)
        {
            // A pass may name an elided hold by the local it aliases, which holds the same object.
            if (hold is not { IsDeref: false, Projections.Count: 0 } || hold.Local.Kind is IrLocalKind.Reference or IrLocalKind.Slot or IrLocalKind.InUse)
                Error($"gives '{N(hold.Local)}', which is not a hold, a holder, or what a hold refers to");
            Operand(IrRead.Borrow(hold), state);
        }
        pendingMoves.Clear();
        if (function.Struct is not null && !returnsMade)
            Error("a struct constructor returns its value");
        if (returnType == WylType.Void)
            Error("returns a value from a function returning nothing");
        else if (ret.Value.Type != returnType)
            Error($"returns a '{ret.Value.Type}' from a function returning '{returnType}'");
        bool reference = function.Symbol.ReturnRefKind != RefKind.None;
        if (reference && ret.Value is not (IrConstant { Value: null } or IrRead { Place: { IsDeref: false, Projections.Count: 0, Local.Kind: IrLocalKind.Reference } }))
            Error("a function returning a reference returns a reference local");
        if (ret.Value is IrRead { Ownership: IrOwnership.Borrow })
            Error("a return needs a value of its own: copy or move it");
    }
}
