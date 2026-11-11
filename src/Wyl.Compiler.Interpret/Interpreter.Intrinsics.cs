using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;

namespace Wyl.Compiler.Interpret;

public sealed partial class Interpreter
{
    // The runtime functions the standard library's extern methods and the builtins name, keyed by
    // the C++ name the compiler already gives them (BuiltinCppName), with an extern struct's own
    // methods under their owner's name so that two String methods can't collide.

    delegate Value? IntrinsicHandler(Interpreter interpreter, IrCall call, string? location, Frame caller);

    /// <summary>
    /// Every intrinsic the interpreter runs, keyed by <see cref="RuntimeKey"/>. The table is both the
    /// dispatch and the answer the registry self-test asks, so a key cannot be known and unhandled.
    /// </summary>
    static readonly IReadOnlyDictionary<string, IntrinsicHandler> Intrinsics = BuildIntrinsics();

    static Dictionary<string, IntrinsicHandler> BuildIntrinsics()
    {
        var table = new Dictionary<string, IntrinsicHandler>(StringComparer.Ordinal)
        {
            ["wyl::print"] = (i, call, location, caller) => i.Print(call.Method, i.ArgumentsOf(call, caller)),
            ["wyl::panic::panic"] = (i, call, location, caller) => Panic(i.ArgumentsOf(call, caller), location),
            ["wyl::Interpolate"] = (i, call, location, caller) => i.Interpolate(call.Method, i.ArgumentsOf(call, caller), location),
            ["wyl::MakeFormatted"] = (i, call, location, caller) => Formatted(call.Method, i.ArgumentsOf(call, caller)),
            ["wyl::panic::checkIndex"] = (i, call, location, caller) => BoundsCheck(i.ArgumentsOf(call, caller), location),
            ["wyl::panic::checkRange"] = (i, call, location, caller) => BoundsCheckRange(i.ArgumentsOf(call, caller), location),
            ["Weak::get"] = (i, call, location, caller) => i.WeakGet(call, caller),
            ["ElementVisitor::visit"] = (i, call, location, caller) =>
            {
                (i.ReceiverOf(call, caller) as ElementVisitorValue)?.Visit?.Invoke(i.RefArgumentCell(call, 0, caller));
                return null;
            },
            ["wyl::argumentCount"] = (i, call, location, caller) => new IntValue((i.options.Arguments ?? []).Count),
            ["wyl::argument"] = (i, call, location, caller) => i.ProgramArgument(i.ArgumentsOf(call, caller)),
            ["wyl::replace"] = (i, call, location, caller) => i.Replace(call, caller, location),
            ["wyl::swap"] = (i, call, location, caller) => i.Swap(call, caller, location),
            ["wyl::take"] = (i, call, location, caller) => i.Take(call, caller, location),
            ["wyl::threading::spawnThread"] = (i, call, location, caller) => i.SpawnThread(call, caller, location),
            ["wyl::threading::joinThread"] = (i, call, location, caller) => i.JoinThread(call, caller, location),
            ["wyl::threading::releaseThread"] = (i, call, location, caller) => i.ReleaseThread(call, caller),
            ["wyl::threading::createState"] = (i, call, location, caller) => i.CreateChannelState(call, caller),
            ["wyl::threading::attachSender"] = (i, call, location, caller) => i.AttachSender(call, caller, location),
            ["wyl::threading::detachSender"] = (i, call, location, caller) => i.DetachSender(call, caller),
            ["wyl::threading::attachReceiver"] = (i, call, location, caller) => i.AttachReceiver(call, caller),
            ["wyl::threading::detachReceiver"] = (i, call, location, caller) => i.DetachReceiver(call, caller),
            ["wyl::threading::sendValue"] = (i, call, location, caller) => i.SendValue(call, caller, location),
            ["wyl::threading::receiveValue"] = (i, call, location, caller) => i.ReceiveValue(call, caller, location),
            ["wyl::threading::buildIsolated"] = (i, call, location, caller) => i.BuildIsolated(call, caller),
            ["wyl::threading::openIsolated"] = (i, call, location, caller) => i.OpenIsolated(call, caller),
            ["wyl::threading::takeValue"] = (i, call, location, caller) => i.TakeIsolatedValue(call, caller),
            ["wyl::threading::createMutex"] = (i, call, location, caller) => i.CreateMutex(call, caller, location),
            ["wyl::threading::releaseMutex"] = (i, call, location, caller) => i.ReleaseMutex(call, caller),
            ["wyl::threading::lockMutex"] = (i, call, location, caller) => i.LockMutex(call, caller, location),
            ["wyl::threading::createAtomic"] = (i, call, location, caller) => i.CreateAtomic(call, caller),
            ["wyl::threading::releaseAtomic"] = (i, call, location, caller) => i.ReleaseAtomic(call, caller),
            ["wyl::threading::atomicGet"] = (i, call, location, caller) => i.AtomicGet(call, caller),
            ["wyl::threading::atomicSet"] = (i, call, location, caller) => i.AtomicSet(call, caller),
            ["wyl::threading::atomicExchange"] = (i, call, location, caller) => i.AtomicExchange(call, caller),
            ["wyl::threading::atomicCompareExchange"] = (i, call, location, caller) => i.AtomicCompareExchange(call, caller),
            ["wyl::threading::atomicAdd"] = (i, call, location, caller) => i.AtomicArithmetic(call, caller, location, subtract: false),
            ["wyl::threading::atomicSubtract"] = (i, call, location, caller) => i.AtomicArithmetic(call, caller, location, subtract: true),
            // The collector takes no owned value, and its `ref` arguments are cells.
            ["wyl::memory::collectCycles"] = (i, call, location, caller) => i.CollectCycles(call, caller, location ?? "<unknown>"),
            ["wyl::memory::takeCycleStats"] = (i, call, location, caller) => i.TakeCycleStats(call, caller),
            ["wyl::memory::allocate"] = (i, call, location, caller) => i.MemoryAllocate(i.ArgumentsOf(call, caller), location),
            ["wyl::memory::free"] = (i, call, location, caller) => MemoryFree(i.ArgumentsOf(call, caller)),
            ["wyl::memory::initialize"] = (i, call, location, caller) => i.MemoryInitialize(call, caller),
            ["wyl::memory::initializeDefault"] = (i, call, location, caller) => i.MemoryInitializeDefault(call, caller),
            ["wyl::memory::drop"] = (i, call, location, caller) => i.MemoryDrop(i.ArgumentsOf(call, caller)),
            ["wyl::memory::moveOut"] = (i, call, location, caller) => MemoryMoveOut(i.ArgumentsOf(call, caller)),
            ["wyl::memory::moveTo"] = (i, call, location, caller) => MemoryMoveTo(i.ArgumentsOf(call, caller)),
            ["wyl::memory::compare"] = (i, call, location, caller) => MemoryCompare(i.ArgumentsOf(call, caller)),
            ["wyl::memory::equal"] = (i, call, location, caller) => MemoryEqual(i.ArgumentsOf(call, caller)),
            ["String::byteLength"] = StringMethod(s => new IntValue(s.Bytes.Length)),
            ["String::cstr"] = StringMethod(s => s.Interpreter.StringStorage(s.Receiver)),
            ["String::toString"] = StringMethod(s => s.Receiver),
            ["wyl::string::copyOf"] = (i, call, location, caller) => CopyOf(i.ArgumentsOf(call, caller)),
        };
        foreach (var type in StdlibSymbols.HostedPrimitives)
        {
            table[$"{type.Name}::toString"] = (i, call, location, caller) => new StringValue(RuntimeText.Format(type, i.ReceiverOf(call, caller)!));
            if (type != WylType.Bool)
            {
                table[$"{type.Name}::compareTo"] = (i, call, location, caller) =>
                    new IntValue(RuntimeArithmetic.CompareTotal(type, i.ReceiverOf(call, caller)!, i.ArgumentsOf(call, caller)[0]));
            }
            if (type == WylType.Float)
                table["float::toBits"] = (i, call, location, caller) => new IntValue(BitConverter.SingleToInt32Bits((float)((FloatValue)i.ReceiverOf(call, caller)!).Number));
            if (type == WylType.Double)
                table["double::toBits"] = (i, call, location, caller) => new IntValue(BitConverter.DoubleToInt64Bits(((FloatValue)i.ReceiverOf(call, caller)!).Number));
            if (type.IsInteger && type != WylType.Char)
            {
                foreach (var (name, op) in (ReadOnlySpan<(string, char)>)[("Add", '+'), ("Sub", '-'), ("Mul", '*')])
                {
                    table[$"{type.Name}::wrapping{name}"] = (i, call, location, caller) =>
                        RuntimeArithmetic.Wrapping(type, op, Integer(i.ReceiverOf(call, caller)!), Integer(i.ArgumentsOf(call, caller)[0]));
                    table[$"{type.Name}::checked{name}"] = (i, call, location, caller) =>
                        new OptionalValue(RuntimeArithmetic.Checked(type, op, Integer(i.ReceiverOf(call, caller)!), Integer(i.ArgumentsOf(call, caller)[0])) is { } result
                            ? new Cell(type, result)
                            : null);
                }
            }
        }
        return table;
    }

    /// <summary>
    /// The runtime name an intrinsic is keyed by: the C++ name the compiler gives the builtin, with
    /// an extern struct's own method under its owner's name so that two String methods can't collide.
    /// </summary>
    public static string RuntimeKey(FunctionSymbol symbol)
    {
        var key = symbol.BuiltinCppName ?? throw new InterpreterException($"builtin '{symbol.Name}' has no runtime name");
        if (symbol.IsInstanceMethod && symbol.ContainingType is { IsExtern: true } owner)
            key = $"{owner.Name}::{key}";
        return key;
    }

    /// <summary>
    /// An argument of the program as UTF-8, each unpaired surrogate replaced by U+FFFD, as the
    /// runtime's SetArguments makes it from the Windows command line.
    /// </summary>
    StringValue ProgramArgument(IReadOnlyList<Value> arguments)
    {
        int index = (int)(long)((IntValue)arguments[0]).Bits;
        return new StringValue(System.Text.Encoding.UTF8.GetBytes(options.Arguments![index]));
    }

    /// <summary>Whether the interpreter has a handler for a runtime name, which the registry self-test asks.</summary>
    public static bool Handles(string key) => Intrinsics.ContainsKey(key);

    /// <summary>Runs the intrinsic a call names, or <paramref name="resolved"/>, the one a dispatched call reached.</summary>
    Value? CallIntrinsic(IrCall call, Frame caller, FunctionSymbol? resolved = null)
    {
        if (program.StaticOf(call.Method.Symbol) is { } statik)
        {
            EnsureStatic(statik, call.Loc is { } at ? Location(at, caller) ?? "" : "");
            return null;
        }
        var key = RuntimeKey(resolved ?? call.Method.Symbol);
        if (!Intrinsics.TryGetValue(key, out var handler))
            throw new UnsupportedFeature($"the intrinsic '{key}'");
        var location = call.Loc is { } loc ? Location(loc, caller) : null;
        return handler(this, call, location, caller);
    }

    /// <summary>An argument's value: the value in a place passed by reference, or a copy of a value.</summary>
    Value ArgumentValue(IrArgument argument, Frame frame) => argument switch
    {
        IrRefArgument reference => PlaceCell(reference.Place, frame).Value,
        IrAddrArgument address => new RefValue(PlaceCell(address.Place, frame)),
        IrOperand operand => EvalArgument(operand, operand.Type, frame),
        _ => throw new InterpreterException($"unexpected argument {argument.GetType().Name}"),
    };

    IReadOnlyList<Value> ArgumentsOf(IrCall call, Frame caller) =>
        [.. call.Arguments.Where(a => a is not IrFieldDefault).Select(a => ArgumentValue(a, caller))];

    Value? ReceiverOf(IrCall call, Frame caller) => call.Receiver is { } given ? ArgumentValue(given, caller) : null;

    /// <summary>The cell a call's <c>ref</c> argument names, which <c>replace</c>, <c>swap</c>, and <c>take</c> change.</summary>
    Cell RefArgumentCell(IrCall call, int index, Frame caller)
    {
        var given = call.Arguments[index];
        return given switch
        {
            IrRefArgument reference => PlaceCell(reference.Place, caller),
            IrAddrArgument address => PlaceCell(address.Place, caller),
            _ => throw new InterpreterException($"argument {index + 1} of '{call.Method.Symbol.Name}' is not a 'ref' argument"),
        };
    }

    void CheckTakenOut(Cell cell, string message, string? location)
    {
        if (AnyInUse(cell.Type, cell.Value))
            throw new WylPanic(message, location ?? "<unknown>");
    }

    /// <summary><c>replace(place, value)</c>: returns what the place held, and stores the new value.</summary>
    Value? Replace(IrCall call, Frame caller, string? location)
    {
        var cell = RefArgumentCell(call, 0, caller);
        CheckTakenOut(cell, "cannot replace a value that is in use", location);
        var value = ArgumentValue(call.Arguments[1], caller);
        var previous = cell.Value;
        cell.Value = value;
        return previous;
    }

    /// <summary><c>swap(a, b)</c>: exchanges what the two places hold, running no destructor.</summary>
    Value? Swap(IrCall call, Frame caller, string? location)
    {
        var a = RefArgumentCell(call, 0, caller);
        var b = RefArgumentCell(call, 1, caller);
        CheckTakenOut(a, "cannot swap a value that is in use", location);
        CheckTakenOut(b, "cannot swap a value that is in use", location);
        if (!ReferenceEquals(a, b))
            (a.Value, b.Value) = (b.Value, a.Value);
        return null;
    }

    /// <summary><c>take(place)</c>: returns what the place held, and leaves null in it.</summary>
    Value? Take(IrCall call, Frame caller, string? location)
    {
        var cell = RefArgumentCell(call, 0, caller);
        CheckTakenOut(cell, "cannot take a value that is in use", location);
        var previous = cell.Value;
        cell.Value = cell.Type.IsNullableValue ? new OptionalValue(null) : new RefValue(null);
        return previous;
    }

    Value? Print(MethodReference method, IReadOnlyList<Value> arguments)
    {
        var line = RuntimeText.Format(method.ParameterType(0), arguments[0]);
        lock (outputLock)
        {
            output.AddRange(line);
            output.Add((byte)'\n');
        }
        return null;
    }

    /// <summary><c>Weak&lt;T&gt;.get()</c>: a counted reference to the target while it lives, or a null reference.</summary>
    Value WeakGet(IrCall call, Frame caller)
    {
        var receiver = call.Receiver switch
        {
            null => throw new InterpreterException("Weak.get was called without a receiver"),
            IrRead read => ReadInPlace(read, caller),
            var other => ArgumentValue(other, caller),
        };
        return receiver switch
        {
            WeakValue weak => weak.Get(),
            NullValue => new RefValue(null),
            var other => throw new InterpreterException($"Weak.get was called on a {other.GetType().Name}"),
        };
    }

    static Value? Panic(IReadOnlyList<Value> arguments, string? location)
    {
        var message = arguments[0] as StringValue ?? throw new InterpreterException("panic's message is not a string");
        throw new WylPanic(message.Text, location ?? "<unknown>");
    }

    Value Interpolate(MethodReference method, IReadOnlyList<Value> arguments, string? location)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is FormattedValue formatted)
                bytes.AddRange(RuntimeText.FormatSpecified(formatted.InnerType, formatted.Inner, formatted.Kind, formatted.Precision, formatted.Alignment, location));
            else
                bytes.AddRange(RuntimeText.Format(method.ParameterType(i), arguments[i]));
        }
        return new StringValue([.. bytes]);
    }

    static Value Formatted(MethodReference method, IReadOnlyList<Value> arguments) =>
        new FormattedValue(method.ParameterType(0), arguments[0], (int)Integer(arguments[1]), (int)Integer(arguments[2]), (int)Integer(arguments[3]));

    static Value? BoundsCheck(IReadOnlyList<Value> arguments, string? location)
    {
        var index = Integer(arguments[0]);
        var length = Integer(arguments[1]);
        if ((uint)(long)index >= (uint)(long)length)
            throw new WylPanic(RuntimeText.IndexMessage(index, length), location ?? "<unknown>");
        return null;
    }

    static Value? BoundsCheckRange(IReadOnlyList<Value> arguments, string? location)
    {
        var start = Integer(arguments[0]);
        var count = Integer(arguments[1]);
        var length = Integer(arguments[2]);
        if (start < 0 || count < 0 || start + count > length)
            throw new WylPanic(RuntimeText.RangeMessage(start, count, length), location ?? "<unknown>");
        return null;
    }

    /// <summary>A call to an extern method of <c>String</c>, which works over the receiver's UTF-8 bytes as <c>runtime/wyl/core.h</c> does.</summary>
    readonly record struct StringCall(Interpreter Interpreter, StringValue Receiver)
    {
        public byte[] Bytes => Receiver.Bytes;
    }

    static IntrinsicHandler StringMethod(Func<StringCall, Value?> body) => (i, call, location, caller) =>
    {
        var receiver = i.ReceiverOf(call, caller) as StringValue
            ?? throw new InterpreterException($"'{call.Method.Symbol.Name}' was called on something that is not a String");
        return body(new StringCall(i, receiver));
    };

    /// <summary>
    /// A pointer to the string's bytes and the null byte after them, which <c>cstr</c> returns. The
    /// interpreter makes the storage once per string value, so a method that calls <c>bytes</c> in a
    /// loop does not copy the string each time. Writing through the pointer is the program's
    /// undefined behavior, which the interpreter does not detect.
    /// </summary>
    Value StringStorage(StringValue text)
    {
        if (text.Storage is not { } storage)
        {
            var made = AllocateBuffer(text.Bytes.Length + 1);
            for (int i = 0; i < text.Bytes.Length; i++)
                made.Write(i, new Cell(WylType.Byte, new IntValue(text.Bytes[i])));
            made.Write(text.Bytes.Length, new Cell(WylType.Byte, new IntValue(0)));
            storage = Interlocked.CompareExchange(ref text.Storage, made, null) ?? made;
        }
        return new PointerValue(storage, 0);
    }

    static readonly System.Text.UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary><c>String.copyOf</c>, whose caller promises valid UTF-8; the interpreter checks the promise.</summary>
    static Value CopyOf(IReadOnlyList<Value> arguments)
    {
        var bytes = SpanBytes(arguments[0]);
        try
        {
            StrictUtf8.GetCharCount(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            throw new UndefinedBehavior("String.copyOf was given bytes that are not valid UTF-8");
        }
        return new StringValue(bytes);
    }
}
