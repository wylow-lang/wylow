using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;

namespace Wyl.Compiler.Interpret;

public sealed partial class Interpreter
{
    Value? IsolatedIntrinsic(string name, IrCall call, Frame caller, string? location)
    {
        var arguments = ArgumentsOf(call, caller);
        switch (name)
        {
            case "buildIsolated":
            {
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
            case "openIsolated":
            {
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
            case "takeValue":
                return Take(RefArgumentCell(call, 0, caller));
            default:
                throw new UnsupportedFeature($"the intrinsic 'wyl::isolated::{name}'");
        }
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
