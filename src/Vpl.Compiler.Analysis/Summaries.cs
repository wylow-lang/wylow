using Vpl.Compiler.Semantics;

namespace Vpl.Compiler.Analysis;

/// <summary>
/// A summary of each unit of code, which may depend on the summaries of the units it calls. Every
/// unit is summarized once, then again whenever a summary it read has grown since, until none
/// grows. A summary must only grow as those it reads do, so this ends; recursion is what makes a
/// unit need summarizing more than once.
/// </summary>
sealed class Summaries<TUnit, T>(IEnumerable<TUnit> units, Func<T> empty, Func<T, int> size) where TUnit : class where T : class
{
    readonly IReadOnlyList<TUnit> units = [.. units];
    readonly Dictionary<TUnit, T> found = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<TUnit, HashSet<TUnit>> readers = new(ReferenceEqualityComparer.Instance);
    TUnit? summarizing;

    /// <summary>The summary of a unit, or null if it isn't one of them.</summary>
    public T? this[TUnit unit]
    {
        get
        {
            if (summarizing is not null)
            {
                if (!readers.TryGetValue(unit, out var those))
                    readers[unit] = those = new(ReferenceEqualityComparer.Instance);
                those.Add(summarizing);
            }
            return found.GetValueOrDefault(unit);
        }
    }

    public void Solve(Func<TUnit, T> summarize)
    {
        foreach (var unit in units)
            found[unit] = empty();
        var pending = new Queue<TUnit>(units);
        var queued = new HashSet<TUnit>(units, ReferenceEqualityComparer.Instance);
        while (pending.TryDequeue(out var unit))
        {
            queued.Remove(unit);
            summarizing = unit;
            var summary = summarize(unit);
            summarizing = null;
            int growth = size(summary) - size(found[unit]);
            if (growth < 0)
                throw new InvalidOperationException($"the summary of '{unit}' shrank");
            if (growth == 0)
                continue;
            found[unit] = summary;
            foreach (var reader in readers.GetValueOrDefault(unit) ?? [])
            {
                if (queued.Add(reader))
                    pending.Enqueue(reader);
            }
        }
    }
}
