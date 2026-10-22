namespace Vpl.Compiler.Semantics;

/// <summary>
/// The methods a dispatched call may run, for whole-program analyses: the method called, and every
/// method overriding it or implementing it, directly or by overriding another that does.
/// </summary>
public sealed class Overrides
{
    readonly ILookup<FunctionSymbol, FunctionSymbol> overriders;
    readonly Dictionary<FunctionSymbol, IReadOnlyList<FunctionSymbol>> targets = [];

    public Overrides(BoundProgram program) =>
        overriders = program.Types.SelectMany(t => t.Methods.Values.SelectMany(m => m))
            .SelectMany(m => m.Overridden.Select(overridden => (Overridden: overridden, By: m)))
            .ToLookup(p => p.Overridden, p => p.By);

    /// <summary>The methods a call of the method may run if it dispatches, starting with the method itself.</summary>
    public IReadOnlyList<FunctionSymbol> Targets(FunctionSymbol method)
    {
        if (targets.TryGetValue(method, out var found))
            return found;
        var all = new List<FunctionSymbol>();
        var seen = new HashSet<FunctionSymbol>();
        var pending = new Queue<FunctionSymbol>([method]);
        while (pending.TryDequeue(out var next))
        {
            if (!seen.Add(next))
                continue;
            all.Add(next);
            foreach (var overrider in overriders[next])
                pending.Enqueue(overrider);
        }
        return targets[method] = all;
    }
}
