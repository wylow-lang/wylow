using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.Ir;

/// <summary>
/// A place an instruction may write: as a store's target, or somewhere in it, as a callee may
/// through a <c>ref</c> argument; a write inside a place overlaps it either way (<see cref="IrAccess.Overlaps"/>).
/// </summary>
public sealed record IrWrite(IrPlace Place);

/// <summary>
/// Code an instruction may run, by what it is rather than which functions: the whole-program
/// analyses resolve it to those (<c>Callees</c>).
/// </summary>
public abstract record IrRun;

/// <summary>A method, or if the call dispatches and isn't <paramref name="Direct"/>, whatever overrides or implements it.</summary>
public sealed record IrRunMethod(MethodReference Method, bool Direct) : IrRun;

/// <summary>A lambda that a function value of the type may be.</summary>
public sealed record IrRunFunctionValue(WylType Type) : IrRun;

/// <summary>The field initializers that making the type's default value runs.</summary>
public sealed record IrRunDefault(WylType Type) : IrRun;

/// <summary>
/// What releasing a value of the type, or with none, any value, may run: the destructors of the
/// objects it may destroy, and a <c>Borrowable</c>'s <c>beginBorrow</c> and <c>endBorrow</c>
/// around a borrow of one.
/// </summary>
public sealed record IrRunRelease(WylType? Type) : IrRun;

/// <summary>
/// What one instruction may do: the places it writes itself, the code it may run (a call, or a
/// release that may run a destructor), which may write anything that isn't fixed, and whether it
/// may panic. Every analysis and the verifier learn what instructions do from here alone. It is a
/// visitor, so a new kind of instruction can't compile without an entry. A compound statement does
/// what its statements do.
/// </summary>
public sealed record IrEffects(IReadOnlyList<IrWrite> Writes, IReadOnlyList<IrRun> Runs, bool MayPanic)
{
    public static readonly IrEffects None = new([], [], false);

    /// <summary>What releasing a value of the type may do.</summary>
    public static IrEffects Releasing(WylType type) => new([], [new IrRunRelease(type)], false);

    /// <summary>What a local's release may do, when its scope or an <see cref="IrEnd"/> ends it.</summary>
    public static IrEffects Releasing(IrLocal local) => !ReleaseMayRunCode(local) ? None
        // A holder's object may be of any type.
        : local.Kind == IrLocalKind.Holder ? new([], [new IrRunRelease(null)], false)
        : Releasing(local.Type);

    /// <summary>What releasing the locals a block declares, other than <paramref name="except"/>, when it ends, may do.</summary>
    public static IrEffects Ending(IrBlock block, IrLocal? except = null)
    {
        var runs = IrWalk.Declared(block).Where(l => l != except).SelectMany(l => Releasing(l).Runs).Distinct().ToList();
        return runs.Count == 0 ? None : new([], runs, false);
    }

    public bool RunsCode => Runs.Count > 0;

    /// <summary>
    /// What a statement does itself, not counting the statements inside it: a block releases what
    /// it declares as it ends, and an <c>if</c> or loop does nothing more.
    /// </summary>
    public static IrEffects Itself(IrStatement statement) => statement switch
    {
        IrBlock block => Ending(block),
        IrIf or IrLoop => None,
        _ => Of(statement),
    };

    /// <summary>
    /// What a statement that isn't compound does. A local it moves out of is left empty, so it is
    /// written, and a read of it that C++ may order differently conflicts; with <paramref name="moves"/>
    /// false, that write is left out.
    /// </summary>
    public static IrEffects Of(IrStatement statement, bool moves = true)
    {
        var effects = statement.Accept(StatementEffects.Instance);
        if (!moves)
            return effects;
        var moved = IrWalk.Operands(statement).OfType<IrRead>()
            .Where(r => r.Ownership == IrOwnership.Move && !r.Place.Local.IsTemporary).Select(r => new IrWrite(r.Place)).ToList();
        return moved.Count == 0 ? effects : effects with { Writes = [.. effects.Writes, .. moved] };
    }

    public static IrEffects Of(IrRvalue value) => value.Accept(RvalueEffects.Instance);

    /// <summary>Whether a local's release when its scope or an <see cref="IrEnd"/> ends it may destroy an object.</summary>
    public static bool ReleaseMayRunCode(IrLocal local) => local.Kind switch
    {
        IrLocalKind.Hold or IrLocalKind.Slot or IrLocalKind.Holder => true,
        IrLocalKind.Value => local.Type.MayOwnObjects || local.Type.RunsDestructor,
        IrLocalKind.Reference or IrLocalKind.Borrowed or IrLocalKind.InUse => false,
        _ => throw new InvalidOperationException($"unexpected local kind {local.Kind}"),
    };

    sealed class StatementEffects : IIrStatementVisitor<IrEffects>
    {
        public static readonly StatementEffects Instance = new();

        public IrEffects Let(IrLet statement) => Of(statement.Value);

        // The old value is released after the new one is stored.
        public IrEffects Store(IrStore statement) =>
            new([new IrWrite(statement.Target)], statement.Target.Type.MayOwnObjects || statement.Target.Type.RunsDestructor ? [new IrRunRelease(statement.Target.Type)] : [], false);

        public IrEffects Do(IrDo statement) => Of(statement.Value);
        public IrEffects Check(IrCheck statement) => new([], [], !statement.NeverRuns);
        public IrEffects End(IrEnd statement) => Releasing(statement.Local);
        // The value is moved, and its fields are released when the scope ends.
        public IrEffects Discard(IrDiscard statement) => new([new IrWrite(new IrPlace(statement.Local))], [], false);
        public IrEffects Unreachable(IrUnreachable statement) => new([], [], true);
        public IrEffects Declare(IrDeclare statement) => None;
        // The caller's holder takes its own hold of the object, and borrows it again if the hold borrows.
        public IrEffects Return(IrReturn statement) => statement.Holds.Count == 0 ? None
            : new([], [.. statement.Holds.Select(h => new IrRunRelease(h.Local.Kind == IrLocalKind.Holder ? null : h.Type)).Distinct()], false);
        public IrEffects Break(IrBreak statement) => None;
        public IrEffects Continue(IrContinue statement) => None;
        public IrEffects Block(IrBlock statement) => Compound();
        public IrEffects If(IrIf statement) => Compound();
        public IrEffects Loop(IrLoop statement) => Compound();

        static IrEffects Compound() => throw new ArgumentException("a compound statement's effects are those of its statements");
    }

    sealed class RvalueEffects : IIrRvalueVisitor<IrEffects>
    {
        public static readonly RvalueEffects Instance = new();

        public IrEffects Call(IrCall value) =>
            Calling(value, value.Method.Symbol.IsPure ? null : new IrRunMethod(value.Method, value.IsSuperCall));

        public IrEffects Invoke(IrInvoke value) => Calling(value, new IrRunFunctionValue(value.Callee.Type.NonNull));
        public IrEffects New(IrNew value) => Calling(value, new IrRunMethod(value.Constructor, Direct: true));

        // An implicit constructor makes the default value; a memberwise one may run field initializers.
        public IrEffects Construct(IrConstruct value) => Calling(value,
            !value.Constructor.Symbol.IsImplicit ? new IrRunMethod(value.Constructor, Direct: true) : value.Type.DefaultRunsCode ? new IrRunDefault(value.Type) : null);

        public IrEffects Unary(IrUnary value) => new([], [], value.Loc is not null);
        public IrEffects Binary(IrBinary value) => new([], [], value.Loc is not null);
        public IrEffects Convert(IrConvert value) =>
            value.Type.Symbol is { IsIsolated: true } && value.Type.DefaultRunsCode ? new([], [new IrRunDefault(value.Type)], true) : new([], [], value.Loc is not null);

        // Borrowing calls the object's beginBorrow.
        public IrEffects Hold(IrHold value) => value.Borrows ? Releasing(value.Type) : None;
        public IrEffects InUse(IrInUse value) => None;

        public IrEffects Use(IrUse value) => None;
        public IrEffects Variant(IrVariant value) => None;
        public IrEffects Array(IrArray value) => None;
        public IrEffects Default(IrDefault value) => value.Type.DefaultRunsCode ? new([], [new IrRunDefault(value.Type)], true) : None;
        public IrEffects Zeroed(IrZeroed value) => None;
        public IrEffects Closure(IrClosure value) => None;
        public IrEffects Lambda(IrLambda value) => None;
        public IrEffects Choose(IrChoose value) => None;
        public IrEffects Ref(IrRef value) => None;
        public IrEffects Addr(IrAddr value) => None;
        public IrEffects Data(IrData value) => None;
        public IrEffects Test(IrTest value) => None;

        static IrEffects Calling(IrRvalue value, IrRun? runs)
        {
            var passed = IrWalk.Passed(value)!.Value;
            var written = passed.All.Select(a => a switch
            {
                IrRefArgument { Kind: RefKind.Ref } reference => reference.Place,
                IrAddrArgument { Kind: RefKind.Ref } address => address.Place,
                _ => null,
            }).OfType<IrPlace>();
            // The callee gives the holder what the returned reference points into.
            if (value is IrCall { Holder: { } holder })
                written = written.Append(new IrPlace(holder));
            return new([.. written.Select(p => new IrWrite(p))], runs is null ? [] : [runs], runs is not null || passed.Method.Symbol.PassesLocation);
        }
    }
}
