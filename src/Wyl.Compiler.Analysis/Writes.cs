using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;

namespace Wyl.Compiler.Analysis;

/// <summary>
/// The storage some code may write, as <see cref="IrAccess"/> without locals (<see
/// cref="IrAccess.Summarized"/>), or <see cref="Unknown"/> if it may write anything. A function's
/// summary also has the types of the values it may release, which <see cref="Writes.Of"/>
/// resolves to what their destructors write once the type arguments are known.
/// </summary>
public sealed class WriteSet : IMonotone<WriteSet>
{
    const int MaxWrites = 64;

    readonly HashSet<IrAccess> writes = [];
    readonly HashSet<WylType?> released = [];

    public bool Unknown { get; private set; }

    /// <summary>Whether it holds every write and release <paramref name="earlier"/> may have.</summary>
    public bool Contains(WriteSet earlier) =>
        earlier.released.IsSubsetOf(released) && (Unknown || (!earlier.Unknown && earlier.writes.IsSubsetOf(writes)));

    /// <summary>The types of the values it may release, null among them if any.</summary>
    public IReadOnlyCollection<WylType?> Released => released;

    public void AddRelease(WylType? type) => released.Add(type);

    public void Add(IrPlace place) => Add(IrAccess.Target(place).Summarized(TypeMap.Empty));

    void Add(IrAccess write)
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
            Add(write.Summarized(map));
        foreach (var type in from.released)
            released.Add(type is null ? null : map.Apply(type));
    }

    /// <summary>Whether it may write storage that the access touches.</summary>
    public bool MayWrite(IrAccess access) => Unknown || writes.Any(w => w.Overlaps(access));
}

/// <summary>
/// The storage each function may write, including through the functions it calls, for the
/// passes and the verifier. What a function stores to its own locals is left out, since no other code sees them, and
/// so is what a constructor stores into its own object, which is new, so can't be an element
/// anything iterates. A lambda's writes count where it may run, at each call of a function value
/// of its type, not in the code that makes it: no other code runs a lambda.
/// </summary>
public sealed class Writes
{
    readonly Callees callees;
    readonly Summaries<IrFunction, WriteSet> summaries;
    readonly Dictionary<WylType, WriteSet> releasing = [];
    WriteSet? releasingAny;

    /// <summary>What each lambda may change around it, which code that runs may run.</summary>
    public LambdaWrites Lambdas { get; }

    Writes(Callees callees)
    {
        this.callees = callees;
        Lambdas = LambdaWrites.Find(callees);
        summaries = new Summaries<IrFunction, WriteSet>(callees.Program.Functions, () => new WriteSet());
    }

    internal static Writes Find(Callees callees)
    {
        var result = new Writes(callees);
        result.summaries.Solve(result.Summarize);
        return result;
    }

    /// <summary>What code that runs may write, including what the destructors of what it releases may.</summary>
    public WriteSet Of(IReadOnlyList<IrRun> runs)
    {
        callees.Fresh(runs);
        var set = new WriteSet();
        foreach (var run in runs)
            Add(set, run);
        foreach (var type in set.Released.ToList())
            set.Add(Releasing(type), TypeMap.Empty);
        return set;
    }

    /// <summary>What releasing a value of the type, or with none, any value, may write, through every destructor that may run.</summary>
    WriteSet Releasing(WylType? type)
    {
        var known = type is null ? releasingAny : releasing.GetValueOrDefault(type);
        if (known is not null)
            return known;
        var set = new WriteSet();
        var done = new HashSet<WylType?>();
        var pending = new Queue<WylType?>([type]);
        while (pending.TryDequeue(out var next))
        {
            if (!done.Add(next))
                continue;
            foreach (var callee in callees.Of(new IrRunRelease(next))!.Functions)
                set.Add(summaries[callee.Function]!, callee.Map);
            foreach (var more in set.Released.Where(t => !done.Contains(t)))
                pending.Enqueue(more);
        }
        if (type is null)
            releasingAny = set;
        else
            releasing[type] = set;
        return set;
    }

    /// <summary>
    /// Whether code that runs may write the access (<see cref="IrAccess.WrittenByCode"/>): a field
    /// only if something it runs writes storage that overlaps it, and a local if a lambda changes
    /// it, which by default is any of the function's. Storage the function can't see may be moved
    /// by any code, as a container that isn't borrowed moves its elements.
    /// </summary>
    public bool CodeMayWrite(IrAccess access, IReadOnlyCollection<IrRun> runs, IIrLambdaChanges? lambdas = null) =>
        callees.Fresh(runs).Count > 0 && access.WrittenByCode(lambdas ?? Lambdas) && (access.Storage != IrStorage.Field || Of([.. runs]).MayWrite(access));

    /// <summary>Whether an instruction may write storage that the access touches, itself or by the code it runs.</summary>
    public bool MayWrite(IrEffects effects, IrAccess access) =>
        callees.Fresh(effects).Writes.Any(w => IrAccess.Target(w.Place).Overlaps(access)) || Of(effects.Runs).MayWrite(access);

    void Add(WriteSet into, IrRun run)
    {
        if (run is IrRunRelease release)
        {
            into.AddRelease(release.Type);
            return;
        }
        if (callees.Of(run) is not { } found)
        {
            into.AddUnknown();
            return;
        }
        foreach (var callee in found.Functions)
            into.Add(summaries[callee.Function]!, callee.Map);
        // A program's extern function may write anything through the pointers it is given or
        // keeps. The standard library's write nothing: the containers that use Memory's unsafe
        // functions panic rather than move or drop elements while borrowed.
        if (found.RunsExtern && run is IrRunMethod { Method: var method })
        {
            if (method.Symbol.IsProgramExtern)
                into.AddUnknown();
            foreach (var inner in callees.ExternRuns(method))
                Add(into, inner);
        }
    }

    WriteSet Summarize(IrFunction function)
    {
        if (!effectsOf.TryGetValue(function, out var effects))
            effectsOf[function] = effects = OwnEffects(function);
        var set = new WriteSet();
        foreach (var write in effects.Writes)
            set.Add(write);
        foreach (var run in effects.Runs)
            Add(set, run);
        return set;
    }

    /// <summary>
    /// What a function's own statements write outside its locals, and what they run, which stay the
    /// same however often the solver summarizes the function again.
    /// </summary>
    readonly Dictionary<IrFunction, (List<IrPlace> Writes, List<IrRun> Runs)> effectsOf = new(ReferenceEqualityComparer.Instance);

    static (List<IrPlace> Writes, List<IrRun> Runs) OwnEffects(IrFunction function)
    {
        var own = OwnLocals(function);
        // A callee releases what it is given by value when it returns.
        List<IrRun> runs = [.. function.Parameters.SelectMany(p => IrEffects.Releasing(p).Runs)];
        List<IrPlace> writes = [];
        foreach (var statement in IrWalk.Descendants(function.Body, intoLambdas: false))
        {
            var effects = IrEffects.Itself(statement);
            writes.AddRange(effects.Writes.Where(w => !IsOwn(function, own, w.Place)).Select(w => w.Place));
            runs.AddRange(effects.Runs);
        }
        return (writes, runs);
    }
    public static HashSet<IrLocal> OwnLocals(IrFunction function)
    {
        HashSet<IrLocal> own = [.. function.Parameters, .. function.Captures];
        if (function.Struct is { } made)
            own.Add(made);
        own.UnionWith(IrWalk.Descendants(function.Body, intoLambdas: false).Select(IrWalk.Declared).OfType<IrLocal>());
        return own;
    }

    static bool IsOwn(IrFunction function, HashSet<IrLocal> own, IrPlace place) =>
        (!place.IsDeref && own.Contains(place.Local))
        || (function.Symbol.IsConstructor && place.Local == function.This && place.Projections.All(p => p is IrField or IrElement));
}
