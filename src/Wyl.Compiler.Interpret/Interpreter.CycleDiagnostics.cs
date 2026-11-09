using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;

namespace Wyl.Compiler.Interpret;

// The cycle collector, a port of runtime/wyl/cycles.h: synchronous trial deletion (Bacon and Rajan,
// "Concurrent Cycle Collection in Reference Counted Systems", 2001), run only when the program
// calls memory::collectCycles. Traversals use explicit stacks, since a cycle may be a long chain. A
// reference the walk misses only makes its target look referenced from outside, so the collector
// frees less, never something in use.

public sealed partial class Interpreter
{
    /// <summary>A class's name, as <c>ClassName</c> writes it, for the collector's diagnostics.</summary>
    static string ClassName(HeapObject heap) => heap switch
    {
        ObjectRef obj => obj.Type.Name,
        ClosureValue closure => closure.Name,
        _ => "object",
    };

    /// <summary>A class's name and how many objects of it, in the order the objects first appear.</summary>
    static List<(string Name, long Count)> CountByClass(IEnumerable<HeapObject> objects)
    {
        var counts = new List<(string Name, long Count)>();
        foreach (var heap in objects)
        {
            var name = ClassName(heap);
            int found = counts.FindIndex(entry => entry.Name == name);
            if (found >= 0)
                counts[found] = (name, counts[found].Count + 1);
            else
                counts.Add((name, 1));
        }
        // By name, as runtime/wyl/cycles.h orders them, since the order objects are found in may change with optimization.
        counts.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return counts;
    }

    void NoteWaiting()
    {
        long waiting = buffer.Count - holes;
        growingCalls = waiting > lastWaiting ? growingCalls + 1 : 0;
        lastWaiting = waiting;
        if (growingCalls < GrowingCallsToWarn || warnedGrowing)
            return;
        warnedGrowing = true;
        PerformanceWarning($"{waiting} objects are waiting for memory::collectCycles, more after each of the last {growingCalls} calls; its budget is too small to keep up");
    }

    void NoteFreed()
    {
        foreach (var (name, count) in CountByClass(garbage))
        {
            int found = freedClasses.FindIndex(entry => entry.Name == name);
            if (found < 0)
            {
                freedClasses.Add((name, 0));
                found = freedClasses.Count - 1;
            }
            var (reported, freed) = freedClasses[found];
            if (freed < 0 || ++freed < CollectionsToWarn)
            {
                freedClasses[found] = (reported, freed);
                continue;
            }
            freedClasses[found] = (reported, -1);
            PerformanceWarning($"objects of class '{name}' were freed by {CollectionsToWarn} calls of memory::collectCycles; a Weak<T> in their cycles would let them be destroyed as soon as they are unreachable");
        }
    }

    /// <summary>Finds the garbage among the buffered objects without destroying it, at exit.</summary>
    void ReportUncollected()
    {
        if (options.Release)
            return;
        foreach (var root in buffer)
        {
            if (root is null)
                continue;
            MarkGray(root, long.MaxValue);
            roots.Add(root);
        }
        FindGarbage(roots.Count);
        if (garbage.Count == 0)
            return;

        var message = $"at exit, {garbage.Count} objects in cycles were never freed:";
        var separator = " ";
        foreach (var (name, count) in CountByClass(garbage))
        {
            message += $"{separator}{count} {name}";
            separator = ", ";
        }
        message += ". Call memory::collectCycles to free them, or break the cycles with Weak<T>";
        PerformanceWarning(message);
    }
}
