using Vpl.Compiler.Ir;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;

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
                bool borrows = crossing is BoundCall && program.MayBeBorrowable(path.Root.Type) && use != Use.Read;
                var holder = ObjectLocal(path.Root, borrows);
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
                var pointer = path.Root is BoundLocalAccess { Local.Type.IsNullableReference: false } access ? Accessed(access) : Let(Rvalue(path.Root, Use.Read));
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
        var local = Accessed(access);
        var type = access.Local.Type;
        if (local.Kind == IrLocalKind.Reference && (!type.IsNullableReference || access.AssumesNonNull))
            return IrPlace.Deref(local);
        if (access.AssumesNonNull && type.IsNullableValue)
            return new IrPlace(local).Assumed();
        return new IrPlace(local);
    }

    /// <summary>
    /// The local an access of a source local uses, which every lowering of one asks for: where the
    /// source uses it as not null, as narrowing allows, this says so for the flow analysis to prove.
    /// An <c>is ref</c> name is a place wherever it is assigned, which is where its test matched.
    /// </summary>
    IrLocal Accessed(BoundLocalAccess access)
    {
        var local = locals[access.Local];
        if (access.AssumesNonNull && access.Local.Type.MayBeNull)
            Add(new IrCheck(IrCheckKind.Narrowed, [IrRead.Borrow(new IrPlace(local))], null) { Origin = access.Location });
        return local;
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
                var holder = Hold(current, program.MayBeBorrowable(current.Type));
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
        : IrRead.Copy(new IrPlace(Let(new IrUse(Value(index)))));

    /// <summary>
    /// A local holding the object that a place's first step is in: <c>this</c>, which the caller
    /// holds, a temporary, or a hold of the object, which <c>HoldElision</c> turns into an alias
    /// where nothing can replace it. <paramref name="borrows"/> when the object hands out storage
    /// it may move unless it is borrowed.
    /// </summary>
    IrLocal ObjectLocal(BoundExpression root, bool borrows)
    {
        switch (root)
        {
            case BoundThis:
                return borrows ? Hold(new IrPlace(self!), borrows) : self!;
            case BoundLocalAccess access:
                return Hold(new IrPlace(Accessed(access)), borrows);
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

    /// <summary>A read of a place, copied into a temporary at its turn, which the instruction using it moves from, or copies if it is a reference.</summary>
    IrRead Read(IrPlace place)
    {
        var copy = Let(new IrUse(IrRead.Copy(place)));
        return new IrRead(copy.Kind == IrLocalKind.Value ? IrOwnership.Move : IrOwnership.Copy, new IrPlace(copy));
    }
}
