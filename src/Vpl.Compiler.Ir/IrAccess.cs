using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Ir;

public enum IrStorage
{
    /// <summary>A local's own storage, which only the function itself, or a lambda that assigns it, can write.</summary>
    Frame,
    /// <summary>A field of some object of the field's class, or with no field, anywhere in an object.</summary>
    Field,
    /// <summary>Storage the function can't see: through a reference or pointer, a struct method's <c>this</c>, or a generic value's.</summary>
    Unknown,
}

/// <summary>
/// Which locals a lambda the function makes may change when code runs: the analysis's answer, from
/// the lambdas' bodies (<c>LambdaWrites</c>).
/// </summary>
public interface IIrLambdaChanges
{
    bool MayChange(IrLocal local);
}

/// <summary>
/// The storage an instruction reads or writes: a frame local's, the field of some object of the
/// field's class (with no field, anywhere in an object), or storage the function can't see, then
/// the projections inside it. Two accesses may be the same storage if they <see cref="Overlaps"/>,
/// so a write somewhere inside a place, as a call given it by <c>ref</c> may make, is one to the place.
/// </summary>
public sealed record IrAccess(IrStorage Storage, IrLocal? Local, FieldSymbol? Field, IReadOnlyList<IrProjection> Path)
{
    public static readonly IrAccess Unknown = new(IrStorage.Unknown, null, null, []);

    static IrAccess Whole(IrLocal local) => new(IrStorage.Frame, local, null, []);

    /// <summary>What reading or writing a place touches: the storage it lies in, and the locals read to find it.</summary>
    public static IEnumerable<IrAccess> Of(IrPlace place)
    {
        if (place.IsDeref)
            yield return Whole(place.Local);
        foreach (var element in place.Projections.OfType<IrElement>())
        {
            if (element.Index is IrRead index)
            {
                foreach (var access in Of(index.Place))
                    yield return access;
            }
        }
        yield return Target(place);
    }

    /// <summary>What finding a place reads, without reading what is in it: the locals its base and indices are in.</summary>
    public static IEnumerable<IrAccess> Finding(IrPlace place) => Of(place).SkipLast(1);

    /// <summary>
    /// The storage a place lies in. What a reference or pointer refers to is unknown, unless
    /// <paramref name="referents"/> knows it for the local.
    /// </summary>
    public static IrAccess Target(IrPlace place, Func<IrLocal, IrAccess?>? referents = null)
    {
        var local = place.Local;
        if (!place.IsDeref)
        {
            // A struct method's `this` is its caller's storage.
            return local.IsThis && !local.Type.IsReference ? Unknown : new(IrStorage.Frame, local, null, place.Projections);
        }
        if (local.Deref == IrDeref.Object)
        {
            var first = place.Projections.FirstOrDefault() as IrField;
            return new(IrStorage.Field, local, first?.Field, [.. place.Projections.Skip(first is null ? 0 : 1)]);
        }
        if (referents?.Invoke(local) is { Storage: not IrStorage.Unknown } target)
            return target with { Path = [.. target.Path, .. place.Projections] };
        return Unknown;
    }

    /// <summary>Whether they may be the same storage: one path leads into the other, where elements may be the same whatever their indices.</summary>
    public bool Overlaps(IrAccess other) => (Storage, other.Storage) switch
    {
        (IrStorage.Unknown, _) or (_, IrStorage.Unknown) => true,
        (IrStorage.Frame, IrStorage.Frame) => Local == other.Local && PathsOverlap(Path, other.Path),
        (IrStorage.Field, IrStorage.Field) => Field is null || other.Field is null || (Field == other.Field && PathsOverlap(Path, other.Path)),
        _ => false,
    };

    static bool PathsOverlap(IReadOnlyList<IrProjection> a, IReadOnlyList<IrProjection> b)
    {
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            bool same = (a[i], b[i]) switch
            {
                (IrField x, IrField y) => x.Field == y.Field,
                (IrVariantField x, IrVariantField y) => x.Field == y.Field,
                (IrElement, IrElement) or (IrAssumed, IrAssumed) => true,
                _ => false,
            };
            if (!same)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Whether writing this may replace a nullable value or variant that <paramref name="place"/>
    /// lies in, destroying it: it overlaps the place, and stops short of a projection into one.
    /// </summary>
    public bool Destroys(IrAccess place)
    {
        int last = place.Path.ToList().FindLastIndex(p => p is IrAssumed or IrVariantField);
        return last >= 0 && Path.Count <= last && Overlaps(place);
    }

    /// <summary>Whether it lies in a nullable value or variant, which writing what holds it may destroy.</summary>
    public bool IsDestructible => Path.Any(p => p is IrAssumed or IrVariantField);

    /// <summary>
    /// Whether code the function runs may write it: a <c>mut</c> field, storage the function can't
    /// see, or a local a lambda assigns. Only a class's own constructors assign a field that isn't
    /// <c>mut</c>, and only by storing to it themselves.
    /// </summary>
    public bool WrittenByCode(IIrLambdaChanges lambdas) => Storage switch
    {
        IrStorage.Frame => lambdas.MayChange(Local!),
        IrStorage.Field => Field is null || Field.IsMutable,
        _ => true,
    };
}

/// <summary>What an instruction, or code nested into one, may do, for deciding whether two may run in either order.</summary>
public sealed class IrFootprint
{
    public List<IrAccess> Reads { get; } = [];
    public List<IrAccess> Writes { get; } = [];

    /// <summary>The code it may run, which the whole-program analyses can resolve.</summary>
    public HashSet<IrRun> Runs { get; } = [];

    public bool RunsCode => Runs.Count > 0;
    public bool MayPanic { get; set; }

    /// <summary>It makes a value whose release may run a destructor, so its creation order is observable.</summary>
    public bool CreatesOwner { get; set; }

    public void Add(IrFootprint other)
    {
        Reads.AddRange(other.Reads);
        Writes.AddRange(other.Writes);
        Runs.UnionWith(other.Runs);
        MayPanic |= other.MayPanic;
        CreatesOwner |= other.CreatesOwner;
    }

    public void Add(IrEffects effects)
    {
        foreach (var write in effects.Writes)
            Writes.Add(IrAccess.Target(write.Place));
        Runs.UnionWith(effects.Runs);
        MayPanic |= effects.MayPanic;
    }

    /// <summary>
    /// Whether running one and the other in either order may differ: one writes what the other
    /// touches, code runs while the other touches what code may write, both may panic (which one
    /// is reported is observable), one panics while the other may do I/O, or both make values whose
    /// release order is observable.
    /// </summary>
    public bool ConflictsWith(IrFootprint other, IIrLambdaChanges lambdas) =>
        (RunsCode && other.RunsCode)
        || (MayPanic && (other.MayPanic || other.RunsCode))
        || (other.MayPanic && RunsCode)
        || (CreatesOwner && other.CreatesOwner)
        || Disturbs(other, lambdas) || other.Disturbs(this, lambdas);

    bool Disturbs(IrFootprint other, IIrLambdaChanges lambdas) =>
        Writes.Any(w => other.Reads.Any(w.Overlaps) || other.Writes.Any(w.Overlaps))
        || (RunsCode && (other.Reads.Any(r => r.WrittenByCode(lambdas)) || other.Writes.Any(w => w.WrittenByCode(lambdas))));
}

public static class IrWalk
{
    /// <summary>The places an rvalue names directly, in evaluation order: operands, arguments, and what it refers into.</summary>
    public static IEnumerable<IrPlace> Places(IrRvalue value) => Operands(value).SelectMany(Places);

    public static IEnumerable<IrPlace> Places(IrArgument argument) => argument switch
    {
        IrRead read => [read.Place],
        IrRefArgument reference => [reference.Place],
        IrAddrArgument address => [address.Place],
        _ => [],
    };

    /// <summary>The receiver, arguments, and operands of an rvalue, in evaluation order, with its places as arguments.</summary>
    public static IEnumerable<IrArgument> Operands(IrRvalue value) => value switch
    {
        IrUse use => [use.Operand],
        IrUnary unary => [unary.Operand],
        IrBinary binary => [binary.Left, binary.Right],
        IrCall call => call.Receiver is null ? call.Arguments : [call.Receiver, .. call.Arguments],
        IrInvoke invoke => [invoke.Callee, .. invoke.Arguments],
        IrNew creation => creation.Arguments,
        IrConstruct construction => construction.Arguments,
        IrVariant variant => variant.Fields,
        IrArray array => array.Elements,
        IrClosure closure => closure.Captures,
        IrChoose choice => [choice.Index, .. choice.Options],
        IrRef reference => [new IrRefArgument(reference.Kind, reference.Place)],
        IrAddr address => [new IrAddrArgument(address.Kind, address.Place)],
        IrData data => [new IrRefArgument(RefKind.In, data.Place)],
        IrTest test => [test.Operand],
        IrConvert conversion => [conversion.Operand],
        IrHold hold => [IrRead.Borrow(hold.Place)],
        IrDefault or IrZeroed or IrLambda => [],
        _ => throw new InvalidOperationException($"unexpected IR rvalue {value.GetType().Name}"),
    };

    /// <summary>What a statement evaluates itself, in order, not counting the statements inside it.</summary>
    public static IEnumerable<IrArgument> Operands(IrStatement statement) => statement switch
    {
        IrLet let => Operands(let.Value),
        IrStore store => [new IrRefArgument(RefKind.Ref, store.Target), store.Value],
        IrDo run => Operands(run.Value),
        IrCheck check => check.Operands,
        IrIf conditional => [conditional.Condition],
        IrReturn { Value: { } value } => [value],
        _ => [],
    };

    /// <summary>A statement and every statement inside it, in order, including in lambda bodies unless not <paramref name="intoLambdas"/>.</summary>
    public static IEnumerable<IrStatement> Descendants(IrStatement statement, bool intoLambdas = true)
    {
        yield return statement;
        IEnumerable<IrStatement> inner = statement switch
        {
            IrBlock block => block.Statements,
            IrIf conditional => conditional.Else is null ? [conditional.Then] : [conditional.Then, conditional.Else],
            IrLoop loop => loop.Continue is null ? [loop.Body] : [loop.Body, loop.Continue],
            IrLet { Value: IrLambda lambda } when intoLambdas => [lambda.Function.Body],
            IrLet { Value: IrClosure closure } when intoLambdas => [closure.Function.Body],
            _ => [],
        };
        foreach (var child in inner)
        {
            foreach (var descendant in Descendants(child, intoLambdas))
                yield return descendant;
        }
    }

    /// <summary>The locals a statement, or one inside it, names.</summary>
    public static IEnumerable<IrLocal> Locals(IrStatement statement) => Descendants(statement).SelectMany(OwnLocals);

    static IEnumerable<IrLocal> OwnLocals(IrStatement statement)
    {
        IEnumerable<IrLocal> named = statement switch
        {
            IrLet let => [let.Local, .. Slot(let.Value)],
            IrDeclare declare => [declare.Local],
            IrEnd end => [end.Local],
            IrDo run => Slot(run.Value),
            _ => [],
        };
        return named.Concat(Operands(statement).SelectMany(Places).SelectMany(PlaceLocals));
    }

    static IEnumerable<IrLocal> Slot(IrRvalue value) => value switch
    {
        IrNew { Slot: { } slot } => [slot],
        IrClosure { Slot: { } slot } => [slot],
        _ => [],
    };

    /// <summary>Every place a statement, or one inside it, names, and those its indices read.</summary>
    public static IEnumerable<IrPlace> AllPlaces(IrStatement statement) =>
        Descendants(statement).SelectMany(Operands).SelectMany(Places).SelectMany(WithIndices);

    static IEnumerable<IrPlace> WithIndices(IrPlace place) =>
        [place, .. place.Projections.OfType<IrElement>().Select(e => e.Index).OfType<IrRead>().SelectMany(i => WithIndices(i.Place))];

    /// <summary>The locals a place names: its base and its indices.</summary>
    public static IEnumerable<IrLocal> PlaceLocals(IrPlace place) =>
        [place.Local, .. place.Projections.OfType<IrElement>().Select(e => e.Index).OfType<IrRead>().SelectMany(i => PlaceLocals(i.Place))];

    /// <summary>
    /// What a statement and everything inside it may do, including the releases at the ends of the
    /// blocks inside it. A lambda's body is what the calls that may run it do.
    /// </summary>
    public static IrFootprint Footprint(IrStatement statement)
    {
        var footprint = new IrFootprint();
        foreach (var inner in Descendants(statement, intoLambdas: false))
        {
            switch (inner)
            {
                case IrBlock block:
                    footprint.Add(IrEffects.Ending(block));
                    break;
                case IrIf or IrLoop:
                    break;
                default:
                    footprint.Add(IrEffects.Of(inner));
                    break;
            }
            footprint.Reads.AddRange(Reads(inner));
        }
        return footprint;
    }

    /// <summary>What a statement reads itself: what its operands read, and the paths to the places it binds or stores to.</summary>
    public static IEnumerable<IrAccess> Reads(IrStatement statement) => PlaceReads(statement).SelectMany(r => r.Accesses);

    /// <summary><see cref="Reads"/>, by the place each is a read of.</summary>
    public static IEnumerable<(IrPlace Place, IEnumerable<IrAccess> Accesses)> PlaceReads(IrStatement statement) => Operands(statement).SelectMany(argument => argument switch
    {
        IrRead read => [(read.Place, IrAccess.Of(read.Place))],
        IrRefArgument reference => [(reference.Place, IrAccess.Finding(reference.Place))],
        IrAddrArgument address => [(address.Place, IrAccess.Finding(address.Place))],
        _ => Array.Empty<(IrPlace, IEnumerable<IrAccess>)>(),
    });
}
