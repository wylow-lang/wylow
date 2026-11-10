using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Analysis;

public sealed partial class IrVerifier
{
    /// <summary>
    /// A non-escaping lambda's body runs where it is called, during the code that made it, so it
    /// sees that code's locals. What is assigned where it is made stays assigned, but narrowing
    /// is not kept, since the code before it runs could undo it, except a <c>ref?</c> local's,
    /// which nothing can make refer elsewhere.
    /// </summary>
    void Lambda(IrFunction lambda, State state)
    {
        RecordLambda(lambda);
        var (savedFunction, savedLoops, savedCurrent, savedMoves, savedNarrowed) = (function, loops, current, pendingMoves.ToList(), narrowedOutside);
        function = lambda;
        loops = [];
        narrowedOutside = NarrowedAround(state);
        // It may run any number of times, as a loop's body does.
        loopDepth++;
        scopes.Add([]);
        var savedOutside = capturedOutside;
        capturedOutside = [.. capturedOutside, .. declarationOrder.Skip(rootDeclarations)];
        var inner = state.Clone();
        inner.NonNull.RemoveWhere(l => !l.Type.IsNullableReference);
        foreach (var parameter in Entry(lambda))
            DeclareParameter(parameter, inner);
        inner = Block(lambda.Body, inner, newScope: false);
        if (inner.Reachable && lambda.Symbol.ReturnType != WylType.Void)
            MissingReturn(lambda);
        scopes.RemoveAt(scopes.Count - 1);
        loopDepth--;
        capturedOutside = savedOutside;
        (function, loops, current, narrowedOutside) = (savedFunction, savedLoops, savedCurrent, savedNarrowed);
        pendingMoves.Clear();
        pendingMoves.AddRange(savedMoves);
    }

    /// <summary>An escaping lambda's body sees only its parameters and the copies its closure holds.</summary>
    /// <param name="capturedThis">In a constructor, the copy of what it makes, which the body may use as the constructor may where it is made.</param>
    void Closure(IrFunction lambda, State state, IrLocal? capturedThis)
    {
        RecordLambda(lambda);
        var saved = (function, loops, scopes, loopDepth, current, moves: pendingMoves.ToList(), narrowedOutside, constructed);
        narrowedOutside = NarrowedAround(state);
        constructed = capturedThis;
        var savedOutside = capturedOutside;
        capturedOutside = [.. capturedOutside, .. lambda.Captures];
        VerifyFunction(lambda, root, capturedThis is null ? [] : state.AssignedFields);
        capturedOutside = savedOutside;
        (function, loops, scopes, loopDepth, current, _, narrowedOutside, constructed) = saved;
        pendingMoves.Clear();
        pendingMoves.AddRange(saved.moves);
    }

    /// <summary>The source locals narrowed where a lambda is made, for explaining why they aren't in its body.</summary>
    HashSet<LocalSymbol> NarrowedAround(State state) => [.. narrowedOutside, .. state.NonNull.Select(l => l.Symbol).OfType<LocalSymbol>()];

    /// <summary>The origin of the first statement of a lambda's body that uses the local.</summary>
    static Location? FirstUse(IrFunction lambda, IrLocal local) =>
        IrWalk.Descendants(lambda.Body).FirstOrDefault(s => IrWalk.Operands(s).SelectMany(IrWalk.Places).SelectMany(IrWalk.PlaceLocals).Contains(local))?.Origin;
}
