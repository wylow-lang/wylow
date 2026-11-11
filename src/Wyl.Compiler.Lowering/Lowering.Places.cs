using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.Lowering;

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
        BoundLocalAccess or BoundFieldAccess or BoundStaticFieldAccess or BoundIndex => true,
        BoundThis self => !self.Type.IsReference,
        BoundCall { Method.Symbol.ReturnRefKind: not RefKind.None } => true,
        BoundNonNull { Operand.Type.IsNullableValue: true } => true,
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
                var holder = ObjectLocal(path.Root, crossing is BoundCall && program.MayBeBorrowable(path.Root.Type, symbol));
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
                // A `ref` or `in` pointer parameter names the pointer's storage, so its value is read
                // first; only a local that is the pointer itself can be its target's place.
                var pointer = path.Root is BoundLocalAccess access && locals[access.Local].Deref == IrDeref.Pointee
                    ? Accessed(access)
                    : Let(Rvalue(path.Root, Use.Read));
                place = IrPlace.Deref(pointer).Element(IndexOperand(index.Index));
                first = 1;
                break;
            }
            case PlaceBase.Static:
                place = StaticPlace((BoundStaticFieldAccess)path.Root);
                break;
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

    IrPlace StaticPlace(BoundStaticFieldAccess access)
    {
        var statik = statics[access.Field];
        Add(new IrDo(new IrCall(new MethodReference(statik.Ensure, null, []), null, [], Loc(access.Location))) { Origin = access.Location });
        return new IrPlace(statik.Storage);
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
        if (guardReferences.TryGetValue(access.Local, out var reference))
            return reference;
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
                    Add(new IrCheck(IrCheckKind.Index, [operand, IrConstant.Int(((FixedArrayType)current.Type).Length)], Loc(index.Location)) { Origin = at });
                return current.Element(operand);
            case BoundCall call when current.Type.IsTypeParameter:
            {
                // A generic value may be an object holding what the method hands out, so it is held itself.
                var holder = Hold(current, program.MayBeBorrowable(current.Type, symbol));
                return Returned(call, IrRead.Borrow(new IrPlace(holder)));
            }
            case BoundCall call:
                return Returned(call, new IrRefArgument(call.Method.Symbol.IsReadOnly ? RefKind.In : RefKind.Ref, current));
            case BoundNonNull nonNull:
            {
                if (current.Type.IsNullableValue)
                {
                    Add(new IrCheck(IrCheckKind.HasValue, [IrRead.Borrow(current)], Loc(nonNull.Location)) { Origin = at });
                    return current.Assumed();
                }
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
    /// A local holding the object that a place's first step is in: a class's <c>this</c>, which the
    /// caller holds, a temporary, or a hold of the object, which <c>HoldElision</c> turns into an
    /// alias where nothing can replace it. <paramref name="borrows"/> when the object hands out
    /// storage it may move unless it is borrowed.
    /// </summary>
    IrLocal ObjectLocal(BoundExpression root, bool borrows)
    {
        switch (root)
        {
            // The `this` of an extern struct's method, such as a `String`'s, is the caller's place,
            // so the method holds it as it holds an `in` parameter.
            case BoundThis when !self!.Type.IsReference:
                return Hold(new IrPlace(self), borrows);
            case BoundThis:
                return borrows ? Hold(new IrPlace(self!), borrows) : self!;
            case BoundLiteral:
                return Let(Rvalue(root, Use.Read));
            case var place when IsPlace(place):
                // A local's or field's place is the object, or a narrowed value's place, which is
                // what a `String?` receiver must hold to keep its string.
                return Hold(Place(place, Use.Read), borrows);
            // Held where it is once checked, as any other place's object is, rather than copied out.
            // A nullable value's `!` checks that it has a value; a nullable reference's checks null.
            case BoundNonNull { Operand: var nullable } nonNull when IsPlace(nullable):
            {
                var checkedPlace = Place(nullable, Use.Read);
                bool reference = checkedPlace.Type.IsNullableObject;
                Add(new IrCheck(reference ? IrCheckKind.NotNull : IrCheckKind.HasValue, [IrRead.Borrow(checkedPlace)], Loc(nonNull.Location)) { Origin = at });
                return Hold(reference ? checkedPlace : checkedPlace.Assumed(), borrows);
            }
            default:
                // A temporary is held like a local: a reference or view into it may outlive the
                // block that made it, which the holder is declared outside of.
                return Hold(new IrPlace(Let(Rvalue(root, Use.Read))), borrows);
        }
    }

    IrLocal Hold(IrPlace place, bool borrows) => Let(new IrHold(place, borrows));

    /// <summary>
    /// A read of a place. A copyable value is copied into a temporary at its turn, which the
    /// instruction using it moves from, or copies if it is a reference. A unique value is used in
    /// place, as the operand of an operator is (spec <c>memory.moves</c>); whoever takes it moves it
    /// from the place, which the flow analysis checks is a whole local or parameter.
    /// </summary>
    IrRead Read(IrPlace place)
    {
        if (Moves(place.Type))
            return IrRead.Borrow(place);
        return FromTemporary(Let(new IrUse(IrRead.Copy(place))));
    }

    /// <summary>A read of a temporary that is used once: a value is moved from it, and a reference is copied.</summary>
    static IrRead FromTemporary(IrLocal temporary) =>
        new(temporary.Kind == IrLocalKind.Value ? IrOwnership.Move : IrOwnership.Copy, new IrPlace(temporary));
}
