using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Semantics;

/// <summary>What a <see cref="PlacePath"/> starts from.</summary>
public enum PlaceBase
{
    /// <summary>A local or parameter: its storage, or for a reference or a span, what it refers to.</summary>
    Local,
    /// <summary><c>this</c>: a struct's own storage, or a class's object.</summary>
    This,
    /// <summary>
    /// The object, or string, that the root's value is: the first step is a field of it, or a
    /// reference or view one of its methods returns, which points into storage it holds itself.
    /// </summary>
    Object,
    /// <summary>What the root, a pointer, points to: the first step is an element of it.</summary>
    Pointer,
    /// <summary>The place a function with no receiver returns a reference to; the root is the call.</summary>
    Returned,
    /// <summary>A <c>ref struct</c> constructed from views and arrays, which are its <see cref="PlacePath.Parts"/>.</summary>
    Views,
    /// <summary>Any other value, whose storage is a temporary.</summary>
    Temporary,
}

/// <summary>
/// A place, or a view, decomposed into a base and the steps from it that stay in the base's
/// storage: fields of structs, elements of fixed arrays, references and views that methods of
/// structs and spans return, and the <c>ref?</c> conversions and <c>!</c>s that refer to the same
/// place. Only the base can leave the current storage, so what holds a place's storage is found
/// from its base alone. A new way to make a place or a view is taught here.
/// </summary>
/// <param name="Steps">The steps from the root outward, each one's operand being the one before it, or the root.</param>
public sealed record PlacePath(PlaceBase Base, BoundExpression Root, IReadOnlyList<BoundExpression> Steps, IReadOnlyList<PlacePath> Parts)
{
    /// <summary>The expression the path decomposes.</summary>
    public BoundExpression Value => Steps.Count > 0 ? Steps[^1] : Root;

    public PlacePath Prefix(int count) => this with { Steps = [.. Steps.Take(count)] };

    public static PlacePath Of(BoundExpression expression)
    {
        var steps = new List<BoundExpression>();
        PlacePath Done(PlaceBase kind, BoundExpression root, BoundExpression? crossing = null, IReadOnlyList<PlacePath>? parts = null)
        {
            if (crossing is not null)
                steps.Add(crossing);
            steps.Reverse();
            return new PlacePath(kind, root, steps, parts ?? []);
        }

        var current = expression;
        while (true)
        {
            switch (current)
            {
                case BoundFieldAccess { Receiver.Type.IsReference: true } field:
                    return Done(PlaceBase.Object, field.Receiver, field);
                case BoundIndex { Target.Type.IsPointer: true } index:
                    return Done(PlaceBase.Pointer, index.Target, index);
                case BoundCall { Receiver: { } receiver } call when call.Method.Symbol.ReturnRefKind != RefKind.None || call.Type.IsRefStruct:
                    // A string's bytes are in a block that its copies share.
                    if (receiver.Type.SharesStorage)
                        return Done(PlaceBase.Object, receiver, call);
                    steps.Add(call);
                    current = receiver;
                    break;
                case BoundCall { Receiver: null, Method.Symbol.ReturnRefKind: not RefKind.None } call:
                    return Done(PlaceBase.Returned, call);
                case BoundFieldAccess field:
                    steps.Add(field);
                    current = field.Receiver;
                    break;
                case BoundIndex index:
                    steps.Add(index);
                    current = index.Target;
                    break;
                case BoundNonNull { Operand.Type.IsNullableReference: true } nonNull:
                    steps.Add(nonNull);
                    current = nonNull.Operand;
                    break;
                case BoundConversion conversion when conversion.Type.IsNullableReference || conversion.Type.IsRefStruct:
                    steps.Add(conversion);
                    current = conversion.Operand;
                    break;
                case BoundConstruction { Type.IsRefStruct: true } construction:
                    return Done(PlaceBase.Views, construction, parts: [.. construction.Arguments
                        .Where(a => a is BoundArrayData || a.Type.IsRefStruct)
                        .Select(a => Of(a is BoundArrayData data ? data.Array : a))]);
                case BoundLocalAccess:
                    return Done(PlaceBase.Local, current);
                case BoundThis:
                    return Done(PlaceBase.This, current);
                default:
                    return Done(PlaceBase.Temporary, current);
            }
        }
    }

    /// <summary>
    /// The places whose storage a view views. A view made from another views what that one does;
    /// one stored in a variable, field, or parameter views what it was made from, which the path
    /// can't see, so it is its own result. An object or a string that a view's method was called
    /// on holds what it views itself, and is a path with no steps.
    /// </summary>
    public static IEnumerable<PlacePath> Viewed(PlacePath view)
    {
        int count = view.Steps.Count;
        while (count > 0 && view.Steps[count - 1] is BoundConversion { Type.IsRefStruct: true } or BoundCall { Type.IsRefStruct: true, Receiver.Type.IsRefStruct: true })
            count--;
        if (count > 0 && view.Steps[count - 1] is BoundCall { Type.IsRefStruct: true })
            return [view.Prefix(count - 1)];
        if (count == 0 && view.Base == PlaceBase.Views)
            return view.Parts.SelectMany(part => part.Value.Type.IsRefStruct ? Viewed(part) : [part]);
        return [view.Prefix(count)];
    }

    /// <summary>
    /// The local whose nullable value a place lies in, reached through narrowing, as <c>v.x</c> is in
    /// a <c>Vec3? v</c>. Assigning the local null destroys that value, so a reference to the place
    /// must not outlive the operation using it. A local that isn't <c>mut</c> keeps its value, so
    /// references into it can be kept.
    /// </summary>
    public static LocalSymbol? NullableValueRoot(BoundExpression place) => NullableValueRoot(Of(place));

    static LocalSymbol? NullableValueRoot(PlacePath path) =>
        path is { Base: PlaceBase.Local, Root: BoundLocalAccess { AssumesNonNull: true, Local: { Type.IsNullableValue: true } local } }
        && !MayReferInto(local)
            ? local
            : null;

    /// <summary>
    /// Whether a reference may be kept into the narrowed value of a nullable local: only one that
    /// can never be assigned again, so assigning it null can't destroy what the reference refers to.
    /// </summary>
    public static bool MayReferInto(LocalSymbol local) => !local.IsWritable && local.RefKind == RefKind.None;

    /// <summary>
    /// The local whose nullable value a span or other <c>ref struct</c> views, as <see cref="NullableValueRoot(BoundExpression)"/>.
    /// A view of an object's or a string's contents views storage the value only refers to.
    /// </summary>
    public static LocalSymbol? ViewedNullableValue(BoundExpression view) =>
        Viewed(Of(view)).Select(NullableValueRoot).FirstOrDefault(root => root is not null);
}
