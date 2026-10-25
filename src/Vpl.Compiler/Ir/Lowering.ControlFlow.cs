using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Ir;

public sealed partial class IrLowering
{
    sealed class LoopContext(IrLoop loop)
    {
        public IrLoop Loop { get; } = loop;
        public bool Breaks { get; set; }
    }

    readonly List<LoopContext> loops = [];

    /// <summary>
    /// Defines a local with an expression's value, as a statement of its own: the temporaries its
    /// value needs are released after the local is defined, so if releasing one may be observed, the
    /// local is declared first and stored at the end of their block.
    /// </summary>
    void Define(IrLocal local, BoundExpression value)
    {
        var scope = new IrBlock { IsStatement = true };
        IrLet? let = null;
        Into(scope, () => Add(let = new IrLet(local, Rvalue(value, Use.Read))));
        if (!scope.DeclaresOwner(local))
        {
            block.Statements.AddRange(scope.Statements);
            return;
        }
        var at = scope.Statements.IndexOf(let!);
        scope.Statements.RemoveAt(at);
        var saved = block;
        block = new IrBlock();
        var operand = OperandOf(let!.Value);
        scope.Statements.InsertRange(at, [.. block.Statements, new IrStore(new IrPlace(local), operand)]);
        block = saved;
        Add(new IrDeclare(local));
        Add(scope);
    }

    /// <summary>A condition, evaluated as a statement of its own (spec §3.2, When references are released).</summary>
    IrOperand Condition(BoundExpression condition)
    {
        if (condition is BoundLiteral literal)
            return new IrConstant(literal.Type, literal.Value);
        var value = Temp(VplType.Bool);
        Define(value, condition);
        return IrRead.Move(new IrPlace(value));
    }

    /// <summary>A branch or loop body, as a block of its own.</summary>
    IrBlock Body(BoundStatement statement)
    {
        var body = new IrBlock();
        Into(body, () =>
        {
            foreach (var inner in statement is BoundBlock block ? block.Statements : [statement])
            {
                if (!reachable)
                    break;
                Statement(inner);
            }
        });
        return body;
    }

    void If(BoundIf conditional)
    {
        // Flow analysis counts what a constant condition's branch assigns as assigned after it.
        if (conditional.Condition is BoundLiteral { Value: bool constant })
        {
            if ((constant ? conditional.Then : conditional.Else) is { } taken)
                Add(Body(taken));
            return;
        }
        var condition = Condition(conditional.Condition);
        var then = Body(conditional.Then);
        bool afterThen = reachable;
        reachable = true;
        var otherwise = conditional.Else is { } elseBranch ? Body(elseBranch) : null;
        reachable |= afterThen;
        Add(new IrIf(condition, then, otherwise));
    }

    /// <summary>
    /// A loop whose condition is tested at the start of every iteration, and whose
    /// <paramref name="update"/> runs after the body and every <c>continue</c>.
    /// </summary>
    void Loop(BoundExpression? condition, BoundStatement body, BoundStatement? update)
    {
        var loop = new IrLoop();
        var context = new LoopContext(loop);
        loops.Add(context);
        Into(loop.Body, () =>
        {
            if (condition is not (null or BoundLiteral { Value: true }))
                BreakUnless(Condition(condition), context);
            if (reachable)
                Add(Body(body));
        });
        if (update is not null)
        {
            reachable = true;
            loop.Continue = Body(update);
        }
        loops.RemoveAt(loops.Count - 1);
        Add(loop);
        reachable = context.Breaks;
    }

    void BreakUnless(IrOperand condition, LoopContext context)
    {
        Add(new IrIf(condition, new IrBlock(), new IrBlock([new IrBreak(context.Loop)])));
        context.Breaks = true;
    }

    void For(BoundFor loop)
    {
        var scope = new IrBlock();
        Into(scope, () =>
        {
            if (loop.Initializer is { } initializer)
                Statement(initializer);
            DeclarePatternLocals(loop.Condition);
            Loop(loop.Condition, loop.Body, loop.Update);
        });
        Add(scope);
    }

    /// <summary>
    /// The bounds are evaluated once, in order. An inclusive range compares before it increments,
    /// so a range ending at the type's maximum value can't overflow; an exclusive one only
    /// increments a counter below its end.
    /// </summary>
    void ForRange(BoundForRange range)
    {
        var type = range.Local.Type;
        var scope = new IrBlock();
        Into(scope, () =>
        {
            DeclarePatternLocals(range.Start);
            DeclarePatternLocals(range.End);
            var start = Let(new IrUse(Settle(Value(range.Start))));
            var end = Let(new IrUse(Settle(Value(range.End))));
            var counter = new IrLocal("i", type, IrLocalKind.Value);
            Add(new IrLet(counter, new IrUse(IrRead.Copy(new IrPlace(start)))));
            IrLocal? more = null;
            if (range.IsInclusive)
            {
                more = new IrLocal("more", VplType.Bool, IrLocalKind.Value);
                Add(new IrLet(more, new IrBinary(BoundBinaryOperator.LessOrEqual, IrRead.Copy(new IrPlace(start)), IrRead.Copy(new IrPlace(end)), VplType.Bool)));
            }
            var loop = new IrLoop();
            var context = new LoopContext(loop);
            loops.Add(context);
            var loc = Loc(range.Location);
            Into(loop.Body, () =>
            {
                var test = more is not null
                    ? IrRead.Copy(new IrPlace(more))
                    : IrRead.Move(new IrPlace(Let(new IrBinary(BoundBinaryOperator.Less, IrRead.Copy(new IrPlace(counter)), IrRead.Copy(new IrPlace(end)), VplType.Bool))));
                BreakUnless(test, context);
                var body = new IrBlock();
                Into(body, () =>
                {
                    Add(new IrLet(Declare(range.Local), new IrUse(IrRead.Copy(new IrPlace(counter)))));
                    foreach (var statement in range.Body is BoundBlock block ? block.Statements : [range.Body])
                    {
                        if (!reachable)
                            break;
                        Statement(statement);
                    }
                });
                Add(body);
            });
            reachable = true;
            loop.Continue = new IrBlock();
            Into(loop.Continue, () =>
            {
                if (more is null)
                {
                    Increment(counter, loc);
                    return;
                }
                var differs = Let(new IrBinary(BoundBinaryOperator.NotEqual, IrRead.Copy(new IrPlace(counter)), IrRead.Copy(new IrPlace(end)), VplType.Bool));
                Add(new IrStore(new IrPlace(more), IrRead.Move(new IrPlace(differs))));
                var increment = new IrBlock();
                Into(increment, () => Increment(counter, loc));
                Add(new IrIf(IrRead.Copy(new IrPlace(more)), increment));
            });
            loops.RemoveAt(loops.Count - 1);
            Add(loop);
            reachable = context.Breaks;
        });
        Add(scope);
    }

    void Increment(IrLocal counter, IrLoc loc)
    {
        var next = Let(new IrBinary(BoundBinaryOperator.Add, IrRead.Copy(new IrPlace(counter)), new IrConstant(counter.Type, One(counter.Type)), counter.Type, loc));
        Add(new IrStore(new IrPlace(counter), IrRead.Move(new IrPlace(next))));
    }

    static object One(VplType type) => BoundLiteral.Number(default!, type, 1).Value;

    void Jump(BoundStatement jump)
    {
        var context = loops.LastOrDefault() ?? throw new InvalidOperationException("a jump outside a loop");
        if (jump is BoundBreak)
        {
            Add(new IrBreak(context.Loop));
            context.Breaks = true;
        }
        else
        {
            Add(new IrContinue(context.Loop));
        }
        reachable = false;
    }

    /// <summary>
    /// A value that only some paths compute, as a local each path stores. An operand that may not be
    /// evaluated is a block of its own, so its temporaries are released when it completes.
    /// </summary>
    IrRvalue Chosen(VplType type, Func<IrOperand> condition, Func<IrOperand> whenTrue, Func<IrOperand> whenFalse)
    {
        var result = Temp(type);
        Add(new IrDeclare(result));
        var tested = condition();
        IrBlock Branch(Func<IrOperand> value)
        {
            var branch = new IrBlock();
            Into(branch, () => Add(new IrStore(new IrPlace(result), Settle(value()))));
            return branch;
        }
        Add(new IrIf(tested, Branch(whenTrue), Branch(whenFalse)));
        return new IrUse(IrRead.Move(new IrPlace(result)));
    }

    /// <summary><c>try r</c>: an <c>Err</c> returns it from the function, wherever the <c>try</c> is (§5.6); otherwise the value is the <c>Ok</c>'s.</summary>
    IrRvalue Try(BoundTry attempt)
    {
        var result = attempt.Operand.Type.Symbol!;
        var (ok, err) = (result.FindVariant("Ok")!, result.FindVariant("Err")!);
        var value = Matched(attempt.Operand);
        var ownership = value.Local.IsTemporary && value is { IsDeref: false, Projections.Count: 0 } ? IrOwnership.Move : IrOwnership.Copy;
        var failed = Tested(new IrTest(IrTestKind.Tag, IrRead.Borrow(value)) { Variant = err });
        var returns = new IrBlock();
        Into(returns, () => Add(new IrReturn(OperandOf(new IrVariant(symbol.ReturnType, err, [new IrRead(ownership, value.VariantField(err, 0))])))));
        Add(new IrIf(failed, returns));
        return new IrUse(new IrRead(ownership, value.VariantField(ok, 0)));
    }

    IrOperand Converted(IrOperand operand, VplType type) =>
        operand.Type == type ? operand : IrRead.Move(new IrPlace(Let(new IrConvert(type, operand))));

    IrRvalue Logical(BoundBinary binary)
    {
        IrOperand Left() => Settle(Value(binary.Left));
        IrOperand Right() => OperandOf(Rvalue(binary.Right, Use.Read));
        return binary.Operator == BoundBinaryOperator.LogicalAnd
            ? Chosen(VplType.Bool, Left, Right, () => IrConstant.Bool(false))
            : Chosen(VplType.Bool, Left, () => IrConstant.Bool(true), Right);
    }

    IrRvalue Conditional(BoundConditional conditional)
    {
        return Chosen(conditional.Type, () => Settle(Value(conditional.Condition)),
            () => OperandOf(Rvalue(conditional.WhenTrue, Use.Read)),
            () => OperandOf(Rvalue(conditional.WhenFalse, Use.Read)));
    }

    /// <summary><c>left ?? right</c>: the left value, if it isn't null, is copied out of a temporary.</summary>
    IrRvalue Coalesce(BoundCoalesce coalesce)
    {
        IrLocal left = null!;
        var type = coalesce.Left.Type;
        IrOperand Test()
        {
            left = Let(new IrUse(Settle(Value(coalesce.Left))));
            return IrRead.Move(new IrPlace(Let(new IrTest(type.IsNullableValue ? IrTestKind.HasValue : IrTestKind.NotNull, IrRead.Copy(new IrPlace(left))))));
        }
        return Chosen(coalesce.Type, Test,
            () => Converted(type.IsNullableValue ? IrRead.Copy(new IrPlace(left).Assumed()) : IrRead.Copy(new IrPlace(left)), coalesce.Type),
            () => OperandOf(Rvalue(coalesce.Right, Use.Read)));
    }

    /// <summary>
    /// <c>receiver?.member</c>, through the binder's <see cref="BoundConditionalAccess.Value"/>: a
    /// <c>ref?</c> receiver's reference, whose holds last until the member is done; otherwise, where
    /// the receiver isn't null, its object, or the value in a copy of it. Returns null for a
    /// <c>void</c> member, which is a statement.
    /// </summary>
    IrRvalue? ConditionalAccess(BoundConditionalAccess access)
    {
        var type = access.Receiver.Type;
        IrLocal held;
        if (type.IsNullableReference)
            Add(new IrLet(held = Declare(access.Value), ReferenceValue(access.Receiver, access.Value.Type)));
        else
            held = Let(new IrUse(Settle(Value(access.Receiver))));
        IrOperand Test() => Tested(new IrTest(type.IsNullableValue ? IrTestKind.HasValue : IrTestKind.NotNull, IrRead.Copy(new IrPlace(held))));
        void Bind()
        {
            if (type.IsNullableReference)
                return;
            var value = Declare(access.Value);
            if (!type.IsNullableValue)
            {
                Add(new IrLet(value, new IrConvert(value.Type, IrRead.Copy(new IrPlace(held)))));
                return;
            }
            var place = new IrPlace(held).Assumed();
            value.Kind = IrLocalKind.Borrowed;
            aliases[value] = place;
            Add(new IrLet(value, new IrUse(IrRead.Borrow(place))));
        }
        if (access.Type == VplType.Void)
        {
            var condition = Test();
            var then = new IrBlock();
            Into(then, () =>
            {
                Bind();
                Add(new IrDo(Rvalue(access.WhenNotNull, Use.Read)));
            });
            Add(new IrIf(condition, then));
            return null;
        }
        return Chosen(access.Type, Test, () =>
        {
            Bind();
            return Converted(OperandOf(Rvalue(access.WhenNotNull, Use.Read)), access.Type);
        }, () => new IrConstant(access.Type, null));
    }
}
