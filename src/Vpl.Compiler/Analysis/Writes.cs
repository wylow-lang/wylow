using Vpl.Compiler.Ir;
using Vpl.Compiler.Semantics;

namespace Vpl.Compiler.Analysis;

/// <summary>
/// A type of storage that may be written. An <see cref="Element"/> write is to an element, or to
/// a place a reference or pointer leads to, which may be an element; any other write is to a
/// variable or field, which is only an element's storage through a fixed array it contains.
/// </summary>
public readonly record struct StoredType(VplType Type, bool Element)
{
    public static StoredType Of(IrPlace place)
    {
        var last = place.Projections.LastOrDefault(p => p is not IrAssumed);
        bool element = last switch
        {
            IrElement => true,
            null => place.IsDeref || place.Local.IsThis,
            _ => false,
        };
        return new(place.Type, element);
    }

    /// <summary>Whether a write of this type may store over an element whose type is <paramref name="element"/>.</summary>
    public bool MayStoreOverElement(VplType element) =>
        (Element && IsElementOf(Type, element)) || ContainsElements(Type, element);

    static bool IsElementOf(VplType type, VplType element) => type.NonNull == element || type.NonNull.IsTypeParameter;

    /// <summary>Whether a value of the type may contain elements of type <paramref name="element"/>: a fixed array of them, directly or in a field.</summary>
    static bool ContainsElements(VplType type, VplType element) =>
        type.InlineTypes().Any(t => t.IsTypeParameter || (t.IsFixedArray && IsElementOf(t.ElementType!, element)));
}

/// <summary>The types of storage some code may write, or <see cref="Unknown"/> if it may write anything.</summary>
public sealed class WriteSet
{
    const int MaxWrites = 64;

    readonly HashSet<StoredType> writes = [];

    public bool Unknown { get; private set; }

    public int Size => Unknown ? int.MaxValue : writes.Count;

    public void Add(StoredType write)
    {
        if (!Unknown && writes.Add(write) && writes.Count > MaxWrites)
            Unknown = true;
    }

    public void AddUnknown() => Unknown = true;

    public void Add(WriteSet from, TypeMap map)
    {
        if (from.Unknown)
            Unknown = true;
        foreach (var write in from.writes)
            Add(write with { Type = map.Apply(write.Type) });
    }

    public bool MayStoreOverElement(VplType element) => Unknown || writes.Any(w => w.MayStoreOverElement(element));
}

/// <summary>
/// The types of storage each function may write, including through the functions it calls, for
/// borrowing loop elements in place (<see cref="ElementAliasing"/>) and the verifier's check of
/// them. What a function stores to its own locals is left out, since no other code sees them, and
/// so is what a constructor stores into its own object, which is new, so can't be an element
/// anything iterates. A lambda's writes count where it may run, at each call of a function value
/// of its type, not in the code that makes it: no other code runs a lambda.
/// </summary>
public sealed class Writes
{
    readonly Callees callees;
    readonly Summaries<IrFunction, WriteSet> summaries;
    readonly WriteSet released = new();

    Writes(Callees callees)
    {
        this.callees = callees;
        summaries = new Summaries<IrFunction, WriteSet>(callees.Program.Functions, () => new WriteSet(), s => s.Size);
    }

    public static Writes Find(Callees callees)
    {
        var result = new Writes(callees);
        result.summaries.Solve(result.Summarize);
        foreach (var function in callees.Released)
            result.released.Add(result.summaries[function]!, TypeMap.Empty);
        return result;
    }

    /// <summary>What code that runs may write, including what the releases it makes may.</summary>
    public WriteSet Of(IReadOnlyList<IrRun> runs)
    {
        var set = new WriteSet();
        if (runs.Count == 0)
            return set;
        set.Add(released, TypeMap.Empty);
        foreach (var run in runs)
            Add(set, run);
        return set;
    }

    /// <summary>Whether an instruction may store over an element whose type is <paramref name="element"/>, itself or by the code it runs.</summary>
    public bool MayStoreOverElement(IrEffects effects, VplType element) =>
        effects.Writes.Any(w => StoredType.Of(w.Place).MayStoreOverElement(element)) || Of(effects.Runs).MayStoreOverElement(element);

    void Add(WriteSet into, IrRun run)
    {
        if (run == IrRun.Release)
            return;
        if (callees.Of(run) is not { } found)
        {
            into.AddUnknown();
            return;
        }
        foreach (var callee in found)
            into.Add(summaries[callee.Function]!, callee.Map);
    }

    WriteSet Summarize(IrFunction function)
    {
        var own = OwnLocals(function);
        var set = new WriteSet();
        foreach (var statement in IrWalk.Descendants(function.Body, intoLambdas: false))
        {
            if (statement is IrBlock or IrIf or IrLoop)
                continue;
            var effects = IrEffects.Of(statement);
            foreach (var write in effects.Writes.Where(w => !IsOwn(function, own, w.Place)))
                set.Add(StoredType.Of(write.Place));
            foreach (var run in effects.Runs)
                Add(set, run);
        }
        return set;
    }

    static HashSet<IrLocal> OwnLocals(IrFunction function)
    {
        HashSet<IrLocal> own = [.. function.Parameters, .. function.Captures];
        if (function.Struct is { } made)
            own.Add(made);
        foreach (var statement in IrWalk.Descendants(function.Body, intoLambdas: false))
        {
            switch (statement)
            {
                case IrLet let:
                    own.Add(let.Local);
                    break;
                case IrDeclare declare:
                    own.Add(declare.Local);
                    break;
            }
        }
        return own;
    }

    static bool IsOwn(IrFunction function, HashSet<IrLocal> own, IrPlace place) =>
        (!place.IsDeref && own.Contains(place.Local))
        || (function.Symbol.IsConstructor && place.Local == function.This && place.Projections.All(p => p is IrField or IrElement));
}
