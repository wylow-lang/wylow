using Vpl.Compiler.Ir;

namespace Vpl.Compiler.Analysis;

/// <summary>
/// What each lambda may change around it (<see cref="IrFunction.ChangesAround"/>), from the same
/// effects every other analysis asks. It is what the program does, not an optimization: the
/// verifier finds it each time it runs, so it checks the IR as it stands.
/// </summary>
public static class LambdaWrites
{
    public static void Find(IrProgram program)
    {
        foreach (var function in program.Functions)
            function.ChangesAround.Clear();
        var done = new HashSet<IrFunction>();
        foreach (var lambda in program.Functions.Where(f => f.IsLambda))
            Find(lambda, done);
    }

    /// <summary>
    /// Replaces lowering's answer to <see cref="IrLocal.IsChangedByLambda"/>, made before the lambdas'
    /// bodies existed, with what they change. The verifier checks that no lambda changes a local
    /// the answer says it doesn't.
    /// </summary>
    public static void Apply(IrProgram program)
    {
        Find(program);
        var changed = program.Functions.SelectMany(f => f.ChangesAround).ToHashSet();
        foreach (var function in program.Functions)
        {
            foreach (var local in Locals(function))
                local.IsChangedByLambda = changed.Contains(local);
        }
    }

    /// <summary>The locals a lambda may change: a function's parameters, captures, and the locals it declares.</summary>
    public static IEnumerable<IrLocal> Locals(IrFunction function) =>
        function.Parameters.Concat(function.Captures).Concat(Writes.OwnLocals(function));

    /// <summary>
    /// A lambda changes what its body writes outside it, and what the lambdas it makes do, which run
    /// only while it does. Storing a whole reference local makes it refer elsewhere, which only a
    /// lambda's own declared references are.
    /// </summary>
    static void Find(IrFunction lambda, HashSet<IrFunction> done)
    {
        if (!done.Add(lambda))
            return;
        var own = Writes.OwnLocals(lambda);
        foreach (var statement in IrWalk.Descendants(lambda.Body, intoLambdas: false))
        {
            if (statement is IrLet { Value: IrLambda inner })
            {
                Find(inner.Function, done);
                lambda.ChangesAround.UnionWith(inner.Function.ChangesAround.Where(l => !own.Contains(l)));
            }
            if (statement is IrIf or IrLoop)
                continue;
            var effects = statement is IrBlock block ? IrEffects.Ending(block) : IrEffects.Of(statement);
            foreach (var write in effects.Writes)
            {
                var local = write.Place.Local;
                if (!write.Place.IsDeref && local.Kind != IrLocalKind.Reference && !own.Contains(local))
                    lambda.ChangesAround.Add(local);
            }
        }
    }
}
