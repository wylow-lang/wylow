using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;

namespace Wyl.Compiler.Interpret;

public sealed partial class Interpreter
{
    sealed record CycleState(
        List<HeapObject?> Buffer,
        int Capacity,
        int Holes,
        int Destroying,
        CycleStats Stats,
        List<HeapObject?> Roots,
        List<HeapObject> Marked,
        List<HeapObject> Stack,
        List<HeapObject> Garbage);

    CycleState EnterThread()
    {
        var saved = new CycleState(buffer, capacity, holes, destroying, stats, roots, marked, stack, garbage);
        buffer = [];
        capacity = 0;
        holes = 0;
        destroying = 0;
        stats = new();
        roots = [];
        marked = [];
        stack = [];
        garbage = [];
        return saved;
    }

    void RestoreThread(CycleState saved)
    {
        buffer = saved.Buffer;
        capacity = saved.Capacity;
        holes = saved.Holes;
        destroying = saved.Destroying;
        stats = saved.Stats;
        roots = saved.Roots;
        marked = saved.Marked;
        stack = saved.Stack;
        garbage = saved.Garbage;
    }

    Value? ThreadIntrinsic(string name, IrCall call, Frame caller, string? location)
    {
        var arguments = ArgumentsOf(call, caller);
        switch (name)
        {
            case "spawn":
            {
                if (!mainStarted)
                    throw new WylPanic("cannot start a thread before main begins", location ?? "<runtime>");
                var body = arguments[0] as FunctionValue
                    ?? throw new InterpreterException("the thread's body is not a function value");
                var bodyType = caller.Map.Apply(call.Method.ParameterType(0));
                var resultType = caller.Map.Apply(call.Method.TypeArguments[0]);
                var saved = EnterThread();
                Value? result;
                try
                {
                    result = Invoke(body.Function, CallFrame(body));
                }
                catch
                {
                    RestoreThread(saved);
                    throw;
                }
                Collect(long.MaxValue, location ?? "<thread end>");
                RestoreThread(saved);
                Release(bodyType, body);
                return new ThreadValue { Result = result is null ? null : new Cell(resultType, result) };
            }
            case "join":
            {
                if (arguments[0] is NullValue)
                    throw new WylPanic("cannot join a thread that was never started", location ?? "<runtime>");
                var thread = arguments[0] as ThreadValue
                    ?? throw new InterpreterException("joined something that is not a thread");
                if (thread.Result is not { } result)
                    throw new InterpreterException("joined a thread that has no result");
                thread.Result = null;
                return result.Value;
            }
            case "release":
            {
                if (arguments[0] is ThreadValue { Result: { } result } thread)
                {
                    thread.Result = null;
                    Release(result);
                }
                return null;
            }
            default:
                throw new UnsupportedFeature($"the intrinsic 'wyl::thread::{name}'");
        }
    }
}
