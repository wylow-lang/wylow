using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.Lowering;

public sealed partial class IrLowering
{
    /// <summary>
    /// A lambda, as a function of its own (docs/design/ir.md §5.8). A non-escaping one's body uses
    /// the locals around it as places; an escaping one's closure copies what it captures, which its
    /// body sees as its captures.
    /// </summary>
    IrRvalue Lambda(BoundLambda lambda)
    {
        var made = lambda.Function;
        var lowered = new IrFunction(made, $"{function.Name}.lambda{lambdas.Count}") { Enclosing = function, Body = new IrBlock { Origin = lambda.Body.Location } };
        lambdas.Add(lowered);
        var captures = new List<IrOperand>();
        var outer = new List<(LocalSymbol Local, IrLocal Lowered)>();
        IrLocal? capturedThis = null;
        if (made.IsEscaping)
        {
            var (capturedLocals, capturesThis) = BoundTree.Captures(lambda);
            foreach (var local in capturedLocals.Select(a => a.Local))
            {
                outer.Add((local, locals[local]));
                captures.Add(IrRead.Copy(new IrPlace(locals[local])));
                lowered.Captures.Add(new IrLocal(local.Name, local.Type, IrLocalKind.Value, IrLocalRole.Capture) { Symbol = local });
            }
            if (capturesThis is not null)
            {
                captures.Add(IrRead.Copy(new IrPlace(self!)));
                lowered.Captures.Add(capturedThis = new IrLocal("this", self!.Type, IrLocalKind.Value, IrLocalRole.Capture));
            }
        }

        var saved = (function, symbol, self, block, reachable, loops: loops.ToList());
        (function, symbol, block, reachable) = (lowered, made, lowered.Body, true);
        if (made.IsEscaping)
            self = capturedThis;
        loops.Clear();
        foreach (var parameter in made.Parameters)
            lowered.Parameters.Add(locals[parameter] = new IrLocal(parameter.Name, parameter.Type, KindOf(parameter), IrLocalRole.Parameter) { Symbol = parameter, RefKind = parameter.RefKind });
        foreach (var (local, copy) in outer.Select(o => o.Local).Zip(lowered.Captures))
            locals[local] = copy;
        FunctionBody(lambda.Body);
        foreach (var (local, original) in outer)
            locals[local] = original;
        (function, symbol, self, block, reachable) = (saved.function, saved.symbol, saved.self, saved.block, saved.reachable);
        loops.Clear();
        loops.AddRange(saved.loops);

        if (!made.IsEscaping)
            return new IrLambda(lowered, lambda.Type);
        return new IrClosure(lowered, captures, lambda.Type)
        {
            Name = $"lambda at {lambda.Location.PanicText}",
            Origin = lambda.Location,
        };    }

    /// <summary>
    /// A call of a function value. A stored closure is held until the call returns, as an object
    /// receiver is.
    /// </summary>
    IrInvoke Invocation(BoundInvocation invocation)
    {
        var callee = invocation.Callee;
        var target = callee.Type.NonNull.IsEscaping && IsPlace(callee)
            ? IrRead.Borrow(new IrPlace(Hold(Place(callee, Use.Read), borrows: false)))
            : Value(callee);
        return new IrInvoke(target, invocation.Signature, Arguments(invocation.Signature, invocation.Arguments));
    }

    /// <summary>
    /// What a non-escaping function parameter, or a nullable one, is given: the lambda, choice, or
    /// function value before its conversion to the parameter's type, which the call makes, so that
    /// what it refers to lives until the call returns.
    /// </summary>
    IrOperand FunctionArgument(WylType parameter, BoundExpression argument)
    {
        if (argument is BoundConversion { Operand: var value })
        {
            if (value.Type == WylType.Null)
                return new IrConstant(parameter, null);
            argument = value;
        }
        return Value(argument);
    }

    /// <summary>
    /// A choice of non-escaping functions by <c>?:</c> or a switch expression (spec 06-syntax.md,
    /// Escaping and non-escaping): the conditions and cases are evaluated in order to the index of
    /// the chosen one, and its lambdas are made and its non-escaping locals referred to whichever it
    /// is, which no one can observe. An escaping option is evaluated only where it is chosen, into the
    /// one local all of them share.
    /// </summary>
    IrChoose Choice(BoundExpression choice)
    {
        static bool IsHeld(BoundExpression option) => option is BoundConversion { Operand.Type.IsEscaping: true };
        var leaves = BoundTree.FunctionOptions(choice).ToList();
        var made = leaves.Where(l => !IsHeld(l)).ToList();
        IrLocal? held = null;
        if (leaves.Any(IsHeld))
        {
            held = new IrLocal("held", ((FunctionType)choice.Type).Escaping, IrLocalKind.Value, IrLocalRole.Variable);
            Add(LetOf(held, new IrUse(new IrConstant(held.Type, null))));
        }

        IrOperand Index(BoundExpression option)
        {
            switch (option)
            {
                case BoundConditional conditional:
                    return OperandOf(Chosen(WylType.Int, () => Value(conditional.Condition),
                        () => Index(conditional.WhenTrue), () => Index(conditional.WhenFalse)));
                case BoundSwitchExpression switchExpression:
                    return OperandOf(SwitchExpression(switchExpression, WylType.Int, Index));
                case var escaping when IsHeld(escaping):
                    Add(new IrStore(new IrPlace(held!), OperandOf(Rvalue(((BoundConversion)escaping).Operand, Use.Read))) { Origin = at });
                    return IrConstant.Int(made.Count);
                default:
                    return IrConstant.Int(made.FindIndex(l => ReferenceEquals(l, option)));
            }
        }
        var index = Index(choice);
        // Made after the index, which no one can tell apart, so that the C++ can make them in place.
        List<IrOperand> options = [.. made.Select(leaf => leaf switch
        {
            BoundLambda lambda => IrRead.Move(new IrPlace(Let(Lambda(lambda)))),
            BoundLocalAccess { Type.IsEscaping: false } local => IrRead.Borrow(Place(local, Use.Read)),
            BoundLocalAccess local => (IrOperand)IrRead.Copy(Place(local, Use.Read)),
            _ => throw new InvalidOperationException($"a chosen function cannot be a {leaf.GetType().Name}"),
        })];
        if (held is not null)
            options.Add(IrRead.Copy(new IrPlace(held)));
        return new IrChoose(index, options, choice.Type);
    }
}
