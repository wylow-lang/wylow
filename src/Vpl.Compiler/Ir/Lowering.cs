using Vpl.Compiler.Analysis;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Ir;

/// <summary>A construct lowering doesn't handle yet; the function is emitted from the bound tree instead.</summary>
public sealed class NotLoweredException(string reason) : Exception(reason);

/// <summary>A function lowering left to the bound tree emitter, and why.</summary>
public sealed record UnloweredFunction(FunctionSymbol Function, string Reason);

/// <summary>
/// Lowers checked functions into the IR (docs/design/ir.md, §5). Lowering is conservative: every
/// object a place goes through is held, and every read that something after it might change is
/// copied into a temporary first. When a block is complete, the holds that nothing in their extent
/// needs are elided (§6.2), using the same effects the verifier then checks them with.
/// </summary>
public sealed partial class IrLowering
{
    readonly BoundProgram program;
    readonly Analyses analyses;
    readonly bool elide;
    readonly IReadOnlyList<TypeSymbol> borrowableClasses;

    readonly FunctionSymbol symbol;
    readonly IrFunction function;
    readonly IrLocal? self;
    readonly Dictionary<LocalSymbol, IrLocal> locals = [];
    readonly Dictionary<BoundExpression, IrLocal> slots = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<BoundExpression> filled = new(ReferenceEqualityComparer.Instance);
    IrBlock block;
    bool reachable = true;

    IrLowering(BoundProgram program, Analyses analyses, bool elide, IReadOnlyList<TypeSymbol> borrowableClasses, FunctionSymbol symbol)
    {
        this.program = program;
        this.analyses = analyses;
        this.elide = elide;
        this.borrowableClasses = borrowableClasses;
        this.symbol = symbol;
        var owner = symbol.ContainingType;
        function = new IrFunction(symbol, owner is null ? symbol.Name : $"{owner.Name}.{symbol.Name}");
        block = function.Body;
        if (owner is not null && !symbol.IsStatic)
            function.This = self = new IrLocal("this", owner.Type, IrLocalKind.Value) { IsThis = true, IsParameter = true };
        foreach (var parameter in symbol.Parameters)
            function.Parameters.Add(locals[parameter] = new IrLocal(parameter.Name, parameter.Type, KindOf(parameter)) { IsParameter = true, Symbol = parameter, RefKind = parameter.RefKind });
    }

    /// <summary>Lowers every function it can; the rest are listed in <paramref name="unlowered"/>.</summary>
    public static IrProgram Lower(BoundProgram program, Analyses analyses, bool elide, List<UnloweredFunction> unlowered)
    {
        var ir = new IrProgram(program);
        var borrowable = program.Stdlib.Borrowable;
        List<TypeSymbol> borrowableClasses = [.. program.Types.Where(s => s.Kind == TypeKind.Class && borrowable is not null && Conversions.IsSubtype(s.Type, borrowable.Type))];
        foreach (var function in program.Functions.Where(f => !f.Symbol.IsMemberwise))
        {
            try
            {
                ir.Functions.Add(new IrLowering(program, analyses, elide, borrowableClasses, function.Symbol).Function(function));
            }
            catch (NotLoweredException e)
            {
                unlowered.Add(new UnloweredFunction(function.Symbol, e.Message));
            }
        }
        return ir;
    }

    static IrLocalKind KindOf(LocalSymbol local) =>
        local.RefKind != RefKind.None || local.Type.IsNullableReference ? IrLocalKind.Reference : IrLocalKind.Value;

    IrFunction Function(BoundFunction bound)
    {
        if (symbol.IsConstructor)
            throw new NotLoweredException("a constructor");
        if (symbol.PassesLocation)
            throw new NotLoweredException("an @CallerLocation function");
        if (BoundTree.AllExpressions(bound.Body).Any(e => e is BoundLambda))
            throw new NotLoweredException("a lambda");

        foreach (var statement in bound.Body.Statements)
        {
            if (!reachable)
                break;
            Statement(statement);
        }
        if (reachable && symbol.ReturnType != VplType.Void)
        {
            var location = bound.Body.Location;
            Unreachable(location with { Span = new TextSpan(location.Span.End - 1, 1) });
        }
        Close(function.Body);
        return function;
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

    void Unreachable(Location location)
    {
        Add(new IrUnreachable(new IrLoc(location)));
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
                throw new NotLoweredException($"a {statement.GetType().Name}");
        }
    }

    /// <summary>Lowers into <paramref name="target"/>, then elides the holds in it that nothing needs.</summary>
    void Into(IrBlock target, Action lower)
    {
        var saved = block;
        block = target;
        lower();
        Close(target);
        block = saved;
    }

    /// <summary>
    /// A statement's temporaries are released when it completes (spec §3.2, When references are
    /// released), so they are declared in a block of its own when releasing one may be observed.
    /// Otherwise they join the enclosing block, which no one can tell apart.
    /// </summary>
    void InStatement(BoundStatement statement, Action lower)
    {
        var scope = new IrBlock();
        Into(scope, () =>
        {
            DeclareSlots(statement, temporaries: true);
            lower();
        });
        Splice(scope);
    }

    /// <summary>
    /// Declares a slot for each object on the stack a statement makes (03-memory-model.md, Stack
    /// allocation), before anything that can refer to it, so that it is destroyed after them: a
    /// local's with the local, and a temporary's in the statement's scope, which ends with it.
    /// </summary>
    void DeclareSlots(BoundStatement statement, bool temporaries)
    {
        foreach (var made in analyses.StackObjects.MadeBy(statement).Where(m => m.IsTemporary == temporaries))
        {
            var slot = new IrLocal("s", made.Creation.Type, IrLocalKind.Slot) { IsTemporary = true };
            slots[made.Creation] = slot;
            Add(new IrDeclare(slot));
        }
    }

    void Splice(IrBlock scope, IrLocal? declared = null)
    {
        if (scope.Statements.Any(s => DeclaresOwner(s, declared)))
            Add(scope);
        else
            block.Statements.AddRange(scope.Statements);
    }

    static bool DeclaresOwner(IrStatement statement, IrLocal? except) => statement switch
    {
        IrLet let => let.Local != except && IrEffects.ReleaseMayRunCode(let.Local),
        IrDeclare declare => declare.Local != except && IrEffects.ReleaseMayRunCode(declare.Local),
        _ => false,
    };

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

        // A loop element nothing writes while it lives refers to the element in place (§6.3).
        if (analyses.Elements.AliasesElement(symbol))
        {
            var element = new IrLocal(symbol.Name, symbol.Type, IrLocalKind.Borrowed) { Symbol = symbol, IsUnwrittenElement = true };
            locals[symbol] = element;
            Add(new IrLet(element, new IrUse(IrRead.Borrow(Place(initializer!, Use.Scope)))));
            return;
        }

        // A reference or view, and the holds of what it points into, last for the local's scope.
        if (symbol.RefKind != RefKind.None || symbol.Type.IsNullableReference || symbol.Type.IsRefStruct)
        {
            var value = symbol.Type.IsRefStruct ? Rvalue(initializer!, Use.Scope) : ReferenceTo(initializer!, symbol);
            Add(new IrLet(Declare(symbol), value));
            return;
        }

        DeclareSlots(declaration, temporaries: false);
        var local = Declare(symbol);
        if (initializer is null)
        {
            // A fixed array holds its default; any other local is stored before it is read.
            Add(symbol.Type.IsFixedArray ? new IrLet(local, new IrDefault(symbol.Type)) : new IrDeclare(local));
            return;
        }

        Define(local, initializer, declaration);
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
            Add(new IrReturn());
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
        // The objects on the stack the local may have held are destroyed where their release would
        // have: those made before it, since a slot is filled at most once.
        foreach (var replaced in analyses.StackObjects.EndedBy(assignment).Where(filled.Contains))
            Add(new IrEnd(slots[replaced]));
    }
}
