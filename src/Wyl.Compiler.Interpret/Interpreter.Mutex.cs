using Wyl.Compiler.Ir;

namespace Wyl.Compiler.Interpret;

public sealed partial class Interpreter
{
    Value CreateMutex(IrCall call, Frame caller, string? location)
    {
        var arguments = ArgumentsOf(call, caller);
        var content = caller.Map.Compose(call.Method.Map).Apply(call.Method.TypeArguments[0]);
        var state = RefArgumentCell(call, 1, caller);
        if (AnyInUse(content, arguments[0]))
            throw new WylPanic("cannot move a value that is in use to another thread", location ?? "<runtime>");
        var data = new MutexData(new Cell(content, arguments[0]), GraphOf(state));
        state.Value = new IsolatedStateValue(new GraphBuffer());
        return new MutexStateValue(data);
    }

    /// <summary>Releases the graph's buffer into this thread's, then the value, as an isolated box's release does.</summary>
    Value? ReleaseMutex(IrCall call, Frame caller)
    {
        var data = MutexOf(ArgumentsOf(call, caller)[0]);
        MergeGraph(data.Graph);
        Release(data.Content);
        data.Gate.Dispose();
        return null;
    }

    Value? LockMutex(IrCall call, Frame caller, string? location)
    {
        var arguments = ArgumentsOf(call, caller);
        var data = MutexOf(arguments[0]);
        var thread = current.Value!;
        if (thread.HoldsLock)
            throw new WylPanic("cannot lock a mutex while this thread holds a lock", location ?? "<runtime>");
        WaitHandle.WaitAny([data.Gate, aborted.WaitHandle]);
        if (aborting)
            throw new AbortedRun();
        thread.HoldsLock = true;
        SwapGraph(data.Graph);
        try
        {
            return InvokeCallableOn(arguments[1], data.Content, caller);
        }
        finally
        {
            SwapGraph(data.Graph);
            thread.HoldsLock = false;
            data.Gate.Release();
        }
    }

    void RefuseWaitWhileLocked(string message, string? location)
    {
        if (current.Value!.HoldsLock)
            throw new WylPanic(message, location ?? "<runtime>");
    }

    static MutexData MutexOf(Value value) =>
        value is MutexStateValue { Data: { } data }
            ? data
            : throw new InterpreterException("a mutex's state is missing");
}
