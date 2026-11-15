using System.Text;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;

namespace Wyl.Compiler.Lowering;

/// <summary>
/// The value of each static whose initializer the compiler can compute without running code. A
/// constant is a literal of a scalar type, a constant static of the same type, an integer sum,
/// difference, product, negation, or conversion of constants whose exact result fits its type, or
/// a fixed array of scalar constants. A static without an initializer is constant if its type is
/// scalar or a fixed array of a scalar type. A result that does not fit leaves the static to its
/// initializer, which panics or wraps at run time as the build mode says.
/// </summary>
sealed class StaticConstants
{
    readonly Dictionary<FieldSymbol, IrRvalue?> values = [];
    readonly HashSet<FieldSymbol> computing = [];

    /// <summary>The static's value, or null if its initializer computes it at run time.</summary>
    public IrRvalue? Of(FieldSymbol field)
    {
        if (values.TryGetValue(field, out var known))
            return known;
        // A static whose value depends on itself is in a cycle, which panics at run time.
        if (!computing.Add(field))
            return null;
        var value = field.Initializer is { } initializer ? Value(initializer, field.Type) : Default(field.Type);
        computing.Remove(field);
        values[field] = value;
        return value;
    }

    static bool IsScalar(WylType type) => type.IsNumeric || type == WylType.Bool || type == WylType.Char;

    static bool IsTable(WylType type) => type is FixedArrayType { Element: var element } && IsScalar(element);

    static IrRvalue? Default(WylType type) =>
        IsScalar(type) ? new IrUse(Zero(type))
        : IsTable(type) ? new IrDefault(type)
        : null;

    static IrConstant Zero(WylType type) =>
        type.IsNumeric ? new IrConstant(type, BoundLiteral.Number(default, type, 0).Value)
        : type == WylType.Bool ? new IrConstant(type, false)
        : new IrConstant(type, new Rune(0));

    IrRvalue? Value(BoundExpression expression, WylType type)
    {
        if (expression is BoundStaticFieldAccess access && access.Type == type && (IsScalar(type) || IsTable(type)))
            return Of(access.Field);
        if (IsScalar(type))
            return Scalar(expression) is { } constant && constant.Type == type ? new IrUse(constant) : null;
        if (IsTable(type) && expression is BoundArrayLiteral array && array.Type == type)
        {
            var element = ((FixedArrayType)type).Element;
            var elements = new List<IrOperand>();
            foreach (var item in array.Elements)
            {
                if (Scalar(item) is not { } constant || constant.Type != element)
                    return null;
                elements.Add(constant);
            }
            return new IrArray(type, elements);
        }
        return null;
    }

    IrConstant? Scalar(BoundExpression expression)
    {
        switch (expression)
        {
            case BoundLiteral literal when IsScalar(literal.Type):
                return new IrConstant(literal.Type, literal.Value);
            case BoundStaticFieldAccess access when IsScalar(access.Type):
                return Of(access.Field) is IrUse { Operand: IrConstant constant } ? constant : null;
            case BoundConversion { Type.IsInteger: true, Operand.Type.IsInteger: true } conversion:
                return Integer(conversion.Operand) is { } converted ? Fitted(conversion.Type, converted) : null;
            case BoundUnary { Operator: BoundUnaryOperator.Negate, Type.IsInteger: true } unary:
                return Integer(unary.Operand) is { } negated ? Fitted(unary.Type, -negated) : null;
            case BoundBinary { Type.IsInteger: true } binary when binary.Left.Type == binary.Type && binary.Right.Type == binary.Type:
                if (Integer(binary.Left) is not { } left || Integer(binary.Right) is not { } right)
                    return null;
                return binary.Operator switch
                {
                    BoundBinaryOperator.Add => Fitted(binary.Type, left + right),
                    BoundBinaryOperator.Subtract => Fitted(binary.Type, left - right),
                    BoundBinaryOperator.Multiply => Fitted(binary.Type, left * right),
                    _ => null,
                };
            default:
                return null;
        }
    }

    Int128? Integer(BoundExpression expression) =>
        expression.Type.IsInteger && Scalar(expression) is { } constant
            ? new BoundLiteral(default, constant.Type, constant.Value!).Integer
            : null;

    /// <summary>
    /// The constant of an exact integer result, or null if the result is outside the type's range
    /// on some target, so that the program computes it at run time.
    /// </summary>
    static IrConstant? Fitted(WylType type, Int128 value) =>
        type.Range!.Value.Contains(value) && type.HeldRange!.Value.Contains(value)
            ? new IrConstant(type, BoundLiteral.Number(default, type, value).Value)
            : null;
}
