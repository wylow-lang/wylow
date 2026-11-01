using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    BoundExpression BindCondition(Expr condition) => Convert(BindExpression(condition), WylType.Bool);

    /// <summary>
    /// <paramref name="expected"/> is the type the value will be converted to, if known. It only lets
    /// enum variants be written without their enum or type arguments; the caller still converts,
    /// so a nullable value expects the value inside it.
    /// </summary>
    BoundExpression BindExpression(Expr expression, WylType? expected = null)
    {
        if (expression is ConditionalExpr or SwitchExpr)
            expected = ChoiceTarget(expected);
        return BindExpressionExpecting(expression, expected is { IsNullableValue: true } or { IsNullableReference: true } ? expected.Narrowed : expected);
    }

    BoundExpression BindExpressionExpecting(Expr expression, WylType? expected) => expression switch
    {
        IntLiteralExpr e => BindIntegerLiteral(e, e.Location, negated: false),
        FloatLiteralExpr e => e.IsDouble
            ? new BoundLiteral(e.Location, WylType.Double, e.Value)
            : new BoundLiteral(e.Location, WylType.Float, (float)e.Value),
        BoolLiteralExpr e => new BoundLiteral(e.Location, WylType.Bool, e.Value),
        CharLiteralExpr e => new BoundLiteral(e.Location, WylType.Char, e.Value),
        StringLiteralExpr e => new BoundLiteral(e.Location, WylType.String, e.Value),
        InterpolatedStringExpr e => BindInterpolatedString(e),
        NullLiteralExpr e => new BoundNullLiteral(e.Location),
        NameExpr e => BindName(e, expected),
        PathExpr e => BindPath(e, expected),
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
        IndexExpr e => BindIndex(e),
        ArrayLiteralExpr e => BindArrayLiteral(e, expected),
        NewArrayExpr e => BindNewArray(e),
        NewExpr e => BindNew(e),
        CastExpr e => BindCast(e),
        ConditionalExpr e => BindConditional(e, expected),
        SwitchExpr e => BindSwitchExpression(e, expected),
        TryExpr e => BindTry(e),
        RefArgExpr e => ErrorExpression(e.Location, "'ref' can only be used on a call argument"),
        NamedArgExpr e => ErrorExpression(e.Location, "a named argument can only be used in a call"),
        LambdaExpr e => expected is FunctionType function ? BindLambda(e, function, null)
            : expected == WylType.Error ? new BoundErrorExpression(e.Location)
            : ErrorExpression(e.Location, "a lambda needs a function type from where it is used, as in '(int) -> int f = x -> x + 1;'"),
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
        NoThisContext.DefaultValue => "default values cannot use 'this', fields, or instance methods",
        _ => "static methods cannot use 'this', fields, or instance methods",
    };

    BoundExpression BindIntegerLiteral(IntLiteralExpr e, Location location, bool negated)
    {
        var type = e.Suffix switch
        {
            IntegerSuffix.Unsigned => WylType.UInt,
            IntegerSuffix.Long => WylType.Long,
            IntegerSuffix.UnsignedLong => WylType.ULong,
            _ => WylType.Int,
        };
        if (negated && !type.IsSigned)
            return ErrorExpression(location, $"operator '-' cannot be applied to '{type}'");

        // Negation is folded into the literal so that int.MinValue and long.MinValue can be written.
        Int128 value = negated ? -(Int128)e.Value : e.Value;
        if (!type.Range!.Value.Contains(value))
        {
            var hint = type == WylType.Int && value > 0 && value <= uint.MaxValue ? "; add a 'u' suffix to make it a 'uint', or an 'L' suffix to make it a 'long'"
                : type == WylType.Int ? "; add an 'L' suffix to make it a 'long'"
                : type == WylType.UInt ? "; add a 'uL' suffix to make it a 'ulong'"
                : "";
            return ErrorExpression(location, $"integer literal is too large for '{type}'{hint}");
        }
        return BoundLiteral.Number(location, type, value);
    }

    bool IsValueName(string name) =>
        scope?.Lookup(name) is not null || (currentType is not null && LookupField(currentType.Type, name) is not null);

    /// <summary>
    /// The type named by an expression like <c>Vec3</c> or <c>Pair&lt;int, float&gt;</c>, or null
    /// when the expression is not a type name. A local or field hides a type of the same name.
    /// </summary>
    WylType? AsTypeName(Expr expression)
    {
        if (AsTypeSymbol(expression) is not var (s, name))
            return null;
        if (expression is NameExpr)
            return BindTypeName(name.Name, name.TypeArguments, name.Location);
        var arguments = name.TypeArguments.Select(a => BindType(a, allowVoid: false)).ToList();
        return arguments.Contains(WylType.Error) ? WylType.Error : Construct(s, arguments, name.Location);
    }

    /// <summary>
    /// The declared type an expression names, as <c>Vec3</c> or <c>world::Chunk</c> write it, with
    /// the name that carries its type arguments; null if it names none. Looking doesn't report a
    /// path that leads nowhere, which binding the expression then does.
    /// </summary>
    (TypeSymbol Symbol, NameExpr Name)? AsTypeSymbol(Expr expression) => expression switch
    {
        NameExpr name when !IsValueName(name.Name) && LookupType(name.Name, name.Location) is { } s => (s, name),
        PathExpr path when FindPath(path.Modules, path.Name.Name, path.Name.Location) is { Type: { } s } => (s, path.Name),
        _ => null,
    };

    /// <summary>A name in a module used as a value: a function, as a reference where a function type is expected.</summary>
    BoundExpression BindPath(PathExpr e, WylType? expected)
    {
        if (ResolvePath(e.Modules, e.Name.Name, e.Name.Location) is not { } entry)
            return new BoundErrorExpression(e.Location);
        var written = TextOf(e.Location);
        if (entry.Type is not null)
            return ErrorExpression(e.Location, $"'{written}' is a type, not a value");
        if (expected?.NonNull is FunctionType function)
            return BindFunctionReference(e, entry.Functions!, function);
        return ErrorExpression(e.Location, $"'{written}' is a function; call it with '{written}(...)'{ReferenceHint(e)}");
    }

    BoundExpression BindName(NameExpr e, WylType? expected)
    {
        if (expected?.NonNull is FunctionType function && ReferencedFunctions(e) is { } referenced)
            return BindFunctionReference(e, referenced, function);
        if (e.TypeArguments.Count > 0)
            return ErrorExpression(e.Location, "type arguments can only be used in a call or construction");

        if (scope!.Lookup(e.Name) is { } local)
            return new BoundLocalAccess(e.Location, local, AssumesNonNull: local.IsReferenceBinding);

        if (currentType is not null && LookupField(currentType.Type, e.Name) is var (field, owner))
        {
            if (!hasThis)
                return ErrorExpression(e.Location, $"'{e.Name}' is an instance field; {NoInstanceReason}");
            CheckVisible(e.Location, field.Reach, $"field '{field.Owner.Name}.{e.Name}'");
            return new BoundFieldAccess(e.Location, new BoundThis(e.Location, currentType.Type), field, TypeMap.For(owner).Apply(field.Type));
        }

        if (LookupType(e.Name, e.Location) is not null || LookupTypeParameter(e.Name) is not null)
            return ErrorExpression(e.Location, $"'{e.Name}' is a type, not a value");
        if (LookupFunctions(e.Name, e.Location) is not null || (currentType is not null && LookupMethods(currentType.Type, e.Name).Count > 0))
            return ErrorExpression(e.Location, $"'{e.Name}' is a function; call it with '{e.Name}(...)'{ReferenceHint(e)}");
        if (ExpectedVariant(e.Name, expected) is { } variant)
            return BindVariant(e.Location, variant.Owner, expected, variant, null);
        if (HiddenElsewhere(e.Name, e.Location.Source) is { } hidden)
            return ErrorExpression(e.Location, hidden);
        return ErrorExpression(e.Location, $"'{e.Name}' is not defined{VariantHint(e.Name, e.Location.Source)}");
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
        if (!receiver.Type.MayBeNull)
            return receiver;
        if (receiver is BoundLocalAccess)
            return AssumeNonNull(receiver);
        return ErrorExpression(receiver.Location, receiver.Type.IsNullableReference
            ? MayReferToNothingMessage(receiver)
            : $"'{TextOf(receiver.Location)}' might be null; use '?.' or '!' to access its members");
    }

    string MayReferToNothingMessage(BoundExpression reference)
    {
        var type = (NullableReferenceType)reference.Type;
        return $"'{TextOf(reference.Location)}' might refer to nothing; check it with 'is {FunctionSymbol.RefPrefix(type.Kind)}{type.Referenced} name', or use '!'";
    }

    /// <summary>
    /// A nullable local used where only a value will do, or a <c>ref?</c> local where only a place
    /// will; flow analysis checks that it was narrowed.
    /// </summary>
    static BoundExpression AssumeNonNull(BoundExpression expression) =>
        expression is BoundLocalAccess { AssumesNonNull: false, Type.MayBeNull: true } local
            ? new BoundLocalAccess(local.Location, local.Local, AssumesNonNull: true)
            : expression;

    /// <summary>A <c>ref?</c> local used as the place it refers to; anything else as it is.</summary>
    static BoundExpression NarrowReference(BoundExpression expression) =>
        expression.Type.IsNullableReference ? AssumeNonNull(expression) : expression;

    static string NullableOperandHint(params BoundExpression[] operands) =>
        operands.Any(o => o.Type.IsNullableValue) ? "; check it for null first, or use '!' or '??'"
        : operands.Any(o => o.Type.IsNullableReference) ? "; check it for null first, or use '!'"
        : "";

    BoundExpression BindMemberAccess(MemberAccessExpr e, WylType? expected)
    {
        if (e.TypeArguments.Count > 0)
            return ErrorExpression(e.NameLocation, "type arguments can only be used in a call");
        if (e.Target is SuperExpr)
            return ErrorExpression(e.NameLocation, $"'super' can only be used to call methods; use 'this.{e.Name}' for fields");
        if (AsEnumName(e.Target, expected) is var (enumSymbol, enumType) && enumSymbol.FindVariant(e.Name) is { } variant)
            return BindVariant(e.Location, enumSymbol, enumType, variant, null);
        if (expected?.NonNull is FunctionType function && ReferencedFunctions(e) is { } referenced)
            return BindFunctionReference(e, referenced, function);

        if (AsTypeName(e.Target) is { } typeName)
        {
            if (typeName == WylType.Error)
                return new BoundErrorExpression(e.Location);
            return ErrorExpression(e.NameLocation, LookupMethods(typeName, e.Name).Count > 0
                ? $"'{typeName.Symbol!.Name}.{e.Name}' is a method; call it with '{e.Name}(...)'{ReferenceHint(e)}"
                : $"'{typeName.Symbol!.Name}' has no static member '{e.Name}'");
        }

        return BindFieldAccess(e.Location, BindExpression(e.Target), e.Name, e.NameLocation);
    }

    BoundExpression BindFieldAccess(Location location, BoundExpression target, string name, Location nameLocation)
    {
        if (target.Type == WylType.Error)
            return new BoundErrorExpression(location);
        target = RequireNonNull(target);
        if (target.Type == WylType.Error)
            return new BoundErrorExpression(location);

        if (target.Type.IsFixedArray)
        {
            return name == "length"
                ? new BoundArrayLength(location, target)
                : ErrorExpression(nameLocation, $"'{target.Type}' has no member '{name}'; fixed arrays only have 'length'");
        }
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
        CheckVisible(nameLocation, field.Reach, $"field '{field.Owner.Name}.{name}'");
        return new BoundFieldAccess(location, target, field, TypeMap.For(owner).Apply(field.Type));
    }

    static bool HasMembers(WylType type) => type.IsStructOrEnum || type.IsReference || type.IsTypeParameter || type == WylType.String;

    BoundExpression BindUnary(UnaryExpr e)
    {
        if (e.Operator == TokenKind.Minus && e.Operand is IntLiteralExpr literal)
            return BindIntegerLiteral(literal, e.Location, negated: true);

        var operand = BindExpression(e.Operand);
        if (operand.Type == WylType.Error)
            return new BoundErrorExpression(e.Location);
        if (operand.Type.IsNullableValue || operand.Type.IsNullableReference)
            operand = AssumeNonNull(operand);

        var (op, valid) = e.Operator switch
        {
            TokenKind.Minus => (BoundUnaryOperator.Negate, operand.Type.IsFloatingPoint || operand.Type.IsSigned),
            TokenKind.Bang => (BoundUnaryOperator.LogicalNot, operand.Type == WylType.Bool),
            TokenKind.Tilde => (BoundUnaryOperator.BitwiseNot, operand.Type.IsInteger),
            _ => throw new InvalidOperationException($"unexpected unary operator {e.Operator}"),
        };
        if (!valid)
            return ErrorExpression(e.Location, $"operator '{SyntaxFacts.GetText(e.Operator)}' cannot be applied to '{operand.Type}'{NullableOperandHint(operand)}");
        return new BoundUnary(e.Location, op, operand);
    }

    BoundExpression BindNonNull(NonNullExpr e)
    {
        var operand = BindExpression(e.Operand);
        if (operand.Type == WylType.Error)
            return new BoundErrorExpression(e.Location);
        if (!operand.Type.MayBeNull)
            return ErrorExpression(e.Location, $"'!' can only be applied to a nullable value, and '{operand.Type}' is not nullable");
        return new BoundNonNull(e.Location, operand);
    }

    BoundExpression BindCoalesce(CoalesceExpr e)
    {
        var left = BindExpression(e.Left);
        var right = BindExpression(e.Right);
        if (left.Type == WylType.Error || right.Type == WylType.Error)
            return new BoundErrorExpression(e.Location);
        if (left.Type.IsNullableReference)
            return ErrorExpression(e.OperatorLocation, $"'??' cannot be applied to '{left.Type}'; check it for null with 'if' instead");
        if (!left.Type.IsNullable)
            return ErrorExpression(e.Left.Location, $"the left side of '??' must be nullable, and '{left.Type}' is not");
        // Its result would refer to a function that may be made only for this operation.
        if (left.Type.IsNullableNonEscapingFunction)
            return ErrorExpression(e.OperatorLocation, "'??' cannot be applied to a nullable non-escaping function; check it for null with 'if' instead");

        // Prefer a non-null result: `a ?? b` is non-null when `b` is.
        WylType[] candidates = [left.Type.NonNull, right.Type, left.Type];
        var type = candidates.FirstOrDefault(t =>
            t != WylType.Null && Conversions.IsImplicit(left.Type.NonNull, t) && (Conversions.IsImplicit(right.Type, t) || IsConvertibleConstant(right, t)));
        if (type is null)
            return ErrorExpression(e.OperatorLocation, $"operator '??' cannot be applied to '{left.Type}' and '{right.Type}'");
        return new BoundCoalesce(e.Location, left, Convert(right, type), type);
    }

    bool IsResult(WylType? type) => type?.Symbol is { } s && s == stdlib.Result;

    /// <summary>The whole value of a statement. A lambda can only be bound with a type, so it is left alone when that type has failed.</summary>
    BoundExpression BindValue(Expr expression, WylType? expected) =>
        BindExpression(expression, expected == WylType.Error && expression is not LambdaExpr ? null : expected);

    BoundExpression BindTry(TryExpr e)
    {
        var operand = BindExpression(e.Operand);
        if (operand.Type == WylType.Error)
            return new BoundErrorExpression(e.Location);
        if (!IsResult(operand.Type))
            return ErrorExpression(e.Operand.Location, $"'try' needs a 'Result', not '{operand.Type}'");
        if (inferring is { } lambda && lambda.Function == currentFunction)
            return ErrorExpression(e.Location, "'try' cannot be used in a lambda whose return type is inferred from what it returns; specify the call's type arguments");
        var function = currentFunction;
        if (function is null || function.IsConstructor || function.IsDestructor || !IsResult(function.ReturnType))
            return ErrorExpression(e.Location, "'try' can only be used in a function that returns a 'Result'");
        var (error, expectedError) = (operand.Type.TypeArguments[1], function.ReturnType.TypeArguments[1]);
        if (error != expectedError)
        {
            return ErrorExpression(e.Location,
                $"'try' would return an error of type '{error}' from {Describe(function)}, whose error type is '{expectedError}'; convert the error first");
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
        string Mismatch() => $"operator '{symbol}' cannot be applied to '{leftType}' and '{rightType}'";

        // A reference that may refer to nothing is compared with null, or narrowed to what it refers to.
        if (left.Type.IsNullableReference || right.Type.IsNullableReference)
        {
            if (op is BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual && (left.Type == WylType.Null || right.Type == WylType.Null))
                return new BoundBinary(location, left, op, right, WylType.Bool);
            var (narrowedLeft, narrowedRight) = (AssumeNonNull(left), AssumeNonNull(right));
            if (narrowedLeft.Type.IsNullableReference || narrowedRight.Type.IsNullableReference)
                return ErrorExpression(location, Mismatch() + NullableOperandHint(left, right));
            return BindBinaryOperation(location, narrowedLeft, syntaxOperator, narrowedRight);
        }

        if (left.Type.IsPointer || right.Type.IsPointer)
            return BindPointerOperation(location, left, op, right, Mismatch());

        if (left.Type.IsNullableValue || right.Type.IsNullableValue)
        {
            if (op is BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual)
                return BindNullableEquality(location, left, op, right, Mismatch());
            var (narrowedLeft, narrowedRight) = (AssumeNonNull(left), AssumeNonNull(right));
            if (narrowedLeft.Type.IsNullableValue || narrowedRight.Type.IsNullableValue)
                return ErrorExpression(location, Mismatch() + NullableOperandHint(left, right));
            return BindBinaryOperation(location, narrowedLeft, syntaxOperator, narrowedRight);
        }

        if (IsReferenceOrNull(left.Type) || IsReferenceOrNull(right.Type))
        {
            if (op is not (BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual) || !IsReferenceOrNull(left.Type) || !IsReferenceOrNull(right.Type))
                return ErrorExpression(location, Mismatch());
            // References compare by identity, and only when one could be the other.
            var (a, b) = (left.Type.NonNull, right.Type.NonNull);
            bool related = a == WylType.Null || b == WylType.Null || a.IsInterface || b.IsInterface
                || Conversions.IsSubtype(a, b) || Conversions.IsSubtype(b, a);
            if (!related)
                return ErrorExpression(location, Mismatch());
            return new BoundBinary(location, left, op, right, WylType.Bool);
        }

        if (op is BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual && (left.Type.IsAggregate || right.Type.IsAggregate))
        {
            if (left.Type != right.Type)
                return ErrorExpression(location, Mismatch());
            var valueType = left.Type;
            while (valueType is FixedArrayType array)
                valueType = array.Element;
            if (valueType.IsStructOrEnum && valueType.FindNonEquatableField() is { } field)
            {
                var owner = valueType == left.Type ? "its" : $"'{valueType}'s";
                return ErrorExpression(location,
                    $"operator '{symbol}' cannot be applied to '{left.Type}' because {owner} field '{field.Name}' of type '{TypeMap.For(valueType).Apply(field.Type)}' does not support '=='");
            }
            if (!left.Type.SupportsEquality)
                return ErrorExpression(location, $"operator '{symbol}' cannot be applied to '{left.Type}', since '{valueType}' does not support '=='");
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
                    return ErrorExpression(right.Location,
                        $"a shift count of {count} is out of range for '{left.Type}', which can shift by 0 to {left.Type.ShiftWidth - 1}{widen}");
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
            var hint = bothNumeric ? "; use an explicit cast to convert one side" : "";
            return ErrorExpression(location, Mismatch() + hint);
        }
        var resultType = producesBool ? WylType.Bool : operandType;
        return new BoundBinary(location, Convert(left, operandType), op, Convert(right, operandType), resultType);
    }

    /// <summary>
    /// A nullable value is equal to null only when it is null. Otherwise both sides become nullables
    /// of the type their values compare as, and null equals only null.
    /// </summary>
    BoundExpression BindNullableEquality(Location location, BoundExpression left, BoundBinaryOperator op, BoundExpression right, string mismatch)
    {
        if (left.Type == WylType.Null || right.Type == WylType.Null)
            return new BoundBinary(location, left, op, right, WylType.Bool);
        WylType[] candidates = [left.Type.NonNull, right.Type.NonNull];
        var type = candidates.FirstOrDefault(t => !t.IsReference && t.CanBeNullable && CanConvert(left, t.Nullable) && CanConvert(right, t.Nullable));
        if (type is null)
            return ErrorExpression(location, mismatch);
        if (!type.SupportsEquality)
            return ErrorExpression(location, $"{mismatch}, since '{type}' does not support '=='");
        return new BoundBinary(location, Convert(left, type.Nullable), op, Convert(right, type.Nullable), WylType.Bool);
    }

    /// <summary>Pointers compare by address, and in unsafe code move by a number of elements.</summary>
    BoundExpression BindPointerOperation(Location location, BoundExpression left, BoundBinaryOperator op, BoundExpression right, string mismatch)
    {
        if (op is BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual)
        {
            var type = left.Type.IsPointer ? left.Type : right.Type;
            if (!CanConvert(left, type) || !CanConvert(right, type))
                return ErrorExpression(location, mismatch);
            return new BoundBinary(location, Convert(left, type), op, Convert(right, type), WylType.Bool);
        }
        if (op is not (BoundBinaryOperator.Add or BoundBinaryOperator.Subtract) || !left.Type.IsPointer || !CanConvert(right, WylType.Int))
            return ErrorExpression(location, mismatch);
        if (!InUnsafe)
            return ErrorExpression(location, "pointer arithmetic is only allowed in 'unsafe' code");
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
        if (Conversions.IsImplicit(left.Type, right.Type))
            return right.Type;
        if (Conversions.IsImplicit(right.Type, left.Type))
            return left.Type;
        if (left.Type.IsInteger && right.Type.IsInteger)
            return WylType.Integers.FirstOrDefault(t => Conversions.IsImplicit(left.Type, t) && Conversions.IsImplicit(right.Type, t));
        return null;
    }

    bool CanConvert(BoundExpression expression, WylType to) =>
        expression is BoundPendingLambda lambda ? (lambda.Inferred is { } inferred ? InferredLambdaFits(inferred, to) : LambdaFits(lambda, to))
        : expression is BoundPendingChoice choice ? to.NonNull.IsFunction && choice.Options.All(o => CanConvert(o, to))
        : Conversions.IsImplicit(expression.Type, to) || IsConvertibleConstant(expression, to) || IsNarrowable(expression, to)
        || IsWeakConversion(expression.Type, to) || SpanConversion(expression, to) is not null || IsNullableWrapping(expression, to)
        || IsPassedByIn(expression, to);

    /// <summary>
    /// An <c>in? T</c> parameter takes what an <c>in T</c> one would, which may be a temporary value:
    /// it refers to it for the call.
    /// </summary>
    bool IsPassedByIn(BoundExpression expression, WylType to) =>
        to is NullableReferenceType { Kind: RefKind.In } readOnly && expression.Type != WylType.Null && CanConvert(expression, readOnly.Referenced);

    /// <summary>A value converts to a nullable of any type it converts to, including by conversions only some values have.</summary>
    bool IsNullableWrapping(BoundExpression expression, WylType to) =>
        to.IsNullableValue && !expression.Type.IsNullable && expression.Type != WylType.Null && CanConvert(expression, to.NonNull);

    /// <summary>
    /// Unsuffixed integer literals convert implicitly to any integer type that holds them on every
    /// target; a suffix already chose the type. Any integer literal converts to float or double
    /// when no precision is lost.
    /// </summary>
    static bool IsConvertibleConstant(BoundExpression expression, WylType to) =>
        expression is BoundLiteral { Integer: { } value } literal
        && (to.IsInteger ? literal.Type == WylType.Int && to.HeldRange!.Value.Contains(value)
            : to.IsFloatingPoint && Conversions.IsExactlyRepresentable(value, to));

    /// <summary>
    /// A nullable local converts to a non-null type if flow analysis proves it is not null there, and
    /// a <c>ref?</c> local to the type it refers to.
    /// </summary>
    static bool IsNarrowable(BoundExpression expression, WylType to) =>
        expression is BoundLocalAccess { AssumesNonNull: false, Type: var type }
        && (type.IsNullableReference ? !to.IsNullableReference : type.IsNullable && !to.IsNullable)
        && Conversions.IsImplicit(type.Narrowed, to);

    BoundExpression Convert(BoundExpression expression, WylType to)
    {
        if (expression.Type == to || expression.Type == WylType.Error || to == WylType.Error)
            return expression;

        if (expression is BoundPendingLambda lambda)
        {
            if (to.NonNull is not FunctionType function)
                return ErrorExpression(expression.Location, NotFunctionTypeMessage(lambda, to));
            var bound = lambda.Inferred is { } inferred
                ? RetargetLambda(lambda, inferred, function)
                : BindLambda(lambda.SyntaxFor(function.Parameters), function, lambda.DeclaredParameters);
            return Convert(bound, to);
        }
        if (expression is BoundPendingChoice choice)
        {
            return to.NonNull.IsFunction
                ? Convert(choice.Finish(ChoiceTarget(to)!.NonNull), to)
                : ErrorExpression(expression.Location, $"a choice of lambdas cannot be converted to '{to}', which is not a function type");
        }

        if (IsConvertibleConstant(expression, to))
            return BoundLiteral.Number(expression.Location, to, ((BoundLiteral)expression).Integer!.Value);

        if (IsNarrowable(expression, to))
        {
            expression = new BoundLocalAccess(expression.Location, ((BoundLocalAccess)expression).Local, AssumesNonNull: true);
            if (expression.Type == to)
                return expression;
        }

        if (IsNullableWrapping(expression, to))
            return new BoundConversion(expression.Location, to, Convert(expression, to.NonNull));
        if (Conversions.IsImplicit(expression.Type, to) || IsWeakConversion(expression.Type, to))
            return new BoundConversion(expression.Location, to, expression);
        if (IsPassedByIn(expression, to))
            return new BoundConversion(expression.Location, to, Convert(expression, to.Referred));
        if (SpanConversion(expression, to) is { } span)
            return span;

        if (IsEscapingMismatch(expression.Type, to))
        {
            ReportNotEscaping(expression);
            return new BoundErrorExpression(expression.Location);
        }
        var hint = Conversions.IsExplicit(expression.Type, to) ? "; use an explicit cast"
            : expression.Type.MayBeNull && Conversions.IsImplicit(expression.Type.Narrowed, to) ? "; check it for null first, or use '!'"
            : SpanConversionHint(expression, to);
        return ErrorExpression(expression.Location, $"cannot implicitly convert '{expression.Type}' to '{to}'{hint}");
    }

    BoundExpression BindCast(CastExpr e)
    {
        var operand = BindExpression(e.Operand);
        var type = BindType(e.Type, allowVoid: false);
        if (operand.Type == WylType.Error || type == WylType.Error)
            return new BoundErrorExpression(e.Location);
        if (operand.Type == type)
            return operand;
        if (!Conversions.IsExplicit(operand.Type, type))
            return ErrorExpression(e.Location, $"cannot convert '{operand.Type}' to '{type}'");
        return new BoundConversion(e.Location, type, operand);
    }

    /// <summary><paramref name="mayWait"/> as for <see cref="BindSwitchExpression"/>.</summary>
    BoundExpression BindConditional(ConditionalExpr e, WylType? expected, bool mayWait = false)
    {
        var condition = BindCondition(e.Condition);
        var whenTrue = BindOption(e.WhenTrue, expected);
        var whenFalse = BindOption(e.WhenFalse, expected);
        return Choose(e.Location, [whenTrue, whenFalse], expected, mayWait,
            type => MakeConditional(e.Location, condition, FinishOption(whenTrue, type), FinishOption(whenFalse, type)));
    }

    BoundExpression MakeConditional(Location location, BoundExpression condition, BoundExpression whenTrue, BoundExpression whenFalse)
    {
        if (whenTrue.Type == WylType.Error || whenFalse.Type == WylType.Error)
            return new BoundErrorExpression(location);

        var (t, f) = (whenTrue.Type, whenFalse.Type);
        // As for operators, a constant takes the other branch's type: `c ? b : 0` is a byte.
        WylType? type = t == f ? t
            : IsConvertibleConstant(whenFalse, t) ? t
            : IsConvertibleConstant(whenTrue, f) ? f
            : Conversions.IsImplicit(t, f) ? f
            : Conversions.IsImplicit(f, t) ? t
            : t == WylType.Null && f.CanBeNullable ? f.Nullable
            : f == WylType.Null && t.CanBeNullable ? t.Nullable
            : null;
        if (type is null || type == WylType.Void || type == WylType.Null)
            return ErrorExpression(location, $"the branches of '?:' have incompatible types '{t}' and '{f}'");
        if (type.IsRefStruct)
            return ErrorExpression(location, $"'?:' cannot choose between two '{type}'s, since the result would view either; use 'if' with a variable for each");
        if (type.IsNullableReference)
            return ErrorExpression(location, "'?:' cannot choose between references; use 'if' with a variable for each");
        // A nullable non-escaping choice would refer to a function made only for the choice.
        if (type.IsNullableNonEscapingFunction)
            type = EscapingForm(type);
        return new BoundConditional(location, condition, Convert(whenTrue, type), Convert(whenFalse, type), type);
    }
}
