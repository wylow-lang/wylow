using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Lowering;

/// <summary>
/// Lowers checked functions into the IR (docs/design/ir.md, §5). Lowering asks nothing about what
/// may change: every object a place goes through is held, every operand is copied into a temporary
/// at its turn, every by-value argument is a copy the statement keeps, and every statement's
/// temporaries are in a block of its own. The mandatory passes (<see cref="Passes"/>) decide what
/// the language says lowering can't, and the optimizations undo what nothing needs.
/// </summary>
public sealed partial class IrLowering
{
    readonly BoundProgram program;

    // A lambda's body is lowered by the lowering of the function making it, which these then describe.
    FunctionSymbol symbol;
    IrFunction function;
    IrLocal? self;
    readonly List<IrFunction> lambdas = [];
    readonly Dictionary<LocalSymbol, IrLocal> locals = [];

    /// <summary>The statement or expression being lowered, which the statements made for it come from.</summary>
    Location? at;

    /// <summary>Where each instruction was lowered from, which a statement made of it later comes from.</summary>
    readonly Dictionary<IrRvalue, Location> valueOrigins = new(ReferenceEqualityComparer.Instance);
    IrBlock block;
    bool reachable = true;

    IrLowering(BoundProgram program, FunctionSymbol symbol, string? name = null)
    {
        this.program = program;
        this.symbol = symbol;
        var owner = symbol.ContainingType;
        function = new IrFunction(symbol, name ?? (owner is null ? symbol.Name : $"{owner.Name}.{symbol.Name}"));
        block = function.Body;
        if (owner is not null && !symbol.IsStatic)
            function.This = self = new IrLocal("this", owner.Type, IrLocalKind.Value, IrLocalRole.This);
        foreach (var parameter in symbol.Parameters)
            function.Parameters.Add(locals[parameter] = new IrLocal(parameter.Name, parameter.Type, KindOf(parameter), IrLocalRole.Parameter) { Symbol = parameter, RefKind = parameter.RefKind });
    }

    public static IrProgram Lower(BoundProgram program)
    {
        var ir = new IrProgram(program);
        void Add(FunctionSymbol symbol, string? name, Func<IrLowering, IrFunction> lower)
        {
            var lowering = new IrLowering(program, symbol, name);
            ir.Functions.Add(lower(lowering));
            ir.Functions.AddRange(lowering.lambdas);
        }

        foreach (var function in program.Functions.Where(f => !f.Symbol.IsMemberwise))
            Add(function.Symbol, null, lowering => lowering.Function(function));
        foreach (var s in program.Types.Where(s => s.Kind == TypeKind.Struct && s.Type.HasDefaultCode))
        {
            var symbol = new FunctionSymbol("default", [], WylType.Void) { ContainingType = s, IsConstructor = true };
            Add(symbol, $"{s.Name}.default", lowering => lowering.Built(null, [.. s.Fields.Select(_ => false)]));
        }
        var forms = MemberwiseForms(program);
        foreach (var function in program.Functions.Where(f => f.Symbol.IsMemberwise))
        {
            foreach (var form in forms.GetValueOrDefault(function.Symbol) ?? [])
                Add(function.Symbol, $"{function.Symbol.ContainingType!.Name}.{function.Symbol.Name}[{FormKey(form)}]", lowering => lowering.Built(function, form));
        }
        return ir;
    }

    /// <summary>
    /// The mandatory passes, in the order they run on the lowered program. The IR after them is the
    /// program's meaning.
    /// </summary>
    public static IReadOnlyList<MandatoryPass> Passes { get; } = [new("deciding borrows", Borrows.Apply)];

    static IrLocalKind KindOf(LocalSymbol local) =>
        local.RefKind != RefKind.None || local.Type.IsNullableReference ? IrLocalKind.Reference : IrLocalKind.Value;

    IrFunction Function(BoundFunction bound)
    {
        function.Body = block = new IrBlock { Origin = bound.Body.Location };
        function.HasLocationParameter = symbol.PassesLocation;
        var statements = bound.Body.Statements;
        if (symbol.IsConstructor)
            statements = ConstructorStart(statements);
        FunctionBody(statements);
        return function;
    }

    /// <summary>Lowers a function's or lambda's body into <see cref="function"/>'s.</summary>
    void FunctionBody(BoundBlock body) => FunctionBody(body.Statements);

    void FunctionBody(IReadOnlyList<BoundStatement> statements)
    {
        foreach (var statement in statements)
        {
            if (!reachable)
                break;
            Statement(statement);
        }
        // The flow analysis reports a function that returns a value and whose end is reachable.
        if (reachable && function.Struct is { } made)
            Add(new IrReturn(IrRead.Copy(new IrPlace(made))) { Origin = null });
    }

    void Add(IrStatement statement) => block.Statements.Add(statement);

    Location? Origin(IrRvalue value) => valueOrigins.TryGetValue(value, out var made) ? made : at;

    IrLet LetOf(IrLocal local, IrRvalue value) => new(local, value) { Origin = Origin(value) };

    IrDo DoOf(IrRvalue value) => new(value) { Origin = Origin(value) };

    /// <summary>A temporary declared without a value, which control flow stores.</summary>
    static IrLocal Temp(WylType type) => new("t", type, IrLocalKind.Value, IrLocalRole.Temporary);

    /// <summary>A temporary holding the value, which the instructions after it read.</summary>
    IrLocal Let(IrRvalue value)
    {
        var let = new IrLet(value) { Origin = Origin(value) };
        Add(let);
        return let.Local;
    }

    /// <summary>Where a panic at <paramref name="location"/> is reported: in an <c>@CallerLocation</c> function, where it was called from.</summary>
    IrLoc Loc(Location location) => function.HasLocationParameter ? IrLoc.Caller : new IrLoc(location);

    void Unreachable(Location location)
    {
        Add(new IrUnreachable(Loc(location)) { Origin = at });
        reachable = false;
    }

    void Statement(BoundStatement statement)
    {
        var enclosing = at;
        at = statement.Location;
        StatementOf(statement);
        at = enclosing;
    }

    void StatementOf(BoundStatement statement)
    {
        switch (statement)
        {
            case BoundBlock nested:
                var lowered = new IrBlock { Origin = at };
                Into(lowered, () =>
                {
                    foreach (var inner in nested.Statements)
                    {
                        if (!reachable)
                            break;
                        Statement(inner);
                    }
                });
                Add(lowered);
                break;
            case BoundVariableDeclaration declaration:
                Declaration(declaration);
                break;
            case BoundAssignment assignment:
                DeclarePatternLocals(assignment.Value);
                InStatement(statement, () => Assignment(assignment));
                break;
            case BoundExpressionStatement expression:
                DeclarePatternLocals(expression.Expression);
                InStatement(statement, () => ExpressionStatement(expression));
                break;
            case BoundReturn ret:
                DeclarePatternLocals(ret.Value);
                InStatement(statement, () => Return(ret));
                break;
            case BoundIf conditional:
                WithPatternLocals(conditional.Condition, () => If(conditional));
                break;
            case BoundWhile loop:
                WithPatternLocals(loop.Condition, () => Loop(loop.Condition, loop.Body, null), references: false);
                break;
            case BoundSwitch switchStatement:
                Switch(switchStatement);
                break;
            case BoundFor loop:
                For(loop);
                break;
            case BoundForRange range:
                ForRange(range);
                break;
            case BoundBreak or BoundContinue:
                Jump(statement);
                break;
            default:
                throw new InvalidOperationException($"unexpected {statement.GetType().Name}");
        }
    }

    void Into(IrBlock target, Action lower)
    {
        var saved = block;
        block = target;
        lower();
        block = saved;
    }

    /// <summary>A statement's temporaries are released when it completes (spec §3.2, When references are released), so they are declared in a block of its own.</summary>
    void InStatement(BoundStatement statement, Action lower)
    {
        var scope = new IrBlock { Origin = at, IsStatement = true };
        Into(scope, lower);
        Add(scope);
    }

    IrLocal Declare(LocalSymbol local, WylType? storage = null)
    {
        var lowered = new IrLocal(local.Name, storage ?? local.Type, KindOf(local), IrLocalRole.Variable) { Symbol = local, RefKind = local.RefKind };
        locals[local] = lowered;
        return lowered;
    }

    /// <summary>
    /// The escaping function value a non-escaping local is made with, or null. The local owns what it
    /// holds (spec functions.lambdas.escaping), so it is stored as the escaping form and
    /// releases the closure when its scope ends; the non-escaping form refers to a closure it doesn't own.
    /// </summary>
    BoundExpression? OwnedFunction(LocalSymbol symbol, BoundExpression initializer)
    {
        if (!symbol.Type.IsNonEscapingFunction)
            return null;
        return initializer switch
        {
            BoundConversion { Operand: { Type.IsEscaping: true } escaping } => escaping,
            BoundLocalAccess access when locals[access.Local].Type.IsEscaping => access,
            _ => null,
        };
    }

    void Declaration(BoundVariableDeclaration declaration)
    {
        var symbol = declaration.Local;
        var initializer = declaration.Initializer;
        DeclarePatternLocals(initializer);

        // A reference or view, and the holds of what it points into, last for the local's scope.
        if (symbol.RefKind != RefKind.None || symbol.Type.IsNullableReference || symbol.Type.IsRefStruct)
        {
            var value = symbol.Type.IsRefStruct ? Rvalue(initializer!, Use.Scope) : ReferenceTo(initializer!, symbol);
            Add(LetOf(Declare(symbol), value));
            return;
        }

        if (initializer is not null && OwnedFunction(symbol, initializer) is { } owned)
        {
            Define(Declare(symbol, ((FunctionType)symbol.Type).Escaping), owned);
            return;
        }

        var local = Declare(symbol);
        if (initializer is null)
        {
            // A fixed array holds its default if it has one; any other local is stored before it is read.
            Add(symbol.Type.IsFixedArray && symbol.Type.IsDefaultable ? LetOf(local, new IrDefault(symbol.Type)) : new IrDeclare(local) { Origin = at });
            return;
        }

        Define(local, initializer);
    }

    /// <summary>The rvalue as an operand a store or return can take: its value, moved out of a temporary if it computes one.</summary>
    IrOperand OperandOf(IrRvalue value) => value is IrUse { Operand: var operand }
        ? operand is IrRead { Ownership: IrOwnership.Borrow } read ? IrRead.Copy(read.Place) : operand
        : IrRead.Move(new IrPlace(Let(value)));

    /// <summary>What a <c>ref</c>, <c>in</c>, <c>ref?</c>, or <c>in?</c> local is initialized with.</summary>
    IrRvalue ReferenceTo(BoundExpression initializer, LocalSymbol local)
    {
        if (local.Type is not NullableReferenceType reference)
            return new IrRef(local.RefKind, Place(initializer, Use.Scope));
        var kind = reference.Kind;
        return initializer switch
        {
            BoundConversion { Operand.Type: var from } conversion when from == WylType.Null => new IrConvert(local.Type, new IrConstant(WylType.Null, null)),
            BoundConversion { Operand: var place } when !place.Type.IsNullableReference => new IrAddr(kind, Place(place, Use.Scope)),
            BoundConversion { Operand: var other } => ReferenceValue(other, local.Type),
            _ => ReferenceValue(initializer, local.Type),
        };
    }

    /// <summary>A <c>ref?</c> or <c>in?</c> value: another one, or one a method returns.</summary>
    IrRvalue ReferenceValue(BoundExpression value, WylType type)
    {
        var rvalue = Rvalue(value, Use.Scope);
        return rvalue.Type == type ? rvalue : new IrConvert(type, OperandOf(rvalue));
    }

    void ExpressionStatement(BoundExpressionStatement statement)
    {
        var expression = statement.Expression;
        if (expression is BoundConditionalAccess access && access.Type == WylType.Void)
        {
            ConditionalAccess(access);
            return;
        }
        var value = Rvalue(expression, Use.Read);
        Add(DoOf(value));
        if (value is IrCall { Method.Symbol.IsNoReturn: true })
            Unreachable(expression.Location);
    }

    void Return(BoundReturn ret)
    {
        if (ret.Value is not { } value)
        {
            Add(new IrReturn(function.Struct is { } made ? IrRead.Copy(new IrPlace(made)) : null) { Origin = at });
        }
        else if (symbol.ReturnRefKind != RefKind.None)
        {
            var reference = value switch
            {
                BoundNullLiteral or BoundConversion { Operand: BoundNullLiteral } => (IrOperand)new IrConstant(symbol.ReturnType, null),
                BoundConversion { Type.IsNullableReference: true, Operand: var place } when !place.Type.IsNullableReference =>
                    IrRead.Copy(new IrPlace(Let(new IrAddr(((NullableReferenceType)symbol.ReturnType).Kind, Place(place, Use.Scope))))),
                _ when symbol.ReturnType.IsNullableReference => IrRead.Copy(new IrPlace(Let(ReferenceValue(value, symbol.ReturnType)))),
                _ => IrRead.Copy(new IrPlace(Let(new IrRef(symbol.ReturnRefKind, Place(value, Use.Scope))))),
            };
            Add(new IrReturn(reference) { Origin = at });
        }
        else
        {
            Add(new IrReturn(OperandOf(Rvalue(value, Use.Read))) { Origin = at });
        }
        reachable = false;
    }

    void Assignment(BoundAssignment assignment)
    {
        if (assignment.Target is BoundIndexerElement element)
        {
            IndexerAssignment(element, assignment.Value);
            return;
        }
        // The target, its indices and their checks included, is found before the value, which reads
        // it from there in a compound assignment like `a[f()] += 1` (spec §3.3, Evaluation order and the heap).
        var target = Place(assignment.Target, Use.InUse);
        bool compound = BoundTree.AllExpressions(assignment.Value).Any(e => ReferenceEquals(e, assignment.Target));
        if (compound && target.Projections.Any(p => p is IrElement { Index: IrRead }))
            target = IrPlace.Deref(Let(new IrRef(RefKind.Ref, target)));
        located[assignment.Target] = target;
        var value = OperandOf(Rvalue(assignment.Value, Use.Read));
        located.Remove(assignment.Target);
        Add(new IrStore(target, value) { Origin = at });
    }

    /// <summary>
    /// An assignment through <c>operator []=</c>, which is a call: the receiver and indices are
    /// found once, before the value, and a compound assignment reads the element with the paired
    /// <c>operator []</c> in between (spec §3.3, Evaluation order and the heap).
    /// </summary>
    void IndexerAssignment(BoundIndexerElement element, BoundExpression value)
    {
        var read = element.Read;
        var write = element.Write;
        var (readReceiver, writeReceiver) = IndexerReceivers(read, write);
        var shared = read.Arguments.Select((argument, i) => SharedIndex(read.Method, i, argument)).ToList();
        if (BoundTree.AllExpressions(value).Any(e => ReferenceEquals(e, element)))
        {
            var result = Let(new IrCall(read.Method, readReceiver, [.. shared.Select(index => index())], LocationFor(read.Method, read)));
            located[element] = read.Method.Symbol.ReturnRefKind != RefKind.None ? IrPlace.Deref(result) : new IrPlace(result);
        }
        int last = shared.Count;
        var assigned = Argument(write.ParameterType(last), write.ParameterRefKind(last), value);
        located.Remove(element);
        if (assigned is IrOperand operand && write.ParameterRefKind(last) == RefKind.None && !write.ParameterType(last).IsNullableReference)
            assigned = Kept(operand);
        Add(DoOf(new IrCall(write, writeReceiver, [.. shared.Select(index => index()), assigned], LocationFor(write, element))));
    }

    /// <summary>The receiver both calls take, found once: an object held while they run, or a struct's place.</summary>
    (IrArgument Read, IrArgument Write) IndexerReceivers(BoundCall read, MethodReference write)
    {
        var receiver = read.Receiver!;
        var type = receiver.Type;
        bool returnsInto = read.Method.Symbol.ReturnRefKind != RefKind.None;
        if (type.IsReference)
        {
            var shared = IrRead.Borrow(new IrPlace(ObjectLocal(receiver, returnsInto && program.MayBeBorrowable(type))));
            return (shared, shared);
        }
        var place = IsPlace(receiver) ? Place(receiver, Use.InUse) : new IrPlace(Let(Rvalue(receiver, Use.Read)));
        if (type.SharesStorage && returnsInto)
        {
            // What the read returns points into the receiver, so it is held as a call returning into it holds it.
            var holder = Hold(place, program.MayBeBorrowable(type));
            return (IrRead.Borrow(new IrPlace(holder)),
                write.Symbol.IsReadOnly ? IrRead.Borrow(new IrPlace(holder)) : new IrRefArgument(RefKind.Ref, IrPlace.Deref(holder)));
        }
        return (new IrRefArgument(read.Method.Symbol.IsReadOnly ? RefKind.In : RefKind.Ref, place),
            new IrRefArgument(write.Symbol.IsReadOnly ? RefKind.In : RefKind.Ref, place));
    }

    /// <summary>
    /// An index argument both calls take, evaluated once: a by-value or <c>in</c> index is a
    /// temporary, so the value can't change it between them, and each call is passed its own kept
    /// copy, as any call is; a <c>ref</c> one refers to its place.
    /// </summary>
    Func<IrArgument> SharedIndex(MethodReference method, int index, BoundExpression argument)
    {
        var refKind = method.ParameterRefKind(index);
        var type = method.ParameterType(index);
        if (refKind == RefKind.In && !type.IsNullableReference && !type.IsRefStruct)
        {
            var temporary = new IrPlace(Let(new IrUse(Value(argument))));
            return () => new IrRefArgument(RefKind.In, temporary);
        }
        var lowered = Argument(type, refKind, argument);
        if (lowered is not IrRead read || refKind != RefKind.None || type.IsNullableReference)
            return () => lowered;
        var evaluated = new IrPlace(Let(new IrUse(read)));
        return () => Kept(IrRead.Copy(evaluated));
    }
}
