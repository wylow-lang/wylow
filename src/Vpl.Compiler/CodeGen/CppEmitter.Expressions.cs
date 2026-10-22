using System.Globalization;
using System.Text;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.CodeGen;

public sealed partial class CppEmitter
{
    string Expression(BoundExpression expression)
    {
        if (replacements.TryGetValue(expression, out var replacement))
            return replacement;
        return expression switch
        {
            BoundLiteral literal => Literal(literal),
            BoundNullLiteral => "nullptr",
            BoundLocalAccess { AssumesNonNull: true, Local.Type.IsNullableValue: true } access => $"vpl::assume_value({LocalName(access.Local)})",
            BoundLocalAccess access => LocalName(access.Local),
            BoundThis => This(),
            BoundFieldAccess field => FieldAccess(field),
            BoundIndex index => Index(index),
            BoundArrayLength length => $"vpl::length({Expression(length.Operand)})",
            BoundArrayData data => $"const_cast<{CppType(data.Type)}>({Expression(data.Array)}.data())",
            // Braces evaluate the elements left to right.
            BoundArrayLiteral literal => $"{CppType(literal.Type)}{{{string.Join(", ", literal.Elements.Select(Expression))}}}",
            BoundUnary unary => Unary(unary),
            BoundBinary binary => Binary(binary),
            BoundCall call => Call(call),
            BoundConstruction construction => Construction(construction),
            BoundNew creation => New(creation),
            BoundConversion conversion => Conversion(conversion),
            BoundConditional { Type.IsFunction: true, Type.IsEscaping: false } conditional => Choice(conditional),
            BoundConditional conditional =>
                $"({Expression(conditional.Condition)} ? {Expression(conditional.WhenTrue)} : {Expression(conditional.WhenFalse)})",
            BoundNonNull nonNull => $"vpl::unwrap({Expression(nonNull.Operand)}, {SourceLocation(nonNull.Location)})",
            BoundCoalesce coalesce => Coalesce(coalesce),
            BoundConditionalAccess access => ConditionalAccess(access),
            BoundIs pattern => Is(pattern),
            BoundSwitchExpression { Type.IsFunction: true, Type.IsEscaping: false } switchExpression => Choice(switchExpression),
            BoundSwitchExpression switchExpression => SwitchExpression(switchExpression),
            BoundVariant variant => Variant(variant),
            BoundLambda lambda => Lambda(lambda),
            BoundInvocation invocation => Invocation(invocation),
            _ => throw new InvalidOperationException($"unexpected expression {expression.GetType().Name}"),
        };
    }

    string This() => capturedThis ?? context switch
    {
        Context.StructConstructor => "self",
        Context.ClassMember => $"vpl::Ref<{ObjectType(currentType!.Type)}>(this)",
        _ => "(*this)",
    };

    string FieldAccess(BoundFieldAccess field)
    {
        if (field.Receiver is BoundThis && !replacements.ContainsKey(field.Receiver))
            return (capturedThis is not null ? capturedThis + "->" : context == Context.StructConstructor ? "self." : "this->") + FieldName(field.Field);
        var receiver = Expression(field.Receiver);
        return receiver + (field.Receiver.Type.IsReference ? "->" : ".") + FieldName(field.Field);
    }

    /// <summary>
    /// An element is a place, so it can't come from <see cref="Ordered"/>'s lambda, which returns a
    /// copy. When the index must be evaluated after the array, <c>vpl::at</c> evaluates it itself.
    /// C++ already evaluates a pointer before its subscript.
    /// </summary>
    string Index(BoundIndex index)
    {
        if (index.Target.Type.IsPointer)
            return $"{Expression(index.Target)}[{Expression(index.Index)}]";
        var location = SourceLocation(index.Location);
        Operand array = new(index.Target, ByReference: true, PinRoot(index.Target));
        if (NeedsOrdering([array, new Operand(index.Index, false)]))
            return Ordered([array], parts => $"vpl::at({parts[0]}, [&] {{ return {Expression(index.Index)}; }}, {location})");
        return Ordered([array, new Operand(index.Index, false)], parts => $"vpl::at({parts[0]}, {parts[1]}, {location})");
    }

    string Unary(BoundUnary unary)
    {
        var operand = Expression(unary.Operand);
        return unary.Operator switch
        {
            BoundUnaryOperator.Negate when unary.Type.IsInteger => $"vpl::neg({operand}, {SourceLocation(unary.Location)})",
            BoundUnaryOperator.Negate => $"(-{operand})",
            BoundUnaryOperator.LogicalNot => $"(!{operand})",
            BoundUnaryOperator.BitwiseNot => $"static_cast<{CppType(unary.Type)}>(~{operand})",
            _ => throw new InvalidOperationException($"unexpected unary operator {unary.Operator}"),
        };
    }

    string Binary(BoundBinary binary)
    {
        if (binary.Operator is BoundBinaryOperator.LogicalAnd or BoundBinaryOperator.LogicalOr)
        {
            var symbol = binary.Operator == BoundBinaryOperator.LogicalAnd ? "&&" : "||";
            return $"({Expression(binary.Left)} {symbol} {Expression(binary.Right)})";
        }
        if (NullTested(binary) is { } tested)
        {
            var test = $"vpl::has_value({Expression(tested)})";
            return binary.Operator == BoundBinaryOperator.Equal ? $"(!{test})" : test;
        }
        if (binary.Left.Type.IsReference || binary.Left.Type == VplType.Null)
        {
            var symbol = binary.Operator == BoundBinaryOperator.Equal ? "==" : "!=";
            return Ordered(binary.Left, binary.Right, operands => $"(vpl::identity({operands[0]}) {symbol} vpl::identity({operands[1]}))");
        }
        return Ordered(binary.Left, binary.Right, operands => BinaryText(binary, operands[0], operands[1]));
    }

    /// <summary>The nullable value compared with the <c>null</c> literal, if that is the comparison.</summary>
    static BoundExpression? NullTested(BoundBinary binary) => (binary.Left, binary.Right) switch
    {
        ({ Type.IsNullableValue: true } value, BoundNullLiteral) => value,
        (BoundNullLiteral, { Type.IsNullableValue: true } value) => value,
        _ => null,
    };

    string BinaryText(BoundBinary binary, string left, string right)
    {
        var operandType = binary.Left.Type;
        var location = SourceLocation(binary.Location);
        if (operandType.IsInteger)
        {
            switch (binary.Operator)
            {
                case BoundBinaryOperator.Add: return $"vpl::add({left}, {right}, {location})";
                case BoundBinaryOperator.Subtract: return $"vpl::sub({left}, {right}, {location})";
                case BoundBinaryOperator.Multiply: return $"vpl::mul({left}, {right}, {location})";
                case BoundBinaryOperator.Divide: return $"vpl::div({left}, {right}, {location})";
                case BoundBinaryOperator.Remainder: return $"vpl::rem({left}, {right}, {location})";
                case BoundBinaryOperator.ShiftLeft: return $"vpl::shl({left}, {right})";
                case BoundBinaryOperator.ShiftRight: return $"vpl::shr({left}, {right})";
            }
        }
        if (operandType.IsFloatingPoint && binary.Operator == BoundBinaryOperator.Remainder)
            return $"std::fmod({left}, {right})";

        var cppOperator = binary.Operator switch
        {
            BoundBinaryOperator.Add => "+",
            BoundBinaryOperator.Subtract => "-",
            BoundBinaryOperator.Multiply => "*",
            BoundBinaryOperator.Divide => "/",
            BoundBinaryOperator.BitwiseAnd => "&",
            BoundBinaryOperator.BitwiseOr => "|",
            BoundBinaryOperator.BitwiseXor => "^",
            BoundBinaryOperator.Equal => "==",
            BoundBinaryOperator.NotEqual => "!=",
            BoundBinaryOperator.Less => "<",
            BoundBinaryOperator.LessOrEqual => "<=",
            BoundBinaryOperator.Greater => ">",
            BoundBinaryOperator.GreaterOrEqual => ">=",
            _ => throw new InvalidOperationException($"unexpected binary operator {binary.Operator}"),
        };

        // C++ promotes bitwise operands to int; cast back so overloads see the VPL type.
        if (binary.Operator is BoundBinaryOperator.BitwiseAnd or BoundBinaryOperator.BitwiseOr or BoundBinaryOperator.BitwiseXor)
            return $"static_cast<{CppType(binary.Type)}>({left} {cppOperator} {right})";
        return $"({left} {cppOperator} {right})";
    }

    Operand ReceiverOperand(BoundExpression receiver)
    {
        var type = receiver.Type;
        if (type.IsReference || type.IsTypeParameter)
            return new Operand(receiver, ByReference: true, PinRoot(receiver), PinValue: MayBeReplaced(receiver));
        return new Operand(receiver, ByReference: true, PinRoot(receiver));
    }

    Operand ArgumentOperand(MethodReference method, int index, BoundExpression argument)
    {
        var value = PassedValue(method.ParameterType(index), argument);
        var operand = ArgumentOperand(value, method.ParameterRefKind(index));
        return IsCopiedForReference(argument, value) ? operand with { Keep = false, Copy = true } : operand;
    }

    /// <summary>
    /// Whether a stored function passed to a nullable non-escaping parameter must be copied: the
    /// parameter refers to it, while the call could replace it where it is stored.
    /// </summary>
    static bool IsCopiedForReference(BoundExpression argument, BoundExpression value) =>
        value != argument && value.Type.NonNull.IsEscaping && MayBeReplaced(value);

    Operand ArgumentOperand(BoundExpression argument, RefKind refKind) =>
        refKind != RefKind.None ? new Operand(argument, true, PinRoot(argument))
        : argument.Type.IsRefStruct ? new Operand(argument, false, PinRoot(argument))
        : new Operand(argument, false, Keep: !IsStable(argument) && MayHoldReferences(argument.Type));

    string Call(BoundCall call)
    {
        var method = call.Method;
        var function = method.Symbol;
        var operands = new List<Operand>();
        // `this` is never reassigned, so it needs no operand of its own.
        bool receiverOperand = call.Receiver is not null && call.Receiver is not BoundThis;
        if (receiverOperand)
            operands.Add(ReceiverOperand(call.Receiver!));
        operands.AddRange(call.Arguments.Select((argument, i) => ArgumentOperand(method, i, argument)));
        var location = function.PassesLocation ? SourceLocation(call.Location) : null;

        // Explicit template arguments keep C++ from deducing differently than VPL resolved.
        var typeArguments = TypeArgumentList(method.TypeArguments);
        // `.template` is required for a member template of a dependent type and harmless otherwise.
        var memberName = (typeArguments.Length > 0 ? "template " : "") + MethodName(function) + typeArguments;

        string Combine(IReadOnlyList<string> parts)
        {
            var arguments = FunctionArguments(method, parts.Skip(receiverOperand ? 1 : 0));
            if (location is not null)
                arguments = arguments.Append(location);
            var argumentText = string.Join(", ", arguments);

            // An extern struct's instance methods are members of its C++ class.
            if (function.IsExtern && function.IsInstanceMethod)
                return $"{ReceiverText(call, parts[0])}{function.BuiltinCppName}({argumentText})";
            if (function.BuiltinCppName is { } builtin)
                return $"{builtin}{typeArguments}({argumentText})";
            if (function.ContainingType is null)
                return $"{FunctionName(function)}{typeArguments}({argumentText})";
            if (function.IsStatic)
                return $"{ObjectType(method.Owner!)}::{memberName}({argumentText})";
            return $"{(receiverOperand ? ReceiverText(call, parts[0]) : ThisReceiverText(call))}{memberName}({argumentText})";
        }

        if (function.ReturnRefKind != RefKind.None && NeedsOrdering(operands))
            return OrderedReferenceCall(receiverOperand, operands, Combine);
        return Ordered(operands, Combine);
    }

    /// <summary>
    /// A returned reference can't come out of <see cref="Ordered"/>'s lambda, which returns a copy.
    /// C++17 evaluates the object of a member call before its arguments, so the object is passed to
    /// a lambda that evaluates the arguments in order and returns the reference.
    /// </summary>
    string OrderedReferenceCall(bool hasReceiverOperand, List<Operand> operands, Func<IReadOnlyList<string>, string> combine)
    {
        if (!hasReceiverOperand)
        {
            var (declarations, temps) = OrderedTemporaries(operands);
            return $"[&]() -> decltype(auto) {{ {declarations}return {combine(temps)}; }}()";
        }
        var receiver = InlineOperandText(operands[0]);
        var (argumentDeclarations, argumentTemps) = OrderedTemporaries([.. operands.Skip(1)]);
        var self = NewTemp();
        return $"vpl::with({receiver}, [&](auto&& {self}) -> decltype(auto) {{ {argumentDeclarations}return {combine([self, .. argumentTemps])}; }})";
    }

    /// <summary>The receiver and member operator. Calls go through the declaring type; see <c>vpl::as</c>.</summary>
    string ReceiverText(BoundCall call, string receiver)
    {
        var receiverType = call.Receiver!.Type;
        var declaring = ObjectType(call.Method.Owner!);
        if (receiverType.IsTypeParameter)
            return $"vpl::bound<{declaring}>({receiver}).";
        if (receiverType.IsReference)
            return receiverType.NonNull.Symbol == call.Method.Owner!.Symbol ? $"{receiver}->" : $"vpl::as<{declaring}>({receiver})->";
        return receiver + ".";
    }

    string ThisReceiverText(BoundCall call)
    {
        var declaring = ObjectType(call.Method.Owner!);
        var self = capturedThis is not null ? $"{capturedThis}.get()" : "this";
        if (call.IsSuperCall)
            return $"{self}->{declaring}::";
        if (context == Context.StructConstructor)
            return "self.";
        if (call.Method.Owner!.Symbol == currentType)
            return $"{self}->";
        return $"static_cast<{declaring}*>({self})->";
    }

    string Construction(BoundConstruction construction)
    {
        var type = CppType(construction.Type);
        var constructor = construction.Constructor;
        var operands = construction.Arguments
            .Select((argument, i) => ArgumentOperand(constructor, i, argument))
            .ToList();
        var created = Ordered(operands, arguments => constructor.Symbol.IsImplicit
            ? DefaultValue(construction.Type)
            : $"{type}::make({string.Join(", ", WithLocation(constructor.Symbol, FunctionArguments(constructor, arguments), construction.Location))})");
        if (construction.Initializers.Count == 0)
            return created;

        var temp = NewTemp();
        var body = new StringBuilder($"[&] {{ {type} {temp} = {created}; ");
        foreach (var initializer in construction.Initializers)
            body.Append($"{temp}.{FieldName(initializer.Field)} = {Expression(initializer.Value)}; ");
        return body.Append($"return {temp}; }}()").ToString();
    }

    string New(BoundNew creation)
    {
        var constructor = creation.Constructor;
        var operands = creation.Arguments
            .Select((argument, i) => ArgumentOperand(constructor, i, argument))
            .ToList();
        return Ordered(operands, arguments =>
            $"vpl::adopt(new {ObjectType(creation.Type)}({string.Join(", ", WithLocation(constructor.Symbol, FunctionArguments(constructor, arguments), creation.Location))}))");
    }

    IEnumerable<string> WithLocation(FunctionSymbol function, IEnumerable<string> arguments, Location location) =>
        function.PassesLocation ? arguments.Append(SourceLocation(location)) : arguments;

    string Coalesce(BoundCoalesce coalesce)
    {
        var temp = NewTemp();
        var value = coalesce.Left.Type.IsNullableValue ? $"vpl::assume_value({temp})" : temp;
        return $"[&]() -> {CppType(coalesce.Type)} {{ auto {temp} = {Expression(coalesce.Left)}; if (vpl::has_value({temp})) {{ return {value}; }} return {Expression(coalesce.Right)}; }}()";
    }

    string ConditionalAccess(BoundConditionalAccess access)
    {
        var value = LocalName(access.Value);
        var receiver = Expression(access.Receiver);
        var member = Expression(access.WhenNotNull);
        // A nullable value is copied, and the member reached through the value in the copy.
        var held = access.Receiver.Type.IsNullableValue ? NewTemp() : value;
        var start = $"auto {held} = {receiver}; if (!vpl::has_value({held}))";
        var bind = held == value ? "" : $"auto& {value} = vpl::assume_value({held}); ";
        if (access.Type == VplType.Void)
            return $"[&] {{ {start} {{ return; }} {bind}{member}; }}()";
        return $"[&]() -> {CppType(access.Type)} {{ {start} {{ return {CppType(access.Type)}(); }} {bind}return {member}; }}()";
    }

    string Variant(BoundVariant variant)
    {
        var operands = variant.Arguments.Select(a => new Operand(a, false)).ToList();
        return Ordered(operands, arguments => $"{CppType(variant.Type)}::{VariantFactory(variant.Variant)}({string.Join(", ", arguments)})");
    }

    string Conversion(BoundConversion conversion)
    {
        var from = conversion.Operand.Type;
        var to = conversion.Type;
        var operand = Expression(conversion.Operand);
        if (from == VplType.Null)
            return to.IsPointer ? $"static_cast<{CppType(to)}>(nullptr)" : $"{CppType(to)}()";
        if (IsNullableNonEscaping(to))
            throw new InvalidOperationException("a nullable non-escaping function is only made from an argument, by OptionalFunctionArgument");
        // A non-escaping function is used during the call it is passed to, while the value it was
        // converted from could be replaced; the copy lives until the end of the statement.
        if (IsNonEscaping(to))
            return IsStable(conversion.Operand) ? operand : $"{CppType(from)}({operand})";
        if (to.IsNullableValue)
            return $"{CppType(to)}({operand})";
        if (to.IsReference)
            return CppType(from) == CppType(to) ? operand : $"{CppType(to)}({operand})";
        if (stdlib.IsWeak(to))
            return $"{CppType(to)}({operand})";
        if (to.IsEnum)
            return $"{ObjectType(to)}::from_value({operand}, {SourceLocation(conversion.Location)})";
        if (from.IsEnum)
            return $"static_cast<{CppType(to)}>({operand}.tag)";
        if (from.IsFloatingPoint && to.IsInteger)
            return $"vpl::f2i<{CppType(to)}>({operand})";
        if (from == VplType.Double && to == VplType.Float)
            return $"vpl::d2f({operand})";
        return $"static_cast<{CppType(to)}>({operand})";
    }

    static string Literal(BoundLiteral literal) => literal.Value switch
    {
        bool b => b ? "true" : "false",
        int i when i == int.MinValue => "INT32_MIN",
        int i => i < 0 ? $"({i})" : i.ToString(CultureInfo.InvariantCulture),
        long l when l == long.MinValue => "INT64_MIN",
        long l => l < 0 ? $"(-INT64_C({-l}))" : $"INT64_C({l})",
        float f => FloatText(f.ToString("R", CultureInfo.InvariantCulture)) + "f",
        double d => FloatText(d.ToString("R", CultureInfo.InvariantCulture)),
        string s => $"vpl::String{{{CppStringLiteral(s)}, {Encoding.UTF8.GetByteCount(s)}}}",
        _ => throw new InvalidOperationException($"unexpected literal {literal.Value.GetType().Name}"),
    };

    static string FloatText(string text) => text.Contains('.') || text.Contains('E') ? text : text + ".0";

    const string CallerLocationParameter = "vpl_location";

    /// <summary>Where a panic is reported: in a function with <c>@CallerLocation</c>, wherever it was called from.</summary>
    string SourceLocation(Location location)
    {
        if (currentFunction?.PassesLocation == true)
            return CallerLocationParameter;
        var (line, column) = location.LineColumn;
        return CppStringLiteral($"{location.Source.FileName}:{line}:{column}");
    }

    static string CppStringLiteral(string value)
    {
        var result = new StringBuilder("\"");
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            switch (b)
            {
                case (byte)'"':
                    result.Append("\\\"");
                    break;
                case (byte)'\\':
                    result.Append("\\\\");
                    break;
                case >= 0x20 and < 0x7F:
                    result.Append((char)b);
                    break;
                default:
                    // Octal escapes stop after three digits; hex escapes would swallow following hex characters.
                    result.Append('\\').Append(System.Convert.ToString(b, 8).PadLeft(3, '0'));
                    break;
            }
        }
        return result.Append('"').ToString();
    }
}
