using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;

namespace Wyl.Compiler.Interpret;

public sealed partial class Interpreter
{
    Value CreateChannelState(IrCall call, Frame caller)
    {
        _ = ArgumentsOf(call, caller);
        return new SyncStateValue(new ChannelState());
    }

    Value? AttachSender(IrCall call, Frame caller, string? location)
    {
        var state = StateOf(ArgumentsOf(call, caller)[0]);
        lock (state.Gate)
        {
            if (state.Closed)
                throw new WylPanic("cannot make a sender for a closed channel", location ?? "<runtime>");
            state.Senders++;
        }
        return null;
    }

    Value? DetachSender(IrCall call, Frame caller)
    {
        var state = StateOf(ArgumentsOf(call, caller)[0]);
        List<Cell>? dropped = null;
        lock (state.Gate)
        {
            if (--state.Senders > 0 || state.Closed)
                return null;
            state.Closed = true;
            if (state.Receivers == 0)
            {
                dropped = [.. state.Queue];
                state.Queue.Clear();
            }
        }
        state.Ready.Set();
        if (dropped is { } values)
            foreach (var cell in values)
                Release(cell);
        return null;
    }

    Value? AttachReceiver(IrCall call, Frame caller)
    {
        var state = StateOf(ArgumentsOf(call, caller)[0]);
        lock (state.Gate)
            state.Receivers++;
        return null;
    }

    Value? DetachReceiver(IrCall call, Frame caller)
    {
        var state = StateOf(ArgumentsOf(call, caller)[0]);
        List<Cell>? dropped = null;
        lock (state.Gate)
        {
            if (--state.Receivers > 0 || !state.Closed)
                return null;
            dropped = [.. state.Queue];
            state.Queue.Clear();
        }
        state.Ready.Set();
        if (dropped is { } values)
            foreach (var cell in values)
                Release(cell);
        return null;
    }

    Value? SendValue(IrCall call, Frame caller, string? location)
    {
        var arguments = ArgumentsOf(call, caller);
        var element = caller.Map.Compose(call.Method.Map).Apply(call.Method.TypeArguments[0]);
        var state = StateOf(arguments[0]);
        var value = arguments[1];
        if (AnyInUse(element, value))
            throw new WylPanic("cannot move a value that is in use to another thread", location ?? "<runtime>");
        lock (state.Gate)
            state.Queue.Enqueue(new Cell(element, value));
        state.Ready.Set();
        return null;
    }

    Value ReceiveValue(IrCall call, Frame caller)
    {
        var arguments = ArgumentsOf(call, caller);
        var map = caller.Map.Compose(call.Method.Map);
        var element = map.Apply(call.Method.TypeArguments[0]);
        var nullable = map.Apply(call.Method.ReturnType);
        var state = StateOf(arguments[0]);
        while (true)
        {
            lock (state.Gate)
            {
                if (state.Queue.Count > 0)
                {
                    var taken = state.Queue.Dequeue();
                    return nullable == element ? taken.Value : nullable.IsNullableValue ? new OptionalValue(taken) : taken.Value;
                }
                if (state.Closed)
                    return NullOf(nullable == element ? element : nullable);
                state.Ready.Reset();
            }
            WaitHandle.WaitAny([state.Ready.WaitHandle, aborted.WaitHandle]);
            if (aborting)
                throw new AbortedRun();
        }
    }

    static ChannelState StateOf(Value value) =>
        value is SyncStateValue { State: { } state }
            ? state
            : throw new InterpreterException("a channel's state is missing");

    static Value NullOf(WylType nullable) =>
        nullable.IsNullableValue ? new OptionalValue(null) : new RefValue(null);
}
