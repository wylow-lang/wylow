using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;

namespace Wyl.Compiler.Interpret;

public sealed partial class Interpreter
{
    // Function values: a non-escaping lambda refers to the frame it was made in, and an escaping one
    // holds a counted closure with copies of what it captured (docs/design/ir.md §5.8). A choice
    // holds every callable it may select; invoking any of them runs the selected lambda's IR.

    /// <summary>A non-escaping lambda, which reads the locals around it as places.</summary>
    Value Lambda(IrLambda lambda, Frame frame) => new FunctionValue(lambda.Function, frame.Map, frame, null);

    /// <summary>
    /// An escaping lambda's closure: copies of what it captures, or nothing when it captures none,
    /// which allocates nothing as the runtime's does. A closure the compiler put in a stack slot
    /// leaves the collector and ends with the slot.
    /// </summary>
    Value Closure(IrClosure closure, Frame frame)
    {
        var captures = new Cell[closure.Captures.Count];
        for (int i = 0; i < captures.Length; i++)
        {
            var capture = closure.Captures[i];
            captures[i] = new Cell(frame.Type(capture.Type), EvalOperand(capture, frame));
        }
        var value = new ClosureValue(closure.Name, captures) { Created = (uint)Interlocked.Increment(ref objectsCreated) };
        if (closure.Slot is { } slot)
        {
            // The slot takes the object's first reference, and the function value a counted copy.
            FillSlot(slot, value, frame);
            value.Retain();
        }
        else
        {
            value.Cyclic = callees.Cycles.IsCyclicClosure(closure.Function.Symbol);
        }
        return new FunctionValue(closure.Function, frame.Map, null, value);
    }

    /// <summary>
    /// A choice: its index, and every callable it may select. It owns the lambdas made into it and the
    /// copies it takes; a borrowed option, a non-escaping local or a nullable parameter, is referred to,
    /// so releasing the choice doesn't release it, as C++ refers to the local. The index is computed by
    /// ordinary control flow, so only the option that was chosen was evaluated into it.
    /// </summary>
    Value Choose(IrChoose choice, Frame frame)
    {
        var index = (int)(long)Integer(EvalOperandInPlace(choice.Index, frame));
        var options = new Cell[choice.Options.Count];
        for (int i = 0; i < options.Length; i++)
        {
            var option = choice.Options[i];
            var value = option is IrRead { Ownership: IrOwnership.Borrow } borrowed
                ? new RefValue(PlaceCell(borrowed.Place, frame))
                : EvalOperand(option, frame);
            options[i] = new Cell(frame.Type(option.Type), value);
        }
        return new ChoiceValue(index, options);
    }

    /// <summary>
    /// Calls a function value as <c>wyl::Function::operator()</c> does: a choice runs the option it
    /// selected, and a lambda runs with the frame it reads or the captures its closure holds.
    /// </summary>
    Value? CallFunctionValue(IrInvoke invoke, Frame caller) =>
        InvokeCallable(EvalOperandInPlace(invoke.Callee, caller), invoke.Arguments, caller);

    /// <summary>Calls a function value, or a choice of them, with the arguments a call gives.</summary>
    Value? InvokeCallable(Value callee, IReadOnlyList<IrArgument> arguments, Frame caller)
    {
        var value = Callable(callee);
        var frame = CallFrame(value);
        BindArguments(arguments, value.Function.Parameters, frame, caller);
        return Invoke(value.Function, frame);
    }

    /// <summary>
    /// The function value a callee runs: a choice's selected option, which may be a choice itself, or
    /// the value a reference or nullable holds. The callee is called where it is: no copy is made,
    /// which would hold counts C++ never takes.
    /// </summary>
    static FunctionValue Callable(Value callee)
    {
        while (callee is ChoiceValue or OptionalValue or RefValue)
        {
            callee = callee switch
            {
                ChoiceValue choice => choice.Options[Chosen(choice)].Value,
                RefValue { Cell: { } referred } => referred.Value,
                OptionalValue { Inner: { } inner } => inner.Value,
                _ => throw new InterpreterException("a null nullable value was used as narrowed"),
            };
        }
        return callee switch
        {
            FunctionValue called => called,
            // The runtime's debug check. In a release build it would call a null pointer, which
            // nothing but a compiler bug reaches either way.
            NullValue => throw new WylPanic("internal compiler error: null function called", "<runtime>"),
            _ => throw new InterpreterException($"called a {callee.GetType().Name}"),
        };
    }

    Frame CallFrame(FunctionValue value)
    {
        var function = value.Function;
        Frame frame;
        if (value.Enclosing is { } enclosing)
        {
            frame = new Frame(value.Map) { Enclosing = enclosing };
        }
        else
        {
            frame = new Frame(value.Map);
            // A closure's captures are its own cells: the body aliases them, and the closure releases
            // them when its last reference goes, as C++ passes them to the body by const reference.
            if (value.Closure is { } closure)
            {
                if (closure.Captures.Length != function.Captures.Count)
                    throw new InterpreterException($"'{function.Name}' was made with {closure.Captures.Length} captures");
                for (int i = 0; i < closure.Captures.Length; i++)
                    frame.Locals[function.Captures[i]] = new RefStorage(closure.Captures[i]);
            }
        }
        return frame;
    }

    /// <summary>
    /// The option a choice runs: its index, or its last option when no index matches, as
    /// <c>wyl::Choice</c> falls through to the one that matches nothing.
    /// </summary>
    static int Chosen(ChoiceValue choice) =>
        choice.Options.Length == 0 ? throw new InterpreterException("a choice with no callable")
        : (uint)choice.Index < (uint)choice.Options.Length ? choice.Index
        : choice.Options.Length - 1;
}
