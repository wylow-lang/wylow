using Wyl.Compiler.Analysis;
using Wyl.Compiler.Ir;

namespace Wyl.Compiler.Optimize;

/// <summary>
/// What each borrowed local is an alias of, so that a place through an alias depends on the
/// storage the alias refers to as well as its own.
/// </summary>
sealed class Aliasing(Writes writes)
{
    readonly Dictionary<IrLocal, IrPlace> aliases = [];

    /// <summary>Records the borrowed locals a function declares as borrows of a place.</summary>
    public void Learn(IrFunction function)
    {
        foreach (var statement in IrWalk.Descendants(function.Body, intoLambdas: false))
        {
            if (statement is IrLet { Local.Kind: IrLocalKind.Borrowed, Value: IrUse { Operand: IrRead { Ownership: IrOwnership.Borrow, Place: var place } } } let)
                aliases[let.Local] = place;
        }
    }

    public void Add(IrLocal local, IrPlace place) => aliases[local] = place;

    /// <summary>Whether what the footprint does may change the place, or what the aliases it goes through borrow.</summary>
    public bool MayChange(IrPlace place, IrFootprint footprint)
    {
        var chain = Chain(place).ToList();
        return chain.Any(a => footprint.Writes.Any(a.Overlaps) || writes.CodeMayWrite(a, footprint.Runs));
    }

    /// <summary>The accesses a place depends on: its own, and those of the places the aliases it goes through borrow.</summary>
    IEnumerable<IrAccess> Chain(IrPlace place)
    {
        foreach (var access in IrAccess.Of(place))
            yield return access;
        if (aliases.TryGetValue(place.Local, out var aliased))
        {
            foreach (var access in Chain(aliased))
                yield return access;
        }
    }
}
