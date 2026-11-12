using Wyl.Compiler.Ir;

namespace Wyl.Compiler.Analysis;

/// <summary>
/// What each lambda may change around it, and which locals a pointer points into, from the same
/// effects every other analysis asks. Code that runs may write a local through a pointer to it, as
/// it may through a lambda that changes it. It is what the program does, not an optimization, and
/// it is found for the IR as it stands: asking it after the program has changed throws.
/// </summary>
public sealed class LambdaWrites : IIrLambdaChanges
{
    readonly Callees callees;
    readonly Dictionary<IrFunction, HashSet<IrLocal>> around = [];
    readonly Dictionary<IrFunction, HashSet<IrLocal>> assigns = [];
    readonly HashSet<IrLocal> addressTaken;
    readonly HashSet<IrLocal> changed;

    LambdaWrites(Callees callees)
    {
        this.callees = callees;
        foreach (var lambda in callees.Program.Functions.Where(f => f.IsLambda))
            Find(lambda);
        var pointed = PointedInto(callees.Program);
        addressTaken = pointed.Address;
        changed = [.. around.Values.SelectMany(l => l), .. pointed.Address, .. pointed.Viewed];
    }

    internal static LambdaWrites Find(Callees callees) => new(callees);

    /// <summary>
    /// The locals around a lambda that its body, or a lambda it makes, may assign or change in
    /// place. Code that runs once it is made may run it.
    /// </summary>
    public IReadOnlySet<IrLocal> Around(IrFunction lambda) => callees.Fresh(around[lambda]);

    /// <summary>The locals around a lambda that it may give another value as a whole, which ends their narrowing.</summary>
    public IReadOnlySet<IrLocal> Assigns(IrFunction lambda) => callees.Fresh(assigns[lambda]);

    /// <summary>
    /// Whether some lambda of the function may change the local, or a pointer points into it, so
    /// that code that runs anywhere in it may. The verifier asks only the lambdas made before a
    /// point (<see cref="Around"/>), and <see cref="IsAddressTaken"/>.
    /// </summary>
    public bool MayChange(IrLocal local) => callees.Fresh(changed).Contains(local);

    /// <summary>
    /// Whether <c>&amp;</c> takes the address of the local's storage or of a place in it, so that
    /// unsafe code may write any of it. A span's pointer writes only the elements of the array it
    /// views, which the verifier's rules for views already cover.
    /// </summary>
    public bool IsAddressTaken(IrLocal local) => callees.Fresh(addressTaken).Contains(local);

    /// <summary>
    /// The locals whose storage a pointer may point into: what <c>&amp;</c> takes the address of,
    /// and the fixed arrays that spans view. A pointer made through a reference local points into
    /// what the reference may refer to.
    /// </summary>
    static (HashSet<IrLocal> Address, HashSet<IrLocal> Viewed) PointedInto(IrProgram program)
    {
        var referents = new Dictionary<IrLocal, List<IrPlace>>();
        var addressed = new List<IrPlace>();
        var viewed = new List<IrPlace>();
        foreach (var statement in program.Functions.SelectMany(f => IrWalk.Descendants(f.Body, intoLambdas: false)))
        {
            var (reference, referent) = statement switch
            {
                IrLet { Local.Kind: IrLocalKind.Reference } let => (let.Local, Referent(let.Value)),
                IrStore { Target: { IsDeref: false, Projections.Count: 0, Local.Kind: IrLocalKind.Reference } target } store => (target.Local, Referent(store.Value)),
                _ => (null, null),
            };
            if (reference is not null && referent is not null)
                (referents.TryGetValue(reference, out var known) ? known : referents[reference] = []).Add(referent);
            if (IrWalk.Rvalue(statement) is IrPointer pointer)
                addressed.Add(pointer.Place);
            else if (IrWalk.Rvalue(statement) is IrData data)
                viewed.Add(data.Place);
        }

        HashSet<IrLocal> Roots(List<IrPlace> places)
        {
            var found = new HashSet<IrLocal>();
            var followed = new HashSet<IrLocal>();
            void Add(IrPlace place)
            {
                if (!place.IsDeref)
                    found.Add(place.Local);
                else if (place.Local.Kind == IrLocalKind.Reference && followed.Add(place.Local))
                    referents.GetValueOrDefault(place.Local)?.ForEach(Add);
            }
            places.ForEach(Add);
            return found;
        }
        return (Roots(addressed), Roots(viewed));
    }

    /// <summary>The place a reference local is made to refer to: a place it is a reference to, or what another reference local refers to.</summary>
    static IrPlace? Referent(IrRvalue value) => value switch
    {
        IrRef reference => reference.Place,
        IrAddr address => address.Place,
        IrUse { Operand: var operand } => Referent(operand),
        _ => null,
    };

    static IrPlace? Referent(IrOperand operand) =>
        operand is IrRead { Place: { IsDeref: false, Projections.Count: 0, Local: { Kind: IrLocalKind.Reference } copied } } ? IrPlace.Deref(copied) : null;

    /// <summary>
    /// A lambda changes what its body writes outside it, and what the lambdas it makes do, which run
    /// only while it does. Storing a whole reference local makes it refer elsewhere, which only a
    /// lambda's own declared references are.
    /// </summary>
    HashSet<IrLocal> Find(IrFunction lambda)
    {
        if (around.TryGetValue(lambda, out var found))
            return found;
        var changes = around[lambda] = [];
        var replaces = assigns[lambda] = [];
        var own = Writes.OwnLocals(lambda);
        foreach (var statement in IrWalk.Descendants(lambda.Body, intoLambdas: false))
        {
            if (statement is IrLet { Value: IrLambda inner })
            {
                changes.UnionWith(Find(inner.Function).Where(l => !own.Contains(l)));
                replaces.UnionWith(assigns[inner.Function].Where(l => !own.Contains(l)));
            }
            foreach (var write in IrEffects.Itself(statement).Writes)
            {
                var local = write.Place.Local;
                if (write.Place.IsDeref || local.Kind == IrLocalKind.Reference || own.Contains(local))
                    continue;
                changes.Add(local);
                if (write.Place.Projections.Count == 0)
                    replaces.Add(local);
            }
        }
        return changes;
    }
}
