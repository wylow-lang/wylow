using Vpl.Compiler.Analysis;
using Vpl.Compiler.Ir;

namespace Vpl.Compiler.Optimize;

/// <summary>
/// Borrows the place a named reference local is copied from, instead of copying it, which saves a
/// reference count increment and decrement (docs/design/ir.md §6.3). The copy keeps the object
/// alive until the local's scope ends, so nothing from the next statement to the block's end,
/// including the releases there, may change the place, or what the aliases it goes through borrow.
/// Loop elements are <see cref="InPlaceElements"/>'s. Run after <see cref="HoldElision"/>, whose
/// holds the place may go through.
/// </summary>
sealed class BorrowedLocals(IrProgram program, Writes writes)
{
    readonly Aliasing aliasing = new(writes);
    int count;

    /// <summary>How many locals were turned into borrows.</summary>
    public int Count => count;

    public static BorrowedLocals Apply(IrProgram program, Writes writes)
    {
        var result = new BorrowedLocals(program, writes);
        foreach (var function in program.Functions)
        {
            result.aliasing.Learn(function);
            foreach (var block in IrWalk.Descendants(function.Body, intoLambdas: false).OfType<IrBlock>())
                result.FindIn(block);
        }
        return result;
    }

    void FindIn(IrBlock block)
    {
        var statements = block.Statements;
        for (int i = 0; i < statements.Count; i++)
        {
            if (statements[i] is not IrLet { Value: IrUse { Operand: IrRead { Ownership: IrOwnership.Copy, Place: var place } } } let || !Candidate(let.Local, place))
                continue;
            var local = let.Local;
            local.Kind = IrLocalKind.Borrowed;
            if (Unchanged(local, place, block, i))
            {
                let.Value = new IrUse(IrRead.Borrow(place));
                aliasing.Add(local, place);
                count++;
            }
            else
            {
                local.Kind = IrLocalKind.Value;
            }
        }
    }

    bool Candidate(IrLocal local, IrPlace place) =>
        local.Kind == IrLocalKind.Value && local.Symbol is not null && !local.IsParameter && !local.IsCapture && !local.IsThis
        && !local.IsChangedByLambda && local.LoopElement is null
        && local.Type.NonNull.IsReference && !local.Type.NonNull.IsTypeParameter && place.Type == local.Type
        && !(place.Local.IsTemporary && place.Local.Kind == IrLocalKind.Value);

    /// <summary>With the local already marked borrowed, so that its own release counts for nothing.</summary>
    bool Unchanged(IrLocal local, IrPlace place, IrBlock block, int at)
    {
        var footprint = new IrFootprint();
        footprint.Add(IrEffects.Ending(block));
        foreach (var statement in block.Statements.Skip(at + 1))
        {
            if (IrWalk.Descendants(statement).Any(s => InPlaceElements.Changes(s, local) || Moves(s, local, place.Local)))
                return false;
            footprint.Add(IrWalk.Footprint(statement));
        }
        return !aliasing.MayChange(place, footprint);
    }

    /// <summary>Whether the statement moves either local out, which a borrow doesn't survive.</summary>
    static bool Moves(IrStatement statement, IrLocal local, IrLocal source) =>
        IrWalk.Operands(statement).OfType<IrRead>().Any(r => r.Ownership == IrOwnership.Move && (r.Place.Local == local || r.Place.Local == source));
}
