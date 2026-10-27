namespace Vpl.Compiler.Ir;

/// <summary>
/// What may happen while a hold, or anything pointing into what it holds, is in use
/// (<see cref="Extent"/>); what may happen once something points into it (<see cref="Referenced"/>),
/// which is all a temporary reference's borrow must last for; and whether a source local or loop
/// points into it (<see cref="Named"/>), whose borrow lasts for its scope.
/// </summary>
public readonly record struct IrExtent(IrFootprint Extent, IrFootprint Referenced, bool Named)
{
    /// <summary>The extent of the hold that statement <paramref name="hold"/> of a block makes.</summary>
    public static IrExtent Of(List<IrStatement> statements, int hold)
    {
        var dependents = new HashSet<IrLocal> { ((IrLet)statements[hold]).Local };
        int last = -1;
        int firstReference = -1;
        bool lastInUse = false;
        for (int i = hold + 1; i < statements.Count; i++)
        {
            foreach (var let in IrWalk.Descendants(statements[i]).OfType<IrLet>().Where(l => RefersInto(l, dependents.Contains)))
            {
                dependents.Add(let.Local);
                // Inside another statement, it may be made before more of that statement runs.
                if (let.Value is not IrLambda && firstReference < 0)
                    firstReference = ReferenceEquals(let, statements[i]) ? i : i - 1;
            }
            if (IrWalk.Locals(statements[i]).Any(dependents.Contains))
            {
                last = i;
                lastInUse = InUse(statements[i], dependents);
            }
        }
        var footprint = new IrFootprint();
        var referenced = new IrFootprint();
        for (int i = hold + 1; i < last || (i == last && lastInUse); i++)
        {
            var inner = IrWalk.Footprint(statements[i]);
            footprint.Add(inner);
            // The statement making the first reference runs before the reference exists.
            if (firstReference >= 0 && i > firstReference)
                referenced.Add(inner);
        }
        return new(footprint, referenced, dependents.Any(d => d.Symbol is not null));
    }

    /// <summary>
    /// Whether the local a let defines may refer into the storage of a local <paramref name="into"/>
    /// accepts, and so needs it to last: a lambda whose body uses it, since the body runs where the
    /// lambda is called, or a local that refers, made from it.
    /// </summary>
    public static bool RefersInto(IrLet let, Func<IrLocal, bool> into) => let.Value is IrLambda lambda
        ? IrWalk.Locals(lambda.Function.Body).Any(into)
        : Refers(let.Local) && IrWalk.Operands(let.Value).SelectMany(IrWalk.Places).SelectMany(IrWalk.PlaceLocals).Any(into);

    /// <summary>Whether a local of this kind refers to storage it doesn't own.</summary>
    public static bool Refers(IrLocal local) =>
        local.Kind is IrLocalKind.Reference or IrLocalKind.Borrowed || local.Type.IsRefStruct || local.Type.IsPointer || local.Type.NonNull.IsNonEscapingFunction;

    /// <summary>Whether the statement uses one of the locals while it runs code, rather than only reading a value out of it first.</summary>
    static bool InUse(IrStatement statement, HashSet<IrLocal> locals)
    {
        if (statement is IrBlock or IrIf or IrLoop)
            return true;
        if (IrWalk.Rvalue(statement) is not { } value || IrWalk.Passed(value) is not { } passed)
            return false;
        return passed.All.Any(argument => argument switch
        {
            IrRefArgument reference => IrWalk.PlaceLocals(reference.Place).Any(locals.Contains),
            IrAddrArgument address => IrWalk.PlaceLocals(address.Place).Any(locals.Contains),
            IrRead { Ownership: IrOwnership.Borrow } read => IrWalk.PlaceLocals(read.Place).Any(locals.Contains),
            IrRead read => locals.Contains(read.Place.Local) && Refers(read.Place.Local) && read.Place is { IsDeref: false, Projections.Count: 0 },
            _ => false,
        });
    }
}
