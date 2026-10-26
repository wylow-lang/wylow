using Vpl.Compiler.Ir;

namespace Vpl.Compiler.Analysis;

/// <summary>
/// What each lambda may change around it, from the same effects every other analysis asks. It is
/// what the program does, not an optimization, and it is found for the IR as it stands: asking it
/// after the program has changed throws.
/// </summary>
public sealed class LambdaWrites : IIrLambdaChanges
{
    readonly Callees callees;
    readonly Dictionary<IrFunction, HashSet<IrLocal>> around = [];
    readonly Dictionary<IrFunction, HashSet<IrLocal>> assigns = [];
    readonly HashSet<IrLocal> changed;

    LambdaWrites(Callees callees)
    {
        this.callees = callees;
        foreach (var lambda in callees.Program.Functions.Where(f => f.IsLambda))
            Find(lambda);
        changed = [.. around.Values.SelectMany(l => l)];
    }

    public static LambdaWrites Find(Callees callees) => new(callees);

    /// <summary>
    /// The locals around a lambda that its body, or a lambda it makes, may assign or change in
    /// place. Code that runs once it is made may run it.
    /// </summary>
    public IReadOnlySet<IrLocal> Around(IrFunction lambda) => callees.Fresh(around[lambda]);

    /// <summary>The locals around a lambda that it may give another value as a whole, which ends their narrowing.</summary>
    public IReadOnlySet<IrLocal> Assigns(IrFunction lambda) => callees.Fresh(assigns[lambda]);

    /// <summary>
    /// Whether some lambda of the function may change the local, so that code that runs anywhere
    /// in it may. The verifier asks only the lambdas made before a point (<see cref="Around"/>).
    /// </summary>
    public bool MayChange(IrLocal local) => callees.Fresh(changed).Contains(local);

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
