using Vpl.Compiler.Ir;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Lowering;

public sealed partial class IrLowering
{
    /// <summary>How long what a place lies in must stay put.</summary>
    enum Use
    {
        /// <summary>Its value is read once, right away.</summary>
        Read,
        /// <summary>It is used while an operation runs: a receiver, a <c>ref</c> argument, an assignment's target.</summary>
        InUse,
        /// <summary>A reference or view to it lives for a local's scope.</summary>
        Scope,
    }

    /// <summary>Targets already found, which a compound assignment's value reads instead of finding them again.</summary>
    readonly Dictionary<BoundExpression, IrPlace> located = new(ReferenceEqualityComparer.Instance);

    /// <summary>What each borrowed local is an alias of: the value a <c>?.</c> on a nullable value binds.</summary>
    readonly Dictionary<IrLocal, IrPlace> aliases = [];

    /// <summary>
    /// A read left in its instruction, unless something lowered between the two may change what it
    /// reads; then it is copied where it was lowered, from <paramref name="Origin"/>.
    /// </summary>
    sealed record PendingRead(IrRead Read, IrBlock Block, int Index, Location? Origin);

    readonly List<PendingRead> pendingReads = [];

    static bool IsPlace(BoundExpression expression) => expression switch
    {
        BoundLocalAccess or BoundFieldAccess or BoundIndex => true,
        BoundThis self => !self.Type.IsReference,
        BoundCall { Method.Symbol.ReturnRefKind: not RefKind.None } => true,
        BoundNonNull { Operand.Type.IsNullableReference: true } => true,
        _ => false,
    };

    /// <summary>
    /// A place, decomposed by <see cref="PlacePath"/>: the objects it goes through are each read
    /// into a local, so that the place dereferences only at its base, and its indices are checked.
    /// </summary>
    IrPlace Place(BoundExpression expression, Use use)
    {
        if (located.TryGetValue(expression, out var found))
            return found;
        var path = PlacePath.Of(expression);
        IrPlace place;
        int first = 0;
        switch (path.Base)
        {
            case PlaceBase.Local:
                place = LocalPlace((BoundLocalAccess)path.Root);
                break;
            case PlaceBase.This:
                place = new IrPlace(self!);
                break;
            case PlaceBase.Object:
            {
                var crossing = path.Steps[0];
                // A value read right away is read before any code runs that could move it.
                bool borrows = crossing is BoundCall && MayBeBorrowable(path.Root.Type) && use != Use.Read;
                var holder = ObjectLocal(path.Root, use, borrows);
                place = crossing switch
                {
                    BoundFieldAccess field => IrPlace.Deref(holder).Field(field.Field),
                    BoundCall call => Returned(call, IrRead.Borrow(new IrPlace(holder))),
                    _ => throw new InvalidOperationException($"unexpected step {crossing.GetType().Name}"),
                };
                first = 1;
                break;
            }
            case PlaceBase.Pointer:
            {
                var index = (BoundIndex)path.Steps[0];
                var pointer = path.Root is BoundLocalAccess { Local: var local } && !local.Type.IsNullableReference ? locals[local] : Let(Rvalue(path.Root, Use.Read));
                place = IrPlace.Deref(pointer).Element(IndexOperand(index.Index));
                first = 1;
                break;
            }
            case PlaceBase.Returned:
                throw new InvalidOperationException("only methods return references");
            default:
                place = new IrPlace(Let(Rvalue(path.Root, use)));
                break;
        }
        for (int i = first; i < path.Steps.Count; i++)
            place = Step(place, path.Steps[i]);
        return place;
    }

    IrPlace LocalPlace(BoundLocalAccess access)
    {
        var local = locals[access.Local];
        var type = access.Local.Type;
        if (local.Kind == IrLocalKind.Reference && (!type.IsNullableReference || access.AssumesNonNull))
            return IrPlace.Deref(local);
        if (access.AssumesNonNull && type.IsNullableValue)
            return new IrPlace(local).Assumed();
        return new IrPlace(local);
    }

    IrPlace Step(IrPlace current, BoundExpression step)
    {
        switch (step)
        {
            case BoundFieldAccess field:
                return current.Field(field.Field);
            case BoundIndex index:
                var operand = IndexOperand(index.Index);
                if (operand is not IrConstant)
                    Add(new IrCheck(IrCheckKind.Index, [operand, IrConstant.Int(current.Type.FixedLength)], Loc(index.Location)) { Origin = at });
                return current.Element(operand);
            case BoundCall call when current.Type.IsTypeParameter:
            {
                // A generic value may be an object holding what the method hands out, so it is held itself.
                var holder = Hold(current, MayBeBorrowable(current.Type));
                return Returned(call, IrRead.Borrow(new IrPlace(holder)));
            }
            case BoundCall call:
                return Returned(call, new IrRefArgument(call.Method.Symbol.IsReadOnly ? RefKind.In : RefKind.Ref, current));
            case BoundNonNull nonNull:
            {
                var reference = current is { IsDeref: false, Projections.Count: 0 } ? current.Local : Let(new IrUse(IrRead.Copy(current)));
                Add(new IrCheck(IrCheckKind.NotNull, [IrRead.Copy(new IrPlace(reference))], Loc(nonNull.Location)) { Origin = at });
                return IrPlace.Deref(reference);
            }
            case BoundConversion:
                return current;
            default:
                throw new InvalidOperationException($"unexpected step {step.GetType().Name}");
        }
    }

    /// <summary>The place a method returns a reference to, or the view it returns, which points into its receiver.</summary>
    IrPlace Returned(BoundCall call, IrArgument receiver)
    {
        var result = Let(Call(call, receiver));
        return call.Method.Symbol.ReturnRefKind != RefKind.None && !call.Type.IsNullableReference ? IrPlace.Deref(result) : new IrPlace(result);
    }

    /// <summary>An index, as a constant or a temporary, so that its check and its element read the same value.</summary>
    IrOperand IndexOperand(BoundExpression index) => index is BoundLiteral literal
        ? new IrConstant(literal.Type, literal.Value)
        : IrRead.Copy(new IrPlace(Let(new IrUse(Settle(Value(index))))));

    /// <summary>
    /// A local holding the object that a place's first step is in: <c>this</c>, an object local, or a
    /// hold of the object, which <c>HoldElision</c> turns into an alias where nothing can replace it.
    /// <paramref name="borrows"/> when the object hands out storage it may move unless it is borrowed.
    /// </summary>
    IrLocal ObjectLocal(BoundExpression root, Use use, bool borrows)
    {
        switch (root)
        {
            case BoundThis:
                return borrows ? Hold(new IrPlace(self!), borrows) : self!;
            case BoundLocalAccess { Local: var symbol }:
                var local = locals[symbol];
                // Only the function itself, or a lambda assigning it, can replace what a local holds.
                return use == Use.Read && !borrows && !ChangedByLambda(local) ? local : Hold(new IrPlace(local), borrows);
            case BoundLiteral:
                return Let(Rvalue(root, Use.Read));
            case var place when IsPlace(place):
                return Hold(Place(place, Use.Read), borrows);
            // Held where it is once checked, as any other place's object is, rather than copied out.
            case BoundNonNull { Operand: var nullable } nonNull when IsPlace(nullable):
            {
                var checkedPlace = Place(nullable, Use.Read);
                Add(new IrCheck(IrCheckKind.NotNull, [IrRead.Borrow(checkedPlace)], Loc(nonNull.Location)) { Origin = at });
                return Hold(checkedPlace, borrows);
            }
            default:
                var value = Let(Rvalue(root, Use.Read));
                return borrows ? Hold(new IrPlace(value), borrows) : value;
        }
    }

    IrLocal Hold(IrPlace place, bool borrows) => Let(new IrHold(place, borrows));

    /// <summary>Whether an object of the type may be a <c>Borrowable</c>, which some class in the program it could be implements.</summary>
    bool MayBeBorrowable(VplType type) => type.TypeParameter is { } parameter
        ? parameter.Bounds.Any(MayBeBorrowable)
        : borrowableClasses.Any(c => c == type.NonNull.Symbol || Conversions.Supertypes(c.Type).Any(t => t.Symbol == type.NonNull.Symbol));

    /// <summary>A read of a place, made by the instruction using it unless that would read something changed since.</summary>
    IrRead Read(IrPlace place)
    {
        var read = IrRead.Copy(place);
        pendingReads.Add(new PendingRead(read, block, block.Statements.Count, at));
        return read;
    }

    T Settle<T>(T argument) where T : IrArgument => (T)Settle([argument])[0];

    /// <summary>
    /// The arguments for an instruction about to be added: a read left in it is copied into a
    /// temporary where it was lowered instead, if what was lowered since may write what it reads.
    /// </summary>
    List<IrArgument> Settle(IEnumerable<IrArgument> arguments)
    {
        var result = arguments.ToList();
        var settling = result.Select((a, i) => (Pending: pendingReads.Find(p => ReferenceEquals(p.Read, a)), Position: i))
            .Where(p => p.Pending is not null)
            .OrderByDescending(p => p.Pending!.Index).ThenByDescending(p => p.Position)
            .ToList();
        foreach (var (pending, position) in settling)
        {
            pendingReads.Remove(pending!);
            if (pending!.Block != block)
                throw new InvalidOperationException("a read is used in another block than it was lowered in");
            var since = new IrFootprint();
            foreach (var statement in block.Statements.Skip(pending.Index))
                since.Add(IrWalk.Footprint(statement));
            if (!MayChange(pending.Read.Place, since))
                continue;
            var copy = new IrLet(new IrUse(pending.Read)) { Origin = pending.Origin };
            block.Statements.Insert(pending.Index, copy);
            foreach (var other in pendingReads.Where(p => p.Block == block && p.Index > pending.Index).ToList())
                pendingReads[pendingReads.IndexOf(other)] = other with { Index = other.Index + 1 };
            result[position] = IrRead.Move(new IrPlace(copy.Local));
        }
        return result;
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

    bool MayChange(IrPlace place, IrFootprint footprint) => footprint.MayChange(Chain(place), this);
}
