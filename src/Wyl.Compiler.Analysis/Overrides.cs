using Wyl.Compiler.Semantics;

namespace Wyl.Compiler.Analysis;

/// <summary>
/// The methods a dispatched call may run, for whole-program analyses: the method called, and every
/// method overriding it or implementing it, directly or by overriding another that does.
/// </summary>
public sealed class Overrides
{
    readonly ILookup<FunctionSymbol, FunctionSymbol> overriders;
    readonly Dictionary<FunctionSymbol, IReadOnlyList<FunctionSymbol>> targets = [];
    readonly StdlibSymbols stdlib;

    internal Overrides(BoundProgram program)
    {
        stdlib = program.Stdlib;
        overriders = program.Types.SelectMany(t => t.Methods.Values.SelectMany(m => m))
            .SelectMany(m => m.Overridden.Select(overridden => (Overridden: overridden, By: m)))
            .ToLookup(p => p.Overridden, p => p.By);
    }

    /// <summary>The methods a call of the method may run if it dispatches, starting with the method itself.</summary>
    public IReadOnlyList<FunctionSymbol> Targets(FunctionSymbol method)
    {
        lock (targets)
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

    /// <summary>
    /// The method a dispatched call of <paramref name="method"/> runs on an object of the runtime
    /// type: the override in the nearest class of its chain, or a struct's own method that implements
    /// it. An abstract method's answer is the method itself, whose stub panics when a call reaches it
    /// during a base constructor or destructor; null if no method of the type does.
    /// </summary>
    public MethodReference? Implementation(MethodReference method, WylType runtimeType)
    {
        var candidates = Targets(method.Symbol);
        for (var type = stdlib.MemberHost(runtimeType.NonNull); type is not null; type = type.BaseType)
        {
            if (type.Symbol is not { } symbol)
                continue;
            foreach (var candidate in candidates)
            {
                if (candidate.ContainingType == symbol)
                    return new MethodReference(candidate, type, method.TypeArguments);
            }
        }
        return null;
    }
}
