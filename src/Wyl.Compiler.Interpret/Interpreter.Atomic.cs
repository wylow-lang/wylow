using Wyl.Compiler.Ir;

namespace Wyl.Compiler.Interpret;

public sealed partial class Interpreter
{
    Value CreateAtomic(IrCall call, Frame caller) => new AtomicStateValue(new AtomicData(ArgumentsOf(call, caller)[0]));

    Value? ReleaseAtomic(IrCall call, Frame caller)
    {
        _ = AtomicOf(ArgumentsOf(call, caller)[0]);
        return null;
    }

    Value AtomicGet(IrCall call, Frame caller)
    {
        var data = AtomicOf(ArgumentsOf(call, caller)[0]);
        lock (data)
            return data.Value;
    }

    Value? AtomicSet(IrCall call, Frame caller)
    {
        var arguments = ArgumentsOf(call, caller);
        var data = AtomicOf(arguments[0]);
        lock (data)
            data.Value = arguments[1];
        return null;
    }

    Value AtomicExchange(IrCall call, Frame caller)
    {
        var arguments = ArgumentsOf(call, caller);
        var data = AtomicOf(arguments[0]);
        lock (data)
        {
            var old = data.Value;
            data.Value = arguments[1];
            return old;
        }
    }

    Value AtomicCompareExchange(IrCall call, Frame caller)
    {
        var arguments = ArgumentsOf(call, caller);
        var data = AtomicOf(arguments[0]);
        lock (data)
        {
            var old = data.Value;
            if (SameAtomicValue(old, arguments[1]))
                data.Value = arguments[2];
            return old;
        }
    }

    /// <summary>Adds or subtracts under the lock, so a debug build's overflow panic leaves the value unchanged.</summary>
    Value AtomicArithmetic(IrCall call, Frame caller, string? location, bool subtract)
    {
        var arguments = ArgumentsOf(call, caller);
        var type = caller.Map.Compose(call.Method.Map).Apply(call.Method.TypeArguments[0]);
        var data = AtomicOf(arguments[0]);
        var delta = ((IntValue)arguments[1]).Bits;
        lock (data)
        {
            var old = (IntValue)data.Value;
            data.Value = subtract
                ? RuntimeArithmetic.Subtract(type, old.Bits, delta, options.Release, location)
                : RuntimeArithmetic.Add(type, old.Bits, delta, options.Release, location);
            return old;
        }
    }

    static bool SameAtomicValue(Value a, Value b) => (a, b) switch
    {
        (IntValue x, IntValue y) => x.Bits == y.Bits,
        (BoolValue x, BoolValue y) => x.Flag == y.Flag,
        _ => throw new InterpreterException($"an atomic holds '{a}', which is not an integer or a bool"),
    };

    static AtomicData AtomicOf(Value value) =>
        value is AtomicStateValue { Data: { } data }
            ? data
            : throw new InterpreterException("an atomic's state is missing");
}
