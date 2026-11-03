using Wyl.Compiler.Analysis;
using Wyl.Compiler.Ir;

namespace Wyl.Compiler.Optimize;

/// <summary>
/// Reads a place where lowering copied it into a temporary, instead of the temporary, which saves
/// a copy and, for a counted value, a reference count increment and decrement (docs/design/ir.md
/// §5.1). Lowering copies every operand at its turn, and passes every by-value argument as a copy
/// its statement keeps (spec memory.release). The place must be
/// unchanged for as long as the copy matters: until it is moved, or for a kept copy whose release
/// may be observed, until its block releases it, so that the place keeps the same objects alive
/// as long. A kept copy of a temporary whose release runs no code is no copy at all: the argument
/// takes the temporary itself. Run before <see cref="HoldElision"/>, whose <c>Flatten</c> then finds
/// the statements that release nothing observable.
/// </summary>
sealed class ReadsInPlace(Writes writes)
{
    readonly Aliasing aliasing = new(writes);

    public static void Apply(IrProgram program, Writes writes)
    {
        var pass = new ReadsInPlace(writes);
        foreach (var function in program.Functions)
        {
            pass.aliasing.Learn(function);
            // Inner blocks first, so that a block storing a copy has already lost the temporaries it read it through.
            foreach (var block in IrWalk.Descendants(function.Body, intoLambdas: false).OfType<IrBlock>().Reverse().ToList())
                pass.FindIn(block);
        }
    }

    void FindIn(IrBlock block)
    {
        var statements = block.Statements;
        for (int i = 0; i < statements.Count; i++)
        {
            if (Copied(statements, i) is not var (copy, place, defined) || Uses(statements, defined, copy) is not { } uses || !Unchanged(block, defined, copy, place, uses))
                continue;
            foreach (var (_, read) in uses)
            {
                read.Place = new IrPlace(place.Local, place.IsDeref, [.. place.Projections, .. read.Place.Projections]);
                if (read.Ownership == IrOwnership.Move)
                    read.Ownership = IrOwnership.Copy;
            }
            statements.RemoveRange(i, defined - i + 1);
            i--;
        }
        for (int i = 0; i < statements.Count; i++)
        {
            foreach (var read in KeptTemporaries(statements, i))
                read.Ownership = IrOwnership.Move;
        }
    }

    /// <summary>
    /// The by-value arguments of statement <paramref name="at"/> that copy a temporary of the block
    /// whose release runs no code, each the temporary's last mention, and its only one there.
    /// </summary>
    static IEnumerable<IrRead> KeptTemporaries(List<IrStatement> statements, int at) =>
        (IrWalk.Rvalue(statements[at]) is { } value && IrWalk.Passed(value) is { } passed ? passed.Arguments : []).OfType<IrRead>()
            .Where(r => r is { Ownership: IrOwnership.Copy, Place: { IsDeref: false, Projections.Count: 0, Local: { IsTemporary: true, Kind: IrLocalKind.Value } temporary } }
                && !IrEffects.ReleaseMayRunCode(temporary)
                && statements.Take(at).Any(s => IrWalk.Declared(s) == temporary)
                && IrWalk.Locals(statements[at]).Count(l => l == temporary) == 1
                && !statements.Skip(at + 1).Any(s => IrWalk.Locals(s).Contains(temporary)))
            .ToList();

    /// <summary>
    /// The temporary copy of a place that statement <paramref name="at"/> starts, and the index of
    /// the statement defining it: a <c>let</c>, or a <c>declare</c> stored by the block right after
    /// it, which then does nothing else, as a base constructor call's argument is.
    /// </summary>
    static (IrLocal Copy, IrPlace Place, int Defined)? Copied(List<IrStatement> statements, int at) => statements[at] switch
    {
        IrLet { Local: { IsTemporary: true, Kind: IrLocalKind.Value or IrLocalKind.Reference } copy, Value: IrUse { Operand: IrRead { Ownership: IrOwnership.Copy, Place: var place } } } =>
            (copy, place, at),
        IrDeclare { Local: { IsTemporary: true, Kind: IrLocalKind.Value } copy } when at + 1 < statements.Count
            && statements[at + 1] is IrBlock { Statements: [IrStore { Target: { IsDeref: false, Projections.Count: 0 } target, Value: IrRead { Ownership: IrOwnership.Copy, Place: var place } }] }
            && target.Local == copy =>
            (copy, place, at + 1),
        _ => null,
    };

    /// <summary>
    /// The reads of the copy, or of a part of it, each an operand of a statement after it in its
    /// block, or null if it is named anywhere else, or read after or beside a move of it.
    /// </summary>
    static List<(int Index, IrRead Read)>? Uses(List<IrStatement> statements, int from, IrLocal copy)
    {
        var uses = new List<(int, IrRead)>();
        bool moved = false;
        for (int j = from + 1; j < statements.Count; j++)
        {
            int named = IrWalk.Locals(statements[j]).Count(l => l == copy);
            if (named == 0)
                continue;
            var reads = IrWalk.Operands(statements[j]).OfType<IrRead>().Where(r => !r.Place.IsDeref && r.Place.Local == copy).ToList();
            if (moved || reads.Count != named || (reads.Count > 1 && reads.Any(r => r.Ownership == IrOwnership.Move)))
                return null;
            moved = reads.Any(r => r.Ownership == IrOwnership.Move);
            uses.AddRange(reads.Select(r => (j, r)));
        }
        return uses.Count == 0 ? null : uses;
    }

    /// <summary>
    /// Whether nothing may change the place, or what the aliases it goes through borrow, while the
    /// copy matters: until it is moved whole, or if what is left of it is released when its block
    /// ends, which may be observed, until then. A copy or a move is read before its instruction
    /// runs; a borrow is in use while it does.
    /// </summary>
    bool Unchanged(IrBlock block, int at, IrLocal copy, IrPlace place, List<(int Index, IrRead Read)> uses)
    {
        var statements = block.Statements;
        var (last, read) = uses[^1];
        bool kept = read is not { Ownership: IrOwnership.Move, Place.Projections.Count: 0 } && IrEffects.ReleaseMayRunCode(copy);
        int end = kept ? statements.Count : uses.Any(u => u.Index == last && u.Read.Ownership == IrOwnership.Borrow) ? last + 1 : last;
        var locals = IrWalk.PlaceLocals(place).ToHashSet();
        var footprint = new IrFootprint();
        for (int j = at + 1; j < end; j++)
        {
            if (IrWalk.Descendants(statements[j]).Any(s => Ends(s, locals)))
                return false;
            footprint.Add(IrWalk.Footprint(statements[j]));
        }
        if (kept)
            footprint.Add(IrEffects.Ending(block, except: copy));
        return !aliasing.MayChange(place, footprint);
    }

    /// <summary>Whether the statement ends or moves out one of the locals, after which a place naming it isn't valid.</summary>
    static bool Ends(IrStatement statement, HashSet<IrLocal> locals) =>
        statement is IrEnd end && locals.Contains(end.Local)
        || IrWalk.Operands(statement).OfType<IrRead>().Any(r => r.Ownership == IrOwnership.Move && locals.Contains(r.Place.Local));
}
