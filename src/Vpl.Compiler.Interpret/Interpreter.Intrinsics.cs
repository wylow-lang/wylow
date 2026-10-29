using Vpl.Compiler.Ir;
using Vpl.Compiler.Semantics;

namespace Vpl.Compiler.Interpret;

public sealed partial class Interpreter
{
    // The runtime functions the standard library's extern methods and the builtins name, keyed by
    // the C++ name the compiler already gives them (BuiltinCppName), with an extern struct's own
    // methods under their owner's name so that two String methods can't collide.

    Value? CallIntrinsic(IrCall call, Frame caller)
    {
        var symbol = call.Method.Symbol;
        var receiver = call.Receiver is { } given ? ArgumentValue(given, caller) : null;
        var arguments = call.Arguments.Where(a => a is not IrFieldDefault).Select(a => ArgumentValue(a, caller)).ToList();
        var key = symbol.BuiltinCppName ?? throw new InterpreterException($"builtin '{symbol.Name}' has no runtime name");
        if (symbol.IsInstanceMethod && symbol.ContainingType is { IsExtern: true } owner)
            key = $"{owner.Name}::{key}";
        var location = call.Loc is { } loc ? Location(loc, caller) : null;
        return Invoke(key, call.Method, receiver, arguments, location);
    }

    /// <summary>An argument's value: the value in a place passed by reference, or a copy of a value.</summary>
    Value ArgumentValue(IrArgument argument, Frame frame) => argument switch
    {
        IrRefArgument reference => PlaceCell(reference.Place, frame).Value,
        IrAddrArgument address => new RefValue(PlaceCell(address.Place, frame)),
        IrOperand operand => EvalOwnedArgument(operand, frame),
        _ => throw new InterpreterException($"unexpected argument {argument.GetType().Name}"),
    };

    Value? Invoke(string key, MethodReference method, Value? receiver, IReadOnlyList<Value> arguments, string? location) => key switch
    {
        "vpl::print" => Print(method, arguments),
        "vpl::panic" => Panic(arguments, location),
        "vpl::interpolate" => Interpolate(method, arguments, location),
        "vpl::formatted" => Formatted(method, arguments),
        "vpl::format_string" => FormatString(method, arguments),
        "vpl::compare" => Compare(method, arguments),
        "vpl::bounds::check" => BoundsCheck(arguments, location),
        "vpl::bounds::checkRange" => BoundsCheckRange(arguments, location),
        _ when key.StartsWith("String::", StringComparison.Ordinal) => StringMethod(key["String::".Length..], receiver, arguments, location),
        _ => throw new UnsupportedFeature($"the intrinsic '{key}'"),
    };

    Value? Print(MethodReference method, IReadOnlyList<Value> arguments)
    {
        output.AddRange(RuntimeText.Format(method.ParameterType(0), arguments[0]));
        output.Add((byte)'\n');
        return null;
    }

    static Value? Panic(IReadOnlyList<Value> arguments, string? location)
    {
        var message = arguments[0] as StringValue ?? throw new InterpreterException("panic's message is not a string");
        throw new VplPanic(message.Text, location ?? "<unknown>");
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

    static Value FormatString(MethodReference method, IReadOnlyList<Value> arguments)
    {
        var type = method.ParameterType(0);
        if (arguments[0] is StringValue text)
            return text;
        return new StringValue(RuntimeText.Format(type, arguments[0]));
    }

    static Value Compare(MethodReference method, IReadOnlyList<Value> arguments) =>
        new IntValue(RuntimeArithmetic.CompareTotal(method.ParameterType(0), arguments[0], arguments[1]));

    static Value? BoundsCheck(IReadOnlyList<Value> arguments, string? location)
    {
        var index = Integer(arguments[0]);
        var length = Integer(arguments[1]);
        if ((uint)(long)index >= (uint)(long)length)
            throw new VplPanic(RuntimeText.IndexMessage(index, length), location ?? "<unknown>");
        return null;
    }

    static Value? BoundsCheckRange(IReadOnlyList<Value> arguments, string? location)
    {
        var start = Integer(arguments[0]);
        var count = Integer(arguments[1]);
        var length = Integer(arguments[2]);
        if (start < 0 || count < 0 || start + count > length)
            throw new VplPanic(RuntimeText.RangeMessage(start, count, length), location ?? "<unknown>");
        return null;
    }

    /// <summary>The members of <c>String</c>, over its UTF-8 bytes, as <c>runtime/vpl/core.h</c> and <c>strings.h</c> write them.</summary>
    static Value? StringMethod(string name, Value? receiver, IReadOnlyList<Value> arguments, string? location)
    {
        var bytes = (receiver as StringValue ?? throw new InterpreterException($"'{name}' was called on something that is not a String")).Bytes;
        switch (name)
        {
            case "byteLength":
                return new IntValue(bytes.Length);
            case "isEmpty":
                return new BoolValue(bytes.Length == 0);
            case "charCount":
                return new IntValue(CharCount(bytes));
            case "startsWith":
                return new BoolValue(Starts(bytes, Text(arguments[0])));
            case "endsWith":
                return new BoolValue(Ends(bytes, Text(arguments[0])));
            case "contains":
                return new BoolValue(IndexOf(bytes, Text(arguments[0])) is not null);
            case "indexOf":
                return new OptionalValue(IndexOf(bytes, Text(arguments[0])) is { } at ? new Cell(VplType.Int, new IntValue(at)) : null);
            case "slice":
                return Slice(bytes, (int)Integer(arguments[0]), (int)Integer(arguments[1]), location);
            case "compareTo":
                return new IntValue(RuntimeText.CompareBytes(bytes, Text(arguments[0])));
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
            throw new VplPanic($"byte range of {count} from {start} is out of bounds for length {bytes.Length}", location ?? "<unknown>");
        foreach (var offset in new[] { start, start + count })
        {
            if (offset != bytes.Length && (bytes[offset] & 0xC0) == 0x80)
                throw new VplPanic($"byte offset {offset} is inside a character", location ?? "<unknown>");
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
