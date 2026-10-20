using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

public sealed partial class Binder
{
    BoundExpression BindCondition(Expr condition) => Convert(BindExpression(condition), VplType.Bool);

    /// <summary>
    /// <paramref name="expected"/> is the type the value will be converted to, if known. It only lets
    /// enum variants be written without their enum or type arguments; the caller still converts.
    /// </summary>
    BoundExpression BindExpression(Expr expression, VplType? expected = null) => expression switch
    {
        IntLiteralExpr e => BindIntegerLiteral(e, e.Location, negated: false),
        FloatLiteralExpr e => e.IsDouble
            ? new BoundLiteral(e.Location, VplType.Double, e.Value)
            : new BoundLiteral(e.Location, VplType.Float, (float)e.Value),
        BoolLiteralExpr e => new BoundLiteral(e.Location, VplType.Bool, e.Value),
        StringLiteralExpr e => new BoundLiteral(e.Location, VplType.String, e.Value),
        NullLiteralExpr e => new BoundNullLiteral(e.Location),
        NameExpr e => BindName(e, expected),
        ThisExpr e => BindThis(e),
        SuperExpr e => ErrorExpression(e.Location, "'super' can only be used to call a base class constructor or method"),
        MemberAccessExpr e => BindMemberAccess(e, expected),
        ConditionalAccessExpr e => BindConditionalFieldAccess(e),
        UnaryExpr e => BindUnary(e),
        NonNullExpr e => BindNonNull(e),
        BinaryExpr e => BindBinary(e),
        CoalesceExpr e => BindCoalesce(e),
        IsExpr e => BindIs(e),
        CallExpr e => BindCall(e, expected),
        NewExpr e => BindNew(e),
        InitializerExpr e => BindInitializer(e),
        CastExpr e => BindCast(e),
        ConditionalExpr e => BindConditional(e, expected),
        SwitchExpr e => BindSwitchExpression(e, expected),
        TryExpr e => ErrorExpression(e.Location, "'try' must be the whole value of a declaration, assignment, return, or expression statement"),
        RefArgExpr e => ErrorExpression(e.Location, "'ref' can only be used on a call argument"),
        ErrorExpr e => new BoundErrorExpression(e.Location),
        _ => throw new InvalidOperationException($"unexpected expression {expression.GetType().Name}"),
    };

    BoundErrorExpression ErrorExpression(Location location, string message)
    {
        Error(location, message);
        return new BoundErrorExpression(location);
    }

    string NoInstanceReason => noThisContext switch
    {
        NoThisContext.FieldInitializer => "field initializers cannot use 'this', fields, or instance methods",
        NoThisContext.SuperArguments => "arguments to 'super(...)' cannot use 'this', fields, or instance methods",
        _ => "static methods cannot use 'this', fields, or instance methods",
    };

    BoundExpression BindIntegerLiteral(IntLiteralExpr e, Location location, bool negated)
    {
        // Negation is folded into the literal so that int.MinValue and long.MinValue can be written.
        ulong limit = (e.IsLong ? (ulong)long.MaxValue : int.MaxValue) + (negated ? 1UL : 0UL);
        if (e.Value > limit)
        {
            return ErrorExpression(location, e.IsLong
                ? "integer literal is too large for 'long'"
                : "integer literal is too large for 'int'; add an 'L' suffix to make it a 'long'");
        }

        if (e.IsLong)
            return new BoundLiteral(location, VplType.Long, negated ? unchecked((long)(0UL - e.Value)) : (long)e.Value);
        return new BoundLiteral(location, VplType.Int, negated ? unchecked((int)(0U - (uint)e.Value)) : (int)e.Value);
    }

    bool IsValueName(string name) =>
        scope?.Lookup(name) is not null || (currentType is not null && LookupField(currentType.Type, name) is not null);

    /// <summary>
    /// The type named by an expression like <c>Vec3</c> or <c>Pair&lt;int, float&gt;</c>, or null
    /// when the expression is not a type name. A local or field hides a type of the same name.
    /// </summary>
    VplType? AsTypeName(Expr expression)
    {
        if (expression is not NameExpr name || IsValueName(name.Name) || !types.TryGetValue(name.Name, out var s))
            return null;
        return BindTypeName(s.Name, name.TypeArguments, name.Location);
    }

    BoundExpression BindName(NameExpr e, VplType? expected)
    {
        if (e.TypeArguments.Count > 0)
            return ErrorExpression(e.Location, "type arguments can only be used in a call or construction");

        if (scope!.Lookup(e.Name) is { } local)
            return new BoundLocalAccess(e.Location, local);

        if (currentType is not null && LookupField(currentType.Type, e.Name) is var (field, owner))
        {
            if (!hasThis)
                return ErrorExpression(e.Location, $"'{e.Name}' is an instance field; {NoInstanceReason}");
            CheckAccessible(e.Location, field.Owner, field.IsPrivate, $"field '{field.Owner.Name}.{e.Name}'");
            return new BoundFieldAccess(e.Location, new BoundThis(e.Location, currentType.Type), field, TypeMap.For(owner).Apply(field.Type));
        }

        if (types.ContainsKey(e.Name) || LookupTypeParameter(e.Name) is not null)
            return ErrorExpression(e.Location, $"'{e.Name}' is a type, not a value");
        if (functions.ContainsKey(e.Name) || (currentType is not null && LookupMethods(currentType.Type, e.Name).Count > 0))
            return ErrorExpression(e.Location, $"'{e.Name}' is a function; call it with '{e.Name}(...)'");
        if (ExpectedVariant(e.Name, expected) is { } variant)
            return BindVariant(e.Location, variant.Owner, expected, variant, null);
        return ErrorExpression(e.Location, $"'{e.Name}' is not defined{VariantHint(e.Name)}");
    }

    BoundExpression BindThis(ThisExpr e)
    {
        if (!hasThis)
        {
            return ErrorExpression(e.Location, currentType is null
                ? "'this' can only be used inside a struct or class"
                : $"'this' cannot be used here; {NoInstanceReason}");
        }
        return new BoundThis(e.Location, currentType!.Type);
    }

    /// <summary>
    /// Members of a nullable value can only be reached through a local that flow analysis can prove
    /// non-null; other nullable values need <c>?.</c> or <c>!</c>.
    /// </summary>
    BoundExpression RequireNonNull(BoundExpression receiver)
    {
        if (!receiver.Type.IsNullable)
            return receiver;
        if (receiver is BoundLocalAccess { AssumesNonNull: false } local)
            return new BoundLocalAccess(local.Location, local.Local, AssumesNonNull: true);
        return ErrorExpression(receiver.Location, $"'{TextOf(receiver.Location)}' might be null; use '?.' or '!' to access its members");
    }

    BoundExpression BindMemberAccess(MemberAccessExpr e, VplType? expected)
    {
        if (e.TypeArguments.Count > 0)
            return ErrorExpression(e.NameLocation, "type arguments can only be used in a call");
        if (e.Target is SuperExpr)
            return ErrorExpression(e.NameLocation, $"'super' can only be used to call methods; use 'this.{e.Name}' for fields");
        if (AsEnumName(e.Target, expected) is var (enumSymbol, enumType) && enumSymbol.FindVariant(e.Name) is { } variant)
            return BindVariant(e.Location, enumSymbol, enumType, variant, null);

        if (AsTypeName(e.Target) is { } typeName)
        {
            if (typeName == VplType.Error)
                return new BoundErrorExpression(e.Location);
            return ErrorExpression(e.NameLocation, LookupMethods(typeName, e.Name).Count > 0
                ? $"'{typeName.Symbol!.Name}.{e.Name}' is a method; call it with '{e.Name}(...)'"
                : $"'{typeName.Symbol!.Name}' has no static member '{e.Name}'");
        }

        return BindFieldAccess(e.Location, BindExpression(e.Target), e.Name, e.NameLocation);
    }

    BoundExpression BindFieldAccess(Location location, BoundExpression target, string name, Location nameLocation)
    {
        if (target.Type == VplType.Error)
            return new BoundErrorExpression(location);
        target = RequireNonNull(target);
        if (target.Type == VplType.Error)
            return new BoundErrorExpression(location);

        if (!target.Type.IsStruct && !target.Type.IsClass)
        {
            return ErrorExpression(nameLocation, LookupMethods(target.Type, name).Count > 0
                ? $"'{DisplayName(target.Type)}.{name}' is a method; call it with '{name}(...)'"
                : target.Type.IsEnum && target.Type.Symbol!.ValueFields.Any(f => f.Name == name)
                ? $"a variant's fields can only be read with a pattern, as in 'if (x is Variant(var {name}))'"
                : $"'{target.Type}' has no member '{name}'");
        }
        if (LookupField(target.Type, name) is not var (field, owner))
        {
            return ErrorExpression(nameLocation, LookupMethods(target.Type, name).Count > 0
                ? $"'{DisplayName(target.Type)}.{name}' is a method; call it with '{name}(...)'"
                : $"'{DisplayName(target.Type)}' has no field '{name}'");
        }
        CheckAccessible(nameLocation, field.Owner, field.IsPrivate, $"field '{field.Owner.Name}.{name}'");
        return new BoundFieldAccess(location, target, field, TypeMap.For(owner).Apply(field.Type));
    }

    // Private members are visible only inside their declaring type. Protected and package access
    // need no checks yet: every program is a single file, which is a single package.
    void CheckAccessible(Location location, TypeSymbol owner, bool isPrivate, string description)
    {
        if (isPrivate && currentType != owner)
            Error(location, $"{description} is private");
    }

    BoundExpression BindUnary(UnaryExpr e)
    {
        if (e.Operator == TokenKind.Minus && e.Operand is IntLiteralExpr literal)
            return BindIntegerLiteral(literal, e.Location, negated: true);

        var operand = BindExpression(e.Operand);
        if (operand.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);

        var (op, valid) = e.Operator switch
        {
            TokenKind.Minus => (BoundUnaryOperator.Negate, operand.Type.IsNumeric),
            TokenKind.Bang => (BoundUnaryOperator.LogicalNot, operand.Type == VplType.Bool),
            TokenKind.Tilde => (BoundUnaryOperator.BitwiseNot, operand.Type.IsInteger),
            _ => throw new InvalidOperationException($"unexpected unary operator {e.Operator}"),
        };
        if (!valid)
            return ErrorExpression(e.Location, $"operator '{SyntaxFacts.GetText(e.Operator)}' cannot be applied to '{operand.Type}'");
        return new BoundUnary(e.Location, op, operand);
    }

    BoundExpression BindNonNull(NonNullExpr e)
    {
        var operand = BindExpression(e.Operand);
        if (operand.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);
        if (!operand.Type.IsNullable)
            return ErrorExpression(e.Location, $"'!' can only be applied to a nullable value, and '{operand.Type}' is not nullable");
        return new BoundNonNull(e.Location, operand);
    }

    BoundExpression BindCoalesce(CoalesceExpr e)
    {
        var left = BindExpression(e.Left);
        var right = BindExpression(e.Right);
        if (left.Type == VplType.Error || right.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);
        if (!left.Type.IsNullable)
            return ErrorExpression(e.Left.Location, $"the left side of '??' must be nullable, and '{left.Type}' is not");

        // Prefer a non-null result: `a ?? b` is non-null when `b` is.
        VplType[] candidates = [left.Type.NonNull, right.Type, left.Type];
        var type = candidates.FirstOrDefault(t =>
            t != VplType.Null && Conversions.IsImplicit(left.Type.NonNull, t) && (Conversions.IsImplicit(right.Type, t) || IsConvertibleConstant(right, t)));
        if (type is null)
            return ErrorExpression(e.OperatorLocation, $"operator '??' cannot be applied to '{left.Type}' and '{right.Type}'");
        return new BoundCoalesce(e.Location, left, Convert(right, type), type);
    }

    bool IsResult(VplType? type) => type?.Symbol is { } s && s == resultSymbol;

    static bool IsTry(BoundExpression value) => value is BoundTry || (value is BoundConversion conversion && IsTry(conversion.Operand));

    /// <summary>The whole value of a statement, which unlike any other expression may be a <c>try</c>.</summary>
    BoundExpression BindValue(Expr expression, VplType? expected) =>
        expression is TryExpr t ? BindTry(t) : BindExpression(expression, expected == VplType.Error ? null : expected);

    BoundExpression BindTry(TryExpr e)
    {
        var operand = BindExpression(e.Operand);
        if (operand.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);
        if (!IsResult(operand.Type))
            return ErrorExpression(e.Operand.Location, $"'try' needs a 'Result', not '{operand.Type}'");
        var function = currentFunction;
        if (function is null || function.IsConstructor || function.IsDestructor || !IsResult(function.ReturnType))
            return ErrorExpression(e.Location, "'try' can only be used in a function that returns a 'Result'");
        var (error, expectedError) = (operand.Type.TypeArguments[1], function.ReturnType.TypeArguments[1]);
        if (error != expectedError)
        {
            return ErrorExpression(e.Location,
                $"'try' would return an error of type '{error}' from '{function.Name}', whose error type is '{expectedError}'; convert the error first");
        }
        return new BoundTry(e.Location, operand, operand.Type.TypeArguments[0]);
    }

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
        var expected = e.Operator is TokenKind.EqualEqual or TokenKind.BangEqual && left.Type.IsEnum ? left.Type : null;
        return BindBinaryOperation(e.OperatorLocation, left, e.Operator, BindExpression(e.Right, expected));
    }

    static bool IsReferenceOrNull(VplType type) => type.IsReference || type == VplType.Null;

    BoundExpression BindBinaryOperation(Location location, BoundExpression left, TokenKind syntaxOperator, BoundExpression right)
    {
        if (left.Type == VplType.Error || right.Type == VplType.Error)
            return new BoundErrorExpression(location);

        var op = BinaryOperatorFor(syntaxOperator);
        var symbol = SyntaxFacts.GetText(syntaxOperator);
        string Mismatch() => $"operator '{symbol}' cannot be applied to '{left.Type}' and '{right.Type}'";

        if (IsReferenceOrNull(left.Type) || IsReferenceOrNull(right.Type))
        {
            if (op is not (BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual) || !IsReferenceOrNull(left.Type) || !IsReferenceOrNull(right.Type))
                return ErrorExpression(location, Mismatch());
            // References compare by identity, and only when one could be the other.
            var (a, b) = (left.Type.NonNull, right.Type.NonNull);
            bool related = a == VplType.Null || b == VplType.Null || a.IsInterface || b.IsInterface
                || Conversions.IsSubtype(a, b) || Conversions.IsSubtype(b, a);
            if (!related)
                return ErrorExpression(location, Mismatch());
            return new BoundBinary(location, left, op, right, VplType.Bool);
        }

        if (op is BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual && (left.Type.IsStructOrEnum || right.Type.IsStructOrEnum))
        {
            if (left.Type != right.Type)
                return ErrorExpression(location, Mismatch());
            if (Conversions.FindNonEquatableField(left.Type) is { } field)
            {
                return ErrorExpression(location,
                    $"operator '{symbol}' cannot be applied to '{left.Type}' because its field '{field.Name}' of type '{TypeMap.For(left.Type).Apply(field.Type)}' does not support '=='");
            }
            return new BoundBinary(location, left, op, right, VplType.Bool);
        }

        bool bothBool = left.Type == VplType.Bool && right.Type == VplType.Bool;
        bool bothNumeric = left.Type.IsNumeric && right.Type.IsNumeric;
        bool bothInteger = left.Type.IsInteger && right.Type.IsInteger;

        VplType? operandType = null;
        bool producesBool = false;
        switch (op)
        {
            case BoundBinaryOperator.ShiftLeft or BoundBinaryOperator.ShiftRight:
                if (bothInteger)
                    return new BoundBinary(location, left, op, right, left.Type);
                break;
            case BoundBinaryOperator.LogicalAnd or BoundBinaryOperator.LogicalOr:
                operandType = bothBool ? VplType.Bool : null;
                break;
            case BoundBinaryOperator.BitwiseAnd or BoundBinaryOperator.BitwiseOr or BoundBinaryOperator.BitwiseXor:
                operandType = bothBool ? VplType.Bool : bothInteger ? CommonNumericType(left, right) : null;
                break;
            case BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual:
                operandType = bothBool ? VplType.Bool : bothNumeric ? CommonNumericType(left, right) : null;
                producesBool = true;
                break;
            case BoundBinaryOperator.Less or BoundBinaryOperator.LessOrEqual
                or BoundBinaryOperator.Greater or BoundBinaryOperator.GreaterOrEqual:
                operandType = bothNumeric ? CommonNumericType(left, right) : null;
                producesBool = true;
                break;
            default:
                operandType = bothNumeric ? CommonNumericType(left, right) : null;
                break;
        }

        if (operandType is null)
        {
            var hint = bothNumeric ? "; use an explicit cast to convert one side" : "";
            return ErrorExpression(location, Mismatch() + hint);
        }
        var resultType = producesBool ? VplType.Bool : operandType;
        return new BoundBinary(location, Convert(left, operandType), op, Convert(right, operandType), resultType);
    }

    VplType? CommonNumericType(BoundExpression left, BoundExpression right)
    {
        if (left.Type == right.Type)
            return left.Type;
        if (CanConvert(left, right.Type))
            return right.Type;
        if (CanConvert(right, left.Type))
            return left.Type;
        return null;
    }

    static bool CanConvert(BoundExpression expression, VplType to) =>
        Conversions.IsImplicit(expression.Type, to) || IsConvertibleConstant(expression, to) || IsNarrowable(expression, to);

    /// <summary>Integer literals convert implicitly to float or double when no precision is lost.</summary>
    static bool IsConvertibleConstant(BoundExpression expression, VplType to) =>
        to.IsFloatingPoint
        && expression is BoundLiteral { Value: int or long } literal
        && Conversions.IsExactlyRepresentable(System.Convert.ToInt64(literal.Value), to);

    /// <summary>A nullable local converts to a non-null type if flow analysis proves it is not null there.</summary>
    static bool IsNarrowable(BoundExpression expression, VplType to) =>
        expression is BoundLocalAccess { AssumesNonNull: false, Type.IsNullable: true }
        && !to.IsNullable
        && Conversions.IsImplicit(expression.Type.NonNull, to);

    BoundExpression Convert(BoundExpression expression, VplType to)
    {
        if (expression.Type == to || expression.Type == VplType.Error || to == VplType.Error)
            return expression;

        if (IsConvertibleConstant(expression, to))
        {
            long value = System.Convert.ToInt64(((BoundLiteral)expression).Value);
            return new BoundLiteral(expression.Location, to, to == VplType.Float ? (object)(float)value : (object)(double)value);
        }

        if (IsNarrowable(expression, to))
        {
            expression = new BoundLocalAccess(expression.Location, ((BoundLocalAccess)expression).Local, AssumesNonNull: true);
            if (expression.Type == to)
                return expression;
        }

        if (Conversions.IsImplicit(expression.Type, to))
            return new BoundConversion(expression.Location, to, expression);

        var hint = Conversions.IsExplicit(expression.Type, to) ? "; use an explicit cast"
            : expression.Type.IsNullable && Conversions.IsImplicit(expression.Type.NonNull, to) ? "; check it for null first, or use '!'"
            : "";
        return ErrorExpression(expression.Location, $"cannot implicitly convert '{expression.Type}' to '{to}'{hint}");
    }

    BoundExpression BindCast(CastExpr e)
    {
        var operand = BindExpression(e.Operand);
        var type = BindType(e.Type, allowVoid: false);
        if (operand.Type == VplType.Error || type == VplType.Error)
            return new BoundErrorExpression(e.Location);
        if (operand.Type == type)
            return operand;
        if (!Conversions.IsExplicit(operand.Type, type))
            return ErrorExpression(e.Location, $"cannot convert '{operand.Type}' to '{type}'");
        return new BoundConversion(e.Location, type, operand);
    }

    BoundExpression BindConditional(ConditionalExpr e, VplType? expected)
    {
        var condition = BindCondition(e.Condition);
        var whenTrue = BindExpression(e.WhenTrue, expected);
        var whenFalse = BindExpression(e.WhenFalse, expected);
        if (whenTrue.Type == VplType.Error || whenFalse.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);

        var (t, f) = (whenTrue.Type, whenFalse.Type);
        VplType? type = t == f ? t
            : Conversions.IsImplicit(t, f) || IsConvertibleConstant(whenTrue, f) ? f
            : Conversions.IsImplicit(f, t) || IsConvertibleConstant(whenFalse, t) ? t
            : t == VplType.Null && f.IsReference ? f.Nullable
            : f == VplType.Null && t.IsReference ? t.Nullable
            : null;
        if (type is null || type == VplType.Void || type == VplType.Null)
            return ErrorExpression(e.Location, $"the branches of '?:' have incompatible types '{t}' and '{f}'");
        return new BoundConditional(e.Location, condition, Convert(whenTrue, type), Convert(whenFalse, type), type);
    }
}
