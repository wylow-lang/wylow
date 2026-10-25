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
/// The storage an instruction reads or writes, at the precision that ordering and hold elision
/// need: two accesses may be the same storage if they <see cref="Overlaps"/>.
/// </summary>
public readonly record struct IrAccess(IrStorage Storage, IrLocal? Local = null, FieldSymbol? Field = null)
{
    public static readonly IrAccess Unknown = new(IrStorage.Unknown);

    /// <summary>What reading or writing a place touches: the storage it lies in, and the locals read to find it.</summary>
    public static IEnumerable<IrAccess> Of(IrPlace place)
    {
        if (place.IsDeref)
            yield return new(IrStorage.Frame, place.Local);
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
    public static IEnumerable<IrAccess> Path(IrPlace place) => Of(place).SkipLast(1);

    /// <summary>The storage a place lies in.</summary>
    public static IrAccess Target(IrPlace place)
    {
        var local = place.Local;
        if (!place.IsDeref)
            return local.IsThis && !local.Type.IsReference ? Unknown : new(IrStorage.Frame, local);
        bool isObject = local.Kind != IrLocalKind.Reference && local.Type.NonNull.IsReference;
        return isObject ? new(IrStorage.Field, local, (place.Projections.FirstOrDefault() as IrField)?.Field) : Unknown;
    }

    public bool Overlaps(IrAccess other) => (Storage, other.Storage) switch
    {
        (IrStorage.Unknown, _) or (_, IrStorage.Unknown) => true,
        (IrStorage.Frame, IrStorage.Frame) => Local == other.Local,
        (IrStorage.Field, IrStorage.Field) => Field is null || other.Field is null || Field == other.Field,
        _ => false,
    };

    /// <summary>
    /// Whether code the function runs may write it: a <c>mut</c> field, storage the function can't
    /// see, or a local a lambda assigns. Only a class's own constructors assign a field that isn't
    /// <c>mut</c>, and only by storing to it themselves.
    /// </summary>
    public bool WrittenByCode => Storage switch
    {
        IrStorage.Frame => Local!.IsAssignedByLambda,
        IrStorage.Field => Field is null || Field.IsMutable,
        _ => true,
    };
}

/// <summary>What an instruction, or code nested into one, may do, for deciding whether two may run in either order.</summary>
public sealed class IrFootprint
{
    public List<IrAccess> Reads { get; } = [];
    public List<IrAccess> Writes { get; } = [];
    public bool RunsCode { get; set; }
    public bool MayPanic { get; set; }

    /// <summary>It makes a value whose release may run a destructor, so its creation order is observable.</summary>
    public bool CreatesOwner { get; set; }

    public void Add(IrFootprint other)
    {
        Reads.AddRange(other.Reads);
        Writes.AddRange(other.Writes);
        RunsCode |= other.RunsCode;
        MayPanic |= other.MayPanic;
        CreatesOwner |= other.CreatesOwner;
    }

    public void Add(IrEffects effects)
    {
        foreach (var write in effects.Writes)
            Writes.Add(IrAccess.Target(write.Place));
        RunsCode |= effects.RunsCode;
        MayPanic |= effects.MayPanic;
    }

    /// <summary>
    /// Whether running one and the other in either order may differ: one writes what the other
    /// touches, code runs while the other touches what code may write, both may panic (which one
    /// is reported is observable), one panics while the other may do I/O, or both make values whose
    /// release order is observable.
    /// </summary>
    public bool ConflictsWith(IrFootprint other) =>
        (RunsCode && other.RunsCode)
        || (MayPanic && (other.MayPanic || other.RunsCode))
        || (other.MayPanic && RunsCode)
        || (CreatesOwner && other.CreatesOwner)
        || Disturbs(other) || other.Disturbs(this);

    bool Disturbs(IrFootprint other) =>
        Writes.Any(w => other.Reads.Any(w.Overlaps) || other.Writes.Any(w.Overlaps))
        || (RunsCode && (other.Reads.Any(r => r.WrittenByCode) || other.Writes.Any(w => w.WrittenByCode)));
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
        IrDefault or IrLambda => [],
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

    /// <summary>A statement and every statement inside it, in order, including in lambda bodies.</summary>
    public static IEnumerable<IrStatement> Descendants(IrStatement statement)
    {
        yield return statement;
        IEnumerable<IrStatement> inner = statement switch
        {
            IrBlock block => block.Statements,
            IrIf conditional => conditional.Else is null ? [conditional.Then] : [conditional.Then, conditional.Else],
            IrLoop loop => loop.Continue is null ? [loop.Body] : [loop.Body, loop.Continue],
            IrLet { Value: IrLambda lambda } => [lambda.Function.Body],
            IrLet { Value: IrClosure closure } => [closure.Function.Body],
            _ => [],
        };
        foreach (var child in inner)
        {
            foreach (var descendant in Descendants(child))
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

    /// <summary>The locals a place names: its base and its indices.</summary>
    public static IEnumerable<IrLocal> PlaceLocals(IrPlace place) =>
        [place.Local, .. place.Projections.OfType<IrElement>().Select(e => e.Index).OfType<IrRead>().SelectMany(i => PlaceLocals(i.Place))];

    /// <summary>What a statement and everything inside it may do, including the releases at the ends of the blocks inside it.</summary>
    public static IrFootprint Footprint(IrStatement statement)
    {
        var footprint = new IrFootprint();
        foreach (var inner in Descendants(statement))
        {
            switch (inner)
            {
                case IrBlock block:
                    if (block.Statements.Any(s => s is IrLet { Local: var l } && IrEffects.ReleaseMayRunCode(l) || s is IrDeclare { Local: var d } && IrEffects.ReleaseMayRunCode(d)))
                        footprint.RunsCode = true;
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
    public static IEnumerable<IrAccess> Reads(IrStatement statement) => Operands(statement).SelectMany(argument => argument switch
    {
        IrRead read => IrAccess.Of(read.Place),
        IrRefArgument reference => IrAccess.Path(reference.Place),
        IrAddrArgument address => IrAccess.Path(address.Place),
        _ => [],
    });
}
