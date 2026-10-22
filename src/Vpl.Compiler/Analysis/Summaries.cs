using Vpl.Compiler.Semantics;

namespace Vpl.Compiler.Analysis;

/// <summary>
/// A summary of each unit, which may depend on the summaries of the functions it calls. Every unit
/// is summarized once, then again whenever a summary it read has grown since, until none grows.
/// A summary must only grow as those it reads do, so this ends; recursion is what makes a unit
/// need summarizing more than once.
/// </summary>
sealed class Summaries<T>(IEnumerable<Unit> units, Func<T> empty, Func<T, int> size) where T : class
{
    readonly IReadOnlyList<Unit> units = [.. units];
    readonly Dictionary<FunctionSymbol, T> found = [];
    readonly Dictionary<FunctionSymbol, HashSet<Unit>> readers = [];
    Unit? summarizing;

    /// <summary>The summary of a function, or null if it has no unit, such as an extern function.</summary>
    public T? this[FunctionSymbol function]
    {
        get
        {
            if (summarizing is not null)
            {
                if (!readers.TryGetValue(function, out var those))
                    readers[function] = those = [];
                those.Add(summarizing);
            }
            return found.GetValueOrDefault(function);
        }
    }

    public void Solve(Func<Unit, T> summarize)
    {
        foreach (var unit in units)
            found[unit.Symbol] = empty();
        var pending = new Queue<Unit>(units);
        var queued = new HashSet<Unit>(units);
        while (pending.TryDequeue(out var unit))
        {
            queued.Remove(unit);
            summarizing = unit;
            var summary = summarize(unit);
            summarizing = null;
            int growth = size(summary) - size(found[unit.Symbol]);
            if (growth < 0)
                throw new InvalidOperationException($"the summary of '{unit.Symbol}' shrank");
            if (growth == 0)
                continue;
            found[unit.Symbol] = summary;
            foreach (var reader in readers.GetValueOrDefault(unit.Symbol) ?? [])
            {
                if (queued.Add(reader))
                    pending.Enqueue(reader);
            }
        }
    }
}
