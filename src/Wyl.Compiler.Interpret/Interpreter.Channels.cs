using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;

namespace Wyl.Compiler.Interpret;

public sealed partial class Interpreter
{
    Value? ChannelIntrinsic(string name, IrCall call, Frame caller, string? location)
    {
        var arguments = ArgumentsOf(call, caller);
        var map = caller.Map.Compose(call.Method.Map);
        var element = map.Apply(call.Method.TypeArguments[0]);
        var nullable = map.Apply(call.Method.ReturnType);
        switch (name)
        {
            case "createState":
                return new SyncStateValue(new ChannelState());
            case "attachSender":
            {
                var state = StateOf(arguments[0]);
                lock (state.Gate)
                {
                    if (state.Closed)
                        throw new WylPanic("cannot make a sender for a closed channel", location ?? "<runtime>");
                    state.Senders++;
                }
                return null;
            }
            case "detachSender":
            {
                var state = StateOf(arguments[0]);
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
            case "attachReceiver":
            {
                var state = StateOf(arguments[0]);
                lock (state.Gate)
                    state.Receivers++;
                return null;
            }
            case "detachReceiver":
            {
                var state = StateOf(arguments[0]);
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
            case "sendValue":
            {
                var state = StateOf(arguments[0]);
                var value = arguments[1];
                if (AnyInUse(element, value))
                    throw new WylPanic("cannot move a value that is in use to another thread", location ?? "<runtime>");
                lock (state.Gate)
                    state.Queue.Enqueue(new Cell(element, value));
                state.Ready.Set();
                return null;
            }
            case "receiveValue":
            {
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
            default:
                throw new UnsupportedFeature($"the intrinsic 'wyl::channel::{name}'");
        }
    }

    static ChannelState StateOf(Value value) =>
        value is SyncStateValue { State: { } state }
            ? state
            : throw new InterpreterException("a channel's state is missing");

    static Value NullOf(WylType nullable) =>
        nullable.IsNullableValue ? new OptionalValue(null) : new RefValue(null);
}
