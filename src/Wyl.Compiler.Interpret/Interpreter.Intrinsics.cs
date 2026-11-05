using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;

namespace Wyl.Compiler.Interpret;

public sealed partial class Interpreter
{
    // The runtime functions the standard library's extern methods and the builtins name, keyed by
    // the C++ name the compiler already gives them (BuiltinCppName), with an extern struct's own
    // methods under their owner's name so that two String methods can't collide.

    delegate Value? IntrinsicHandler(Interpreter interpreter, IrCall call, string? location, Frame caller);

    static readonly string[] MemoryNames =
        ["collectCycles", "takeCycleStats", "allocate", "free", "initialize", "initializeDefault", "drop", "moveTo", "moveOut"];

    static readonly string[] StringNames =
        ["byteLength", "isEmpty", "bytes", "charCount", "startsWith", "endsWith", "contains", "indexOf", "slice", "compareTo", "toString", "charAt", "nextChar"];

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
            ["wyl::interpolate"] = (i, call, location, caller) => i.Interpolate(call.Method, i.ArgumentsOf(call, caller), location),
            ["wyl::formatted"] = (i, call, location, caller) => Formatted(call.Method, i.ArgumentsOf(call, caller)),
            ["wyl::panic::checkIndex"] = (i, call, location, caller) => BoundsCheck(i.ArgumentsOf(call, caller), location),
            ["wyl::panic::checkRange"] = (i, call, location, caller) => BoundsCheckRange(i.ArgumentsOf(call, caller), location),
            ["Weak::get"] = (i, call, location, caller) => i.WeakGet(call, caller),
            ["wyl::argumentCount"] = (i, call, location, caller) => new IntValue((i.options.Arguments ?? []).Count),
            ["wyl::argument"] = (i, call, location, caller) => i.ProgramArgument(i.ArgumentsOf(call, caller)),
            ["wyl::replace"] = (i, call, location, caller) => i.Replace(call, caller, location),
            ["wyl::swap"] = (i, call, location, caller) => i.Swap(call, caller, location),
            ["wyl::take"] = (i, call, location, caller) => i.Take(call, caller, location),
        };
        foreach (var name in MemoryNames)
            table["wyl::memory::" + name] = (i, call, location, caller) => i.MemoryIntrinsic(name, call, caller, location);
        foreach (var name in StringNames)
            table["String::" + name] = (i, call, location, caller) => i.StringMethod(name, i.ReceiverOf(call, caller), i.ArgumentsOf(call, caller), location, caller);
        foreach (var type in StdlibSymbols.HostedPrimitives)
        {
            table[$"{type.Name}::toString"] = (i, call, location, caller) => new StringValue(RuntimeText.Format(type, i.ReceiverOf(call, caller)!));
            if (type != WylType.Bool)
            {
                table[$"{type.Name}::compareTo"] = (i, call, location, caller) =>
                    new IntValue(RuntimeArithmetic.CompareTotal(type, i.ReceiverOf(call, caller)!, i.ArgumentsOf(call, caller)[0]));
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
    /// runtime's set_arguments makes it from the Windows command line.
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
        output.AddRange(RuntimeText.Format(method.ParameterType(0), arguments[0]));
        output.Add((byte)'\n');
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

    /// <summary>The members of <c>String</c>, over its UTF-8 bytes, as <c>runtime/wyl/core.h</c> and <c>strings.h</c> write them.</summary>
    Value? StringMethod(string name, Value? receiver, IReadOnlyList<Value> arguments, string? location, Frame caller)
    {
        var bytes = (receiver as StringValue ?? throw new InterpreterException($"'{name}' was called on something that is not a String")).Bytes;
        switch (name)
        {
            case "byteLength":
                return new IntValue(bytes.Length);
            case "isEmpty":
                return new BoolValue(bytes.Length == 0);
            case "bytes":
                return StringBytes(bytes, caller);
            case "charCount":
                return new IntValue(CharCount(bytes));
            case "startsWith":
                return new BoolValue(Starts(bytes, Text(arguments[0])));
            case "endsWith":
                return new BoolValue(Ends(bytes, Text(arguments[0])));
            case "contains":
                return new BoolValue(IndexOf(bytes, Text(arguments[0])) is not null);
            case "indexOf":
                return new OptionalValue(IndexOf(bytes, Text(arguments[0])) is { } at ? new Cell(WylType.Int, new IntValue(at)) : null);
            case "slice":
                return Slice(bytes, (int)Integer(arguments[0]), (int)Integer(arguments[1]), location);
            case "compareTo":
                return new IntValue(RuntimeText.CompareBytes(bytes, Text(arguments[0])));
            case "toString":
                return receiver;
            case "charAt":
                return new CharValue(Decode(bytes, (int)Integer(arguments[0])));
            case "nextChar":
            {
                int offset = (int)Integer(arguments[0]);
                return new IntValue(offset + LeadLength(bytes[offset]));
            }
            default:
                throw new UnsupportedFeature($"String.{name}");
        }
    }

    static byte[] Text(Value value) => (value as StringValue ?? throw new InterpreterException("a String argument is not one")).Bytes;

    /// <summary>
    /// The bytes as a <c>ReadOnlySpan&lt;byte&gt;</c> over a buffer of them, made by the span's own
    /// constructor, so the interpreter never assumes the span's layout. The string is immutable, so
    /// the span reads a copy that nothing can tell from the string's own storage.
    /// </summary>
    Value StringBytes(byte[] bytes, Frame caller)
    {
        var storage = AllocateBuffer(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
            storage.Write(i, new Cell(WylType.Byte, new IntValue(bytes[i])));
        var span = program.Bound.Stdlib.ReadOnlySpan?.Construct([WylType.Byte])
            ?? throw new InterpreterException("the standard library declares no 'ReadOnlySpan'");
        var constructor = span.Symbol!.Constructors.FirstOrDefault(c => c.Parameters.Count == 2 && c.Parameters[0].Type.IsPointer)
            ?? throw new InterpreterException($"'{span}' declares no constructor from a pointer and a length");
        var method = new MethodReference(constructor, span, []);
        return ConstructWith(method, [
            new Cell(method.ParameterType(0), new PointerValue(storage, 0)),
            new Cell(WylType.Int, new IntValue(bytes.Length)),
        ], caller);
    }

    /// <summary>Makes a struct by running its constructor's IR with values the interpreter already has.</summary>
    Value ConstructWith(MethodReference constructor, IReadOnlyList<Cell> arguments, Frame caller)
    {
        var function = program.Functions.Single(constructor.Symbol)
            ?? throw new InterpreterException($"no lowered function for '{constructor.Symbol.Signature}'");
        var frame = CalleeFrame(caller, constructor.Map);
        for (int i = 0; i < arguments.Count; i++)
            frame.Locals[function.Parameters[i]] = new OwnedStorage(new Cell(frame.Type(function.Parameters[i].Type), arguments[i].Value));
        return Invoke(function, frame) ?? throw new InterpreterException($"'{function.Name}' returned nothing");
    }

    static int CharCount(byte[] bytes)
    {
        int count = 0;
        foreach (var b in bytes)
        {
            if ((b & 0xC0) != 0x80)
                count++;
        }
        return count;
    }

    static bool Starts(byte[] bytes, byte[] prefix) =>
        prefix.Length <= bytes.Length && bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    static bool Ends(byte[] bytes, byte[] suffix) =>
        suffix.Length <= bytes.Length && bytes.AsSpan(bytes.Length - suffix.Length, suffix.Length).SequenceEqual(suffix);

    /// <summary>The byte offset where the text first occurs, or null; the empty text occurs at the start.</summary>
    static int? IndexOf(byte[] bytes, byte[] text)
    {
        if (text.Length > bytes.Length)
            return null;
        if (text.Length == 0)
            return 0;
        for (int at = 0; at <= bytes.Length - text.Length; at++)
        {
            if (bytes.AsSpan(at, text.Length).SequenceEqual(text))
                return at;
        }
        return null;
    }

    static StringValue Slice(byte[] bytes, int start, int count, string? location)
    {
        if (start < 0 || count < 0 || (long)start + count > bytes.Length)
            throw new WylPanic($"byte range of {count} from {start} is out of bounds for length {bytes.Length}", location ?? "<unknown>");
        foreach (var offset in new[] { start, start + count })
        {
            if (offset != bytes.Length && (bytes[offset] & 0xC0) == 0x80)
                throw new WylPanic($"byte offset {offset} is inside a character", location ?? "<unknown>");
        }
        if (count == bytes.Length)
            return new StringValue(bytes);
        return new StringValue(bytes[start..(start + count)]);
    }

    static int Decode(byte[] bytes, int offset)
    {
        int lead = bytes[offset];
        if (lead < 0x80) return lead;
        if (lead < 0xE0) return ((lead & 0x1F) << 6) | (bytes[offset + 1] & 0x3F);
        if (lead < 0xF0) return ((lead & 0x0F) << 12) | ((bytes[offset + 1] & 0x3F) << 6) | (bytes[offset + 2] & 0x3F);
        return ((lead & 0x07) << 18) | ((bytes[offset + 1] & 0x3F) << 12) | ((bytes[offset + 2] & 0x3F) << 6) | (bytes[offset + 3] & 0x3F);
    }

    static int LeadLength(int lead) => lead < 0x80 ? 1 : lead < 0xE0 ? 2 : lead < 0xF0 ? 3 : 4;
}
