using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;

namespace Wyl.Compiler.Interpret;

public sealed partial class Interpreter
{
    Value? BuildIsolated(IrCall call, Frame caller)
    {
        var arguments = ArgumentsOf(call, caller);
        var graph = GraphOf(RefArgumentCell(call, 0, caller));
        SwapGraph(graph);
        try
        {
            return InvokeCallable(arguments[1], [], caller);
        }
        finally
        {
            SwapGraph(graph);
        }
    }

    Value? OpenIsolated(IrCall call, Frame caller)
    {
        var arguments = ArgumentsOf(call, caller);
        var graph = GraphOf(RefArgumentCell(call, 0, caller));
        var content = RefArgumentCell(call, 1, caller);
        SwapGraph(graph);
        try
        {
            return InvokeCallableOn(arguments[2], content, caller);
        }
        finally
        {
            SwapGraph(graph);
        }
    }

    Value? TakeIsolatedValue(IrCall call, Frame caller)
    {
        _ = ArgumentsOf(call, caller);
        return Take(RefArgumentCell(call, 0, caller));
    }

    static GraphBuffer GraphOf(Cell cell) => cell.Value is IsolatedStateValue state
        ? state.Buffer
        : throw new InterpreterException("an isolated box's state is missing");

    /// <summary>
    /// Runs a lambda with one reference parameter bound to a cell, as a non-escaping function value
    /// takes a place.
    /// </summary>
    Value? InvokeCallableOn(Value callee, Cell cell, Frame caller)
    {
        var value = Callable(callee);
        var frame = CallFrame(value);
        if (value.Function.Parameters is not [var parameter])
            throw new InterpreterException($"'{value.Function.Name}' takes {value.Function.Parameters.Count} parameters");
        frame.Locals[parameter] = new RefStorage(cell);
        return Invoke(value.Function, frame);
    }
}
