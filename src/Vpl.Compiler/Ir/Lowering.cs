using Vpl.Compiler.Analysis;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Ir;

/// <summary>
/// Lowers checked functions into the IR (docs/design/ir.md, §5). Lowering is conservative: every
/// object a place goes through is held, and every read that something after it might change is
/// copied into a temporary first. Holds nothing needs are elided later, by <see cref="Optimize.HoldElision"/>.
/// </summary>
public sealed partial class IrLowering
{
    readonly BoundProgram program;
    readonly Analyses analyses;
    readonly IReadOnlyList<TypeSymbol> borrowableClasses;

    // A lambda's body is lowered by the lowering of the function making it, which these then describe.
    FunctionSymbol symbol;
    IrFunction function;
    IrLocal? self;
    readonly List<IrFunction> lambdas = [];
    readonly Dictionary<IrLocal, Location> loopElements = [];
    readonly Dictionary<LocalSymbol, IrLocal> locals = [];
    readonly Dictionary<IrRvalue, Location> creations = new(ReferenceEqualityComparer.Instance);
    IrBlock block;
    bool reachable = true;

    IrLowering(BoundProgram program, Analyses analyses, IReadOnlyList<TypeSymbol> borrowableClasses, FunctionSymbol symbol, string? name = null)
    {
        this.program = program;
        this.analyses = analyses;
        this.borrowableClasses = borrowableClasses;
        this.symbol = symbol;
        var owner = symbol.ContainingType;
        function = new IrFunction(symbol, name ?? (owner is null ? symbol.Name : $"{owner.Name}.{symbol.Name}"));
        block = function.Body;
        if (owner is not null && !symbol.IsStatic)
            function.This = self = new IrLocal("this", owner.Type, IrLocalKind.Value) { IsThis = true, IsParameter = true };
        foreach (var parameter in symbol.Parameters)
            function.Parameters.Add(locals[parameter] = new IrLocal(parameter.Name, parameter.Type, KindOf(parameter)) { IsParameter = true, Symbol = parameter, RefKind = parameter.RefKind });
    }

    public static IrProgram Lower(BoundProgram program, Analyses analyses)
    {
        var ir = new IrProgram(program);
        var borrowable = program.Stdlib.Borrowable;
        List<TypeSymbol> borrowableClasses = [.. program.Types.Where(s => s.Kind == TypeKind.Class && borrowable is not null && Conversions.IsSubtype(s.Type, borrowable.Type))];
        void Add(FunctionSymbol symbol, string? name, Func<IrLowering, IrFunction> lower)
        {
            var lowering = new IrLowering(program, analyses, borrowableClasses, symbol, name);
            ir.Functions.Add(lower(lowering));
            ir.Functions.AddRange(lowering.lambdas);
            foreach (var (element, location) in lowering.loopElements)
                ir.LoopElements[element] = location;
            foreach (var (creation, location) in lowering.creations)
                ir.Creations[creation] = location;
        }

        foreach (var function in program.Functions.Where(f => !f.Symbol.IsMemberwise))
            Add(function.Symbol, null, lowering => lowering.Function(function));
        foreach (var s in program.Types.Where(s => s.Kind == TypeKind.Struct && HasDefaultCode(s.Type)))
        {
            var symbol = new FunctionSymbol("default", [], VplType.Void) { ContainingType = s, IsConstructor = true };
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

    static IrLocalKind KindOf(LocalSymbol local) =>
        local.RefKind != RefKind.None || local.Type.IsNullableReference ? IrLocalKind.Reference : IrLocalKind.Value;

    IrFunction Function(BoundFunction bound)
    {
        function.HasLocationParameter = symbol.PassesLocation;
        var statements = bound.Body.Statements;
        if (symbol.IsConstructor)
            statements = ConstructorStart(statements);
        FunctionBody(bound.Body, statements);
        return function;
    }

    /// <summary>Lowers a function's or lambda's body into <see cref="function"/>'s.</summary>
    void FunctionBody(BoundBlock body) => FunctionBody(body, body.Statements);

    void FunctionBody(BoundBlock body, IReadOnlyList<BoundStatement> statements)
    {
        foreach (var statement in statements)
        {
            if (!reachable)
                break;
            Statement(statement);
        }
        if (reachable && function.Struct is { } made)
            Add(new IrReturn(IrRead.Copy(new IrPlace(made))));
        else if (reachable && symbol.ReturnType != VplType.Void)
        {
            var location = body.Location;
            Unreachable(location with { Span = new TextSpan(location.Span.End - 1, 1) });
        }
    }

    void Add(IrStatement statement) => block.Statements.Add(statement);

    IrLocal Temp(VplType type, IrLocalKind kind = IrLocalKind.Value, RefKind refKind = RefKind.None) =>
        new("t", type, kind) { IsTemporary = true, RefKind = refKind };

    /// <summary>A temporary holding the value, which the instructions after it read.</summary>
    IrLocal Let(IrRvalue value)
    {
        var kind = value switch
        {
            IrRef or IrAddr => IrLocalKind.Reference,
            IrCall { Method.Symbol.ReturnRefKind: not RefKind.None } => IrLocalKind.Reference,
            IrConvert { Type.IsNullableReference: true } => IrLocalKind.Reference,
            IrUse { Operand: IrRead { Place: { IsDeref: false, Projections.Count: 0, Local.Kind: IrLocalKind.Reference } } } => IrLocalKind.Reference,
            IrHold => IrLocalKind.Hold,
            _ => IrLocalKind.Value,
        };
        var refKind = value switch
        {
            IrRef reference => reference.Kind,
            IrCall { Method.Symbol.ReturnRefKind: not RefKind.None and var returned } call when !call.Type.IsNullableReference => returned,
            IrUse { Operand: IrRead { Place.Local: { Kind: IrLocalKind.Reference } source } } when kind == IrLocalKind.Reference => source.RefKind,
            _ => RefKind.None,
        };
        var local = Temp(value.Type, kind, refKind);
        Add(new IrLet(local, value));
        return local;
    }

    /// <summary>Where a panic at <paramref name="location"/> is reported: in an <c>@CallerLocation</c> function, where it was called from.</summary>
    IrLoc Loc(Location location) => function.HasLocationParameter ? IrLoc.Caller : new IrLoc(location);

    void Unreachable(Location location)
    {
        Add(new IrUnreachable(Loc(location)));
        reachable = false;
    }

    void Statement(BoundStatement statement)
    {
        switch (statement)
        {
            case BoundBlock nested:
                var lowered = new IrBlock();
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
                WithPatternLocals(loop.Condition, () => Loop(loop.Condition, loop.Body, null));
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

    /// <summary>
    /// A statement's temporaries are released when it completes (spec §3.2, When references are
    /// released), so they are declared in a block of its own when releasing one may be observed.
    /// Otherwise they join the enclosing block, which no one can tell apart.
    /// </summary>
    void InStatement(BoundStatement statement, Action lower)
    {
        var scope = new IrBlock { IsStatement = true };
        Into(scope, lower);
        Splice(scope);
    }

    void Splice(IrBlock scope)
    {
        if (scope.DeclaresOwner())
            Add(scope);
        else
            block.Statements.AddRange(scope.Statements);
    }

    IrLocal Declare(LocalSymbol local)
    {
        var lowered = new IrLocal(local.Name, local.Type, KindOf(local)) { Symbol = local, RefKind = local.RefKind };
        locals[local] = lowered;
        return lowered;
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
            Add(new IrLet(Declare(symbol), value));
            return;
        }

        var local = Declare(symbol);
        if (initializer is null)
        {
            // A fixed array holds its default; any other local is stored before it is read.
            Add(symbol.Type.IsFixedArray ? new IrLet(local, new IrDefault(symbol.Type)) : new IrDeclare(local));
            return;
        }

        Define(local, initializer);
        if (symbol.IsLoopElement)
            loopElements[local] = declaration.Location;
    }

    /// <summary>The rvalue as an operand a store or return can take: its value, moved out of a temporary if it computes one.</summary>
    IrOperand OperandOf(IrRvalue value) => value is IrUse { Operand: var operand }
        ? operand is IrRead { Ownership: IrOwnership.Borrow } read ? IrRead.Copy(read.Place) : operand
        : IrRead.Move(new IrPlace(Let(value)));

    /// <summary>What a <c>ref</c>, <c>in</c>, <c>ref?</c>, or <c>in?</c> local is initialized with.</summary>
    IrRvalue ReferenceTo(BoundExpression initializer, LocalSymbol local)
    {
        if (!local.Type.IsNullableReference)
            return new IrRef(local.RefKind, Place(initializer, Use.Scope));
        var kind = local.Type.ReferenceKind;
        return initializer switch
        {
            BoundConversion { Operand.Type: var from } conversion when from == VplType.Null => new IrConvert(local.Type, new IrConstant(VplType.Null, null)),
            BoundConversion { Operand: var place } when !place.Type.IsNullableReference => new IrAddr(kind, Place(place, Use.Scope)),
            BoundConversion { Operand: var other } => ReferenceValue(other, local.Type),
            _ => ReferenceValue(initializer, local.Type),
        };
    }

    /// <summary>A <c>ref?</c> or <c>in?</c> value: another one, or one a method returns.</summary>
    IrRvalue ReferenceValue(BoundExpression value, VplType type)
    {
        var rvalue = Rvalue(value, Use.Scope);
        return rvalue.Type == type ? rvalue : new IrConvert(type, OperandOf(rvalue));
    }

    void ExpressionStatement(BoundExpressionStatement statement)
    {
        var expression = statement.Expression;
        if (expression is BoundConditionalAccess access && access.Type == VplType.Void)
        {
            ConditionalAccess(access);
            return;
        }
        var value = Rvalue(expression, Use.Read);
        Add(new IrDo(value));
        if (value is IrCall { Method.Symbol.IsNoReturn: true })
            Unreachable(expression.Location);
    }

    void Return(BoundReturn ret)
    {
        if (ret.Value is not { } value)
        {
            Add(new IrReturn(function.Struct is { } made ? IrRead.Copy(new IrPlace(made)) : null));
        }
        else if (symbol.ReturnRefKind != RefKind.None)
        {
            var reference = value switch
            {
                BoundNullLiteral or BoundConversion { Operand: BoundNullLiteral } => (IrOperand)new IrConstant(symbol.ReturnType, null),
                BoundConversion { Type.IsNullableReference: true, Operand: var place } when !place.Type.IsNullableReference =>
                    IrRead.Copy(new IrPlace(Let(new IrAddr(symbol.ReturnType.ReferenceKind, Place(place, Use.Scope))))),
                _ when symbol.ReturnType.IsNullableReference => IrRead.Copy(new IrPlace(Let(ReferenceValue(value, symbol.ReturnType)))),
                _ => IrRead.Copy(new IrPlace(Let(new IrRef(symbol.ReturnRefKind, Place(value, Use.Scope))))),
            };
            Add(new IrReturn(reference));
        }
        else
        {
            Add(new IrReturn(OperandOf(Rvalue(value, Use.Read))));
        }
        reachable = false;
    }

    void Assignment(BoundAssignment assignment)
    {
        // The target, its indices and their checks included, is found before the value, which reads
        // it from there in a compound assignment like `a[f()] += 1` (spec §3.4, Evaluation order and the heap).
        var target = Place(assignment.Target, Use.InUse);
        bool compound = BoundTree.AllExpressions(assignment.Value).Any(e => ReferenceEquals(e, assignment.Target));
        if (compound && target.Projections.Any(p => p is IrElement { Index: IrRead }))
            target = IrPlace.Deref(Let(new IrRef(RefKind.Ref, target)));
        located[assignment.Target] = target;
        var value = OperandOf(Rvalue(assignment.Value, Use.Read));
        located.Remove(assignment.Target);
        Add(new IrStore(target, value));
    }
}
