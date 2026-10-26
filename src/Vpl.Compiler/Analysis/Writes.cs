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

/// <summary>
/// The types of storage some code may write, or <see cref="Unknown"/> if it may write anything;
/// and the fields of objects it may write, either by name or through storage it can't see (a
/// reference, a pointer, or a struct method's <c>this</c>) of a type the field's value contains.
/// A function's summary also has the types of the values it may release, which <see
/// cref="Writes.Of"/> resolves to what their destructors write once the type arguments are known.
/// </summary>
public sealed class WriteSet
{
    const int MaxWrites = 64;

    readonly HashSet<StoredType> writes = [];
    readonly HashSet<FieldSymbol> fields = [];
    readonly HashSet<VplType> unseen = [];
    readonly HashSet<VplType?> released = [];

    public bool Unknown { get; private set; }

    public int Size => Unknown ? int.MaxValue : writes.Count + fields.Count + unseen.Count + released.Count;

    /// <summary>The types of the values it may release, null among them if any.</summary>
    public IReadOnlyCollection<VplType?> Released => released;

    public void AddRelease(VplType? type) => released.Add(type);

    public void Add(IrPlace place)
    {
        Add(StoredType.Of(place));
        if (place.IsDeref && place.Local.Kind != IrLocalKind.Reference && place.Local.Type.NonNull.IsReference)
        {
            if (place.Projections.FirstOrDefault() is IrField { Field: var field })
                fields.Add(field);
            else
                Unknown = true;
        }
        else if (place.IsDeref || place.Local.IsThis)
            unseen.Add(place.BaseType);
    }

    void Add(StoredType write)
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
        fields.UnionWith(from.fields);
        foreach (var type in from.unseen)
            unseen.Add(map.Apply(type));
        foreach (var type in from.released)
            released.Add(type is null ? null : map.Apply(type));
    }

    public bool MayStoreOverElement(VplType element) => Unknown || writes.Any(w => w.MayStoreOverElement(element));

    /// <summary>Whether it may write the field of some object, or, with no field, anything in one.</summary>
    public bool MayWriteField(FieldSymbol? field)
    {
        if (Unknown || field is null)
            return Unknown || fields.Count > 0 || unseen.Count > 0;
        return fields.Contains(field) || (unseen.Count > 0 && field.Type.InlineTypes().Any(t => t.IsTypeParameter || unseen.Contains(t) || unseen.Any(u => u.IsTypeParameter)));
    }
}

/// <summary>
/// The types of storage each function may write, including through the functions it calls, for
/// borrowing loop elements in place (<see cref="Optimize.InPlaceElements"/>) and the verifier's check of
/// them. What a function stores to its own locals is left out, since no other code sees them, and
/// so is what a constructor stores into its own object, which is new, so can't be an element
/// anything iterates. A lambda's writes count where it may run, at each call of a function value
/// of its type, not in the code that makes it: no other code runs a lambda.
/// </summary>
public sealed class Writes
{
    readonly Callees callees;
    readonly Summaries<IrFunction, WriteSet> summaries;
    readonly Dictionary<VplType, WriteSet> releasing = [];
    WriteSet? releasingAny;

    Writes(Callees callees)
    {
        this.callees = callees;
        summaries = new Summaries<IrFunction, WriteSet>(callees.Program.Functions, () => new WriteSet(), s => s.Size);
    }

    public static Writes Find(Callees callees)
    {
        var result = new Writes(callees);
        result.summaries.Solve(result.Summarize);
        return result;
    }

    /// <summary>What code that runs may write, including what the destructors of what it releases may.</summary>
    public WriteSet Of(IReadOnlyList<IrRun> runs)
    {
        var set = new WriteSet();
        foreach (var run in runs)
            Add(set, run);
        foreach (var type in set.Released.ToList())
            set.Add(Releasing(type), TypeMap.Empty);
        return set;
    }

    /// <summary>What releasing a value of the type, or with none, any value, may write, through every destructor that may run.</summary>
    WriteSet Releasing(VplType? type)
    {
        var known = type is null ? releasingAny : releasing.GetValueOrDefault(type);
        if (known is not null)
            return known;
        var set = new WriteSet();
        var done = new HashSet<VplType?>();
        var pending = new Queue<VplType?>([type]);
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
    /// only if something it runs writes that field.
    /// </summary>
    public bool CodeMayWrite(IrAccess access, IReadOnlyCollection<IrRun> runs) =>
        runs.Count > 0 && access.WrittenByCode && (access.Storage != IrStorage.Field || Of([.. runs]).MayWriteField(access.Field));

    /// <summary>Whether an instruction may store over an element whose type is <paramref name="element"/>, itself or by the code it runs.</summary>
    public bool MayStoreOverElement(IrEffects effects, VplType element) =>
        effects.Writes.Any(w => StoredType.Of(w.Place).MayStoreOverElement(element)) || Of(effects.Runs).MayStoreOverElement(element);

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
        // Other extern functions write nothing: the containers that use Memory's unsafe functions
        // panic rather than move or drop elements while borrowed.
        if (found.RunsExtern && run is IrRunMethod { Method: var method })
        {
            foreach (var inner in callees.ExternRuns(method))
                Add(into, inner);
        }
    }

    WriteSet Summarize(IrFunction function)
    {
        var own = OwnLocals(function);
        var set = new WriteSet();
        // A callee releases what it is given by value when it returns.
        foreach (var parameter in function.Parameters)
        {
            foreach (var run in IrEffects.Releasing(parameter).Runs)
                Add(set, run);
        }
        foreach (var statement in IrWalk.Descendants(function.Body, intoLambdas: false))
        {
            if (statement is IrIf or IrLoop)
                continue;
            var effects = statement is IrBlock block ? IrEffects.Ending(block) : IrEffects.Of(statement);
            foreach (var write in effects.Writes.Where(w => !IsOwn(function, own, w.Place)))
                set.Add(write.Place);
            foreach (var run in effects.Runs)
                Add(set, run);
        }
        return set;
    }
    public static HashSet<IrLocal> OwnLocals(IrFunction function)
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
