using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Ir;

/// <summary>
/// A place an instruction may write: exactly, as a store's target, or, with <see cref="Within"/>,
/// somewhere in it, as a callee may through a <c>ref</c> argument.
/// </summary>
public sealed record IrWrite(IrPlace Place, bool Within);

/// <summary>
/// What one instruction may do: the places it writes itself, whether it may run code (a call, or
/// a release that may run a destructor), which may write anything that isn't fixed, and whether it
/// may panic. Every analysis and the verifier learn what instructions do from here alone. It is a
/// visitor, so a new kind of instruction can't compile without an entry. A compound statement does
/// what its statements do.
/// </summary>
public sealed record IrEffects(IReadOnlyList<IrWrite> Writes, bool RunsCode, bool MayPanic)
{
    public static readonly IrEffects None = new([], false, false);

    public static IrEffects Of(IrStatement statement) => statement.Accept(StatementEffects.Instance);

    public static IrEffects Of(IrRvalue value) => value.Accept(RvalueEffects.Instance);

    /// <summary>Whether a local's release when its scope or an <see cref="IrEnd"/> ends it may destroy an object.</summary>
    public static bool ReleaseMayRunCode(IrLocal local) => local.Kind switch
    {
        IrLocalKind.Hold or IrLocalKind.Slot => true,
        IrLocalKind.Value => local.Type.MayHoldReferences,
        IrLocalKind.Reference or IrLocalKind.Borrowed => false,
        _ => throw new InvalidOperationException($"unexpected local kind {local.Kind}"),
    };

    /// <summary>
    /// Whether making the type's default value may run code: a struct's field initializers can call
    /// functions, and a type parameter may be such a struct.
    /// </summary>
    public static bool DefaultRunsCode(VplType type) =>
        type.IsTypeParameter
        || (type.IsFixedArray && DefaultRunsCode(type.ElementType!))
        || (type.IsStruct && !type.Symbol!.IsExtern && type.Symbol.Fields.Any(f =>
            f.Initializer is not (null or BoundLiteral or BoundNullLiteral) || (f.Initializer is null && DefaultRunsCode(TypeMap.For(type).Apply(f.Type)))));

    sealed class StatementEffects : IIrStatementVisitor<IrEffects>
    {
        public static readonly StatementEffects Instance = new();

        public IrEffects Let(IrLet statement) => Of(statement.Value);

        // The old value is released after the new one is stored.
        public IrEffects Store(IrStore statement) =>
            new([new IrWrite(statement.Target, Within: false)], statement.Target.Type.MayHoldReferences, false);

        public IrEffects Do(IrDo statement) => Of(statement.Value);
        public IrEffects Check(IrCheck statement) => new([], false, true);
        public IrEffects End(IrEnd statement) => new([], ReleaseMayRunCode(statement.Local), false);
        public IrEffects Unreachable(IrUnreachable statement) => new([], false, true);
        public IrEffects Declare(IrDeclare statement) => None;
        public IrEffects Return(IrReturn statement) => None;
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

        public IrEffects Call(IrCall value) => Calling(value.Method, value.Receiver, value.Arguments, runsCode: !value.Method.Symbol.IsPure);
        public IrEffects Invoke(IrInvoke value) => Calling(value.Signature, null, value.Arguments, runsCode: true);
        public IrEffects New(IrNew value) => Calling(value.Constructor, null, value.Arguments, runsCode: true);

        // An implicit constructor makes the default value; a memberwise one may run field initializers.
        public IrEffects Construct(IrConstruct value) =>
            Calling(value.Constructor, null, value.Arguments, runsCode: !value.Constructor.Symbol.IsImplicit || DefaultRunsCode(value.Type));

        public IrEffects Unary(IrUnary value) => new([], false, value.Loc is not null);
        public IrEffects Binary(IrBinary value) => new([], false, value.Loc is not null);
        public IrEffects Convert(IrConvert value) => new([], false, value.Loc is not null);

        // Borrowing calls the object's beginBorrow.
        public IrEffects Hold(IrHold value) => new([], value.Borrows, false);

        public IrEffects Use(IrUse value) => None;
        public IrEffects Variant(IrVariant value) => None;
        public IrEffects Array(IrArray value) => None;
        public IrEffects Default(IrDefault value) => DefaultRunsCode(value.Type) ? new([], true, true) : None;
        public IrEffects Closure(IrClosure value) => None;
        public IrEffects Lambda(IrLambda value) => None;
        public IrEffects Choose(IrChoose value) => None;
        public IrEffects Ref(IrRef value) => None;
        public IrEffects Addr(IrAddr value) => None;
        public IrEffects Data(IrData value) => None;
        public IrEffects Test(IrTest value) => None;

        static IrEffects Calling(MethodReference method, IrArgument? receiver, IReadOnlyList<IrArgument> arguments, bool runsCode)
        {
            IEnumerable<IrArgument> passed = receiver is null ? arguments : [receiver, .. arguments];
            var written = passed.Select(a => a switch
            {
                IrRefArgument { Kind: RefKind.Ref } reference => reference.Place,
                IrAddrArgument { Kind: RefKind.Ref } address => address.Place,
                _ => null,
            }).OfType<IrPlace>();
            return new([.. written.Select(p => new IrWrite(p, Within: true))], runsCode, runsCode || method.Symbol.PassesLocation);
        }
    }
}
