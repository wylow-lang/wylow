using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    static BoundBinaryOperator BinaryOperatorFor(TokenKind kind) => kind switch
    {
        TokenKind.Plus => BoundBinaryOperator.Add,
        TokenKind.Minus => BoundBinaryOperator.Subtract,
        TokenKind.Star => BoundBinaryOperator.Multiply,
        TokenKind.Slash => BoundBinaryOperator.Divide,
        TokenKind.Percent => BoundBinaryOperator.Remainder,
        TokenKind.Ampersand => BoundBinaryOperator.BitwiseAnd,
        TokenKind.Pipe => BoundBinaryOperator.BitwiseOr,
        TokenKind.Caret => BoundBinaryOperator.BitwiseXor,
        TokenKind.LessLess => BoundBinaryOperator.ShiftLeft,
        TokenKind.GreaterGreater => BoundBinaryOperator.ShiftRight,
        TokenKind.AmpersandAmpersand => BoundBinaryOperator.LogicalAnd,
        TokenKind.PipePipe => BoundBinaryOperator.LogicalOr,
        TokenKind.EqualEqual => BoundBinaryOperator.Equal,
        TokenKind.BangEqual => BoundBinaryOperator.NotEqual,
        TokenKind.Less => BoundBinaryOperator.Less,
        TokenKind.LessEqual => BoundBinaryOperator.LessOrEqual,
        TokenKind.Greater => BoundBinaryOperator.Greater,
        TokenKind.GreaterEqual => BoundBinaryOperator.GreaterOrEqual,
        _ => throw new InvalidOperationException($"unexpected binary operator {kind}"),
    };

    /// <summary>An enum on the left of <c>==</c> or <c>!=</c> lets the right side name its variant alone, as in <c>this == Hearts</c>.</summary>
    BoundExpression BindBinary(BinaryExpr e)
    {
        var left = BindExpression(e.Left);
        var expected = e.Operator is TokenKind.EqualEqual or TokenKind.BangEqual && left.Type.Narrowed.IsEnum ? left.Type.Narrowed : null;
        return BindBinaryOperation(e.OperatorLocation, left, e.Operator, BindExpression(e.Right, expected));
    }

    static bool IsReferenceOrNull(WylType type) => type.IsReference || type == WylType.Null;

    BoundExpression BindBinaryOperation(Location location, BoundExpression left, TokenKind syntaxOperator, BoundExpression right)
    {
        if (left.Type == WylType.Error || right.Type == WylType.Error)
            return new BoundErrorExpression(location);

        var op = BinaryOperatorFor(syntaxOperator);
        var symbol = SyntaxFacts.GetText(syntaxOperator);
        var (leftType, rightType) = (left.Type, right.Type);
        Diagnostic Mismatch(NullableOperand nullable = NullableOperand.None, bool numeric = false) =>
            Catalog.BinaryOperatorNotApplicable(location, symbol, leftType.ToString(), rightType.ToString(), nullable, numeric);

        // A reference that may refer to nothing is compared with null, or narrowed to what it refers to.
        if (left.Type.IsNullableReference || right.Type.IsNullableReference)
        {
            if (op is BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual && (left.Type == WylType.Null || right.Type == WylType.Null))
                return new BoundBinary(location, left, op, right, WylType.Bool);
            var (narrowedLeft, narrowedRight) = (AssumeNonNull(left), AssumeNonNull(right));
            if (narrowedLeft.Type.IsNullableReference || narrowedRight.Type.IsNullableReference)
                return ErrorExpression(Mismatch(NullableOperands(left, right)));
            return BindBinaryOperation(location, narrowedLeft, syntaxOperator, narrowedRight);
        }

        if (left.Type.IsPointer || right.Type.IsPointer)
            return BindPointerOperation(location, left, op, right, Mismatch());

        if (left.Type.IsNullableValue || right.Type.IsNullableValue)
        {
            if (op is BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual)
                return BindNullableEquality(location, left, op, right, symbol, Mismatch());
            var (narrowedLeft, narrowedRight) = (AssumeNonNull(left), AssumeNonNull(right));
            if (narrowedLeft.Type.IsNullableValue || narrowedRight.Type.IsNullableValue)
                return ErrorExpression(Mismatch(NullableOperands(left, right)));
            return BindBinaryOperation(location, narrowedLeft, syntaxOperator, narrowedRight);
        }

        if (IsReferenceOrNull(left.Type) || IsReferenceOrNull(right.Type))
        {
            if (op is not (BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual) || !IsReferenceOrNull(left.Type) || !IsReferenceOrNull(right.Type))
                return ErrorExpression(Mismatch());
            // References compare by identity, and only when one could be the other.
            var (a, b) = (left.Type.NonNull, right.Type.NonNull);
            bool related = a == WylType.Null || b == WylType.Null || a.IsInterface || b.IsInterface
                || Conversions.IsSubtype(a, b, currentFunction) || Conversions.IsSubtype(b, a, currentFunction);
            if (!related)
                return ErrorExpression(Mismatch());
            return new BoundBinary(location, left, op, right, WylType.Bool);
        }

        if (op is BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual && (left.Type.IsAggregate || right.Type.IsAggregate))
        {
            if (left.Type != right.Type)
                return ErrorExpression(Mismatch());
            var valueType = left.Type;
            while (valueType is FixedArrayType array)
                valueType = array.Element;
            if (valueType.IsStructOrEnum && valueType.FindNonEquatableField() is { } field)
            {
                return ErrorExpression(Catalog.FieldNotEquatable(location, symbol, left.Type.ToString(), valueType.ToString(), field.Name, TypeMap.For(valueType).Apply(field.Type).ToString()));
            }
            if (!left.Type.SupportsEquality)
                return ErrorExpression(Catalog.TypeNotEquatable(location, symbol, left.Type.ToString(), right.Type.ToString(), valueType.ToString()));
            return new BoundBinary(location, left, op, right, WylType.Bool);
        }

        // A char has no arithmetic of its own; in arithmetic it is its code point, so `c - '0'` is an int.
        if (op is not (BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual or BoundBinaryOperator.Less or BoundBinaryOperator.LessOrEqual
            or BoundBinaryOperator.Greater or BoundBinaryOperator.GreaterOrEqual or BoundBinaryOperator.LogicalAnd or BoundBinaryOperator.LogicalOr))
        {
            if (left.Type == WylType.Char)
                left = Convert(left, WylType.Int);
            if (right.Type == WylType.Char)
                right = Convert(right, WylType.Int);
        }

        bool bothBool = left.Type == WylType.Bool && right.Type == WylType.Bool;
        bool bothChar = left.Type == WylType.Char && right.Type == WylType.Char;
        bool bothString = left.Type == WylType.String && right.Type == WylType.String;
        bool bothNumeric = left.Type.IsNumeric && right.Type.IsNumeric;
        bool bothInteger = left.Type.IsInteger && right.Type.IsInteger;

        WylType? operandType = null;
        bool producesBool = false;
        switch (op)
        {
            case BoundBinaryOperator.ShiftLeft or BoundBinaryOperator.ShiftRight:
                if (!bothInteger)
                    break;
                // The count is masked, so an out-of-range constant would silently shift by something else.
                if (right is BoundLiteral { Integer: { } count } && (count < 0 || count >= left.Type.ShiftWidth))
                {
                    var widen = left.Type.ShiftWidth < 32 ? $"; widen it first, as in '(int)x {symbol} {count}'" : "";
                    return ErrorExpression(Catalog.ShiftCountOutOfRange(right.Location, symbol, (long)count, left.Type.ToString(), left.Type.ShiftWidth));
                }
                return new BoundBinary(location, left, op, right, left.Type);
            case BoundBinaryOperator.LogicalAnd or BoundBinaryOperator.LogicalOr:
                operandType = bothBool ? WylType.Bool : null;
                break;
            case BoundBinaryOperator.BitwiseAnd or BoundBinaryOperator.BitwiseOr or BoundBinaryOperator.BitwiseXor:
                operandType = bothBool ? WylType.Bool : bothInteger ? CommonNumericType(left, right) : null;
                break;
            case BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual:
                operandType = bothBool || bothChar || bothString ? left.Type : bothNumeric ? CommonNumericType(left, right) : null;
                producesBool = true;
                break;
            case BoundBinaryOperator.Less or BoundBinaryOperator.LessOrEqual
                or BoundBinaryOperator.Greater or BoundBinaryOperator.GreaterOrEqual:
                operandType = bothChar ? WylType.Char : bothNumeric ? CommonNumericType(left, right) : null;
                producesBool = true;
                break;
            default:
                operandType = bothNumeric ? CommonNumericType(left, right) : null;
                break;
        }

        if (operandType is null)
        {
            return ErrorExpression(Mismatch(numeric: bothNumeric));
        }
        var resultType = producesBool ? WylType.Bool : operandType;
        return new BoundBinary(location, Convert(left, operandType), op, Convert(right, operandType), resultType);
    }

    /// <summary>
    /// A nullable value is equal to null only when it is null. Otherwise both sides become nullables
    /// of the type their values compare as, and null equals only null.
    /// </summary>
    BoundExpression BindNullableEquality(Location location, BoundExpression left, BoundBinaryOperator op, BoundExpression right, string symbol, Diagnostic mismatch)
    {
        if (left.Type == WylType.Null || right.Type == WylType.Null)
            return new BoundBinary(location, left, op, right, WylType.Bool);
        WylType[] candidates = [left.Type.NonNull, right.Type.NonNull];
        var type = candidates.FirstOrDefault(t => !t.IsReference && t.CanBeNullable && CanConvert(left, t.Nullable) && CanConvert(right, t.Nullable));
        if (type is null)
            return ErrorExpression(mismatch);
        if (!type.SupportsEquality)
            return ErrorExpression(Catalog.TypeNotEquatable(location, symbol, left.Type.ToString(), right.Type.ToString(), type.ToString()));
        return new BoundBinary(location, Convert(left, type.Nullable), op, Convert(right, type.Nullable), WylType.Bool);
    }

    /// <summary>Pointers compare by address, and in unsafe code move by a number of elements.</summary>
    BoundExpression BindPointerOperation(Location location, BoundExpression left, BoundBinaryOperator op, BoundExpression right, Diagnostic mismatch)
    {
        if (op is BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual)
        {
            var type = left.Type.IsPointer && CanConvert(right, left.Type) ? left.Type : right.Type;
            if (!CanConvert(left, type) || !CanConvert(right, type))
                return ErrorExpression(mismatch);
            return new BoundBinary(location, Convert(left, type), op, Convert(right, type), WylType.Bool);
        }
        if (op is not (BoundBinaryOperator.Add or BoundBinaryOperator.Subtract) || !left.Type.IsPointer || !CanConvert(right, WylType.Int))
            return ErrorExpression(mismatch);
        if (left.Type.IsVoidPointer)
            return ErrorExpression(Catalog.VoidPointerAccess(location, "do arithmetic on"));
        if (!InUnsafe)
            return ErrorExpression(Catalog.PointerArithmetic(location));
        return new BoundBinary(location, left, op, Convert(right, WylType.Int), left.Type);
    }

    /// <summary>
    /// The type two numbers operate in. A constant takes the other side's type, so <c>light - 1</c>
    /// stays a <c>byte</c>. Two integer types that don't convert to each other meet in the smallest
    /// integer type both convert to, so <c>int + uint</c> is a <c>long</c>.
    /// </summary>
    WylType? CommonNumericType(BoundExpression left, BoundExpression right)
    {
        if (left.Type == right.Type)
            return left.Type;
        if (IsConvertibleConstant(right, left.Type))
            return left.Type;
        if (IsConvertibleConstant(left, right.Type))
            return right.Type;
        if (Conversions.IsImplicit(left.Type, right.Type, currentFunction))
            return right.Type;
        if (Conversions.IsImplicit(right.Type, left.Type, currentFunction))
            return left.Type;
        if (left.Type.IsInteger && right.Type.IsInteger)
            return WylType.Integers.FirstOrDefault(t => Conversions.IsImplicit(left.Type, t, currentFunction) && Conversions.IsImplicit(right.Type, t, currentFunction));
        return null;
    }
}
