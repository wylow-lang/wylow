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
    /// so a nullable value expects the value inside it. A switch expression's type is the expected
    /// type itself, so that a case may produce null; each case's value expects the value inside it.
    /// </summary>
    BoundExpression BindExpression(Expr expression, WylType? expected = null)
    {
        if (expression is SwitchExpr)
            return BindExpressionExpecting(expression, ChoiceTarget(expected));
        if (expression is ConditionalExpr)
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
        SuperExpr e => ErrorExpression(Catalog.SuperNotCall(e.Location)),
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
        // The parser makes these only in an argument list, which a call or an index unwraps.
        RefArgExpr or NamedArgExpr => throw new InvalidOperationException("an argument marker is bound outside an argument list"),
        LambdaExpr e => expected is FunctionType function ? BindLambda(e, function, null)
            : expected == WylType.Error ? new BoundErrorExpression(e.Location)
            : ErrorExpression(Catalog.UntypedLambda(e.Location)),
        ErrorExpr e => new BoundErrorExpression(e.Location),
        _ => throw new InvalidOperationException($"unexpected expression {expression.GetType().Name}"),
    };

    BoundErrorExpression ErrorExpression(Diagnostic diagnostic)
    {
        Error(diagnostic);
        return new BoundErrorExpression(diagnostic.Location);
    }

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
            return ErrorExpression(Catalog.UnaryOperatorNotApplicable(location, "-", type.ToString()));

        // Negation is folded into the literal so that int.MinValue and long.MinValue can be written.
        Int128 value = negated ? -(Int128)e.Value : e.Value;
        if (!type.Range!.Value.Contains(value))
        {
            return ErrorExpression(Catalog.IntegerLiteralOutOfRange(location, type.ToString(), value > 0 && value <= uint.MaxValue));
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
            return ErrorExpression(Catalog.TypeNotValue(e.Location, written));
        if (expected?.NonNull is FunctionType function)
            return BindFunctionReference(e, entry.Functions!, function);
        return ErrorExpression(Catalog.FunctionNotValue(e.Location, "function", written, written, CanReference(e)));
    }

    BoundExpression BindName(NameExpr e, WylType? expected)
    {
        if (expected?.NonNull is FunctionType function && ReferencedFunctions(e) is { } referenced)
            return BindFunctionReference(e, referenced, function);
        if (e.TypeArguments.Count > 0)
            return ErrorExpression(Catalog.TypeArgumentsOnName(e.Location));

        if (scope!.Lookup(e.Name) is { } local)
            return new BoundLocalAccess(e.Location, local, AssumesNonNull: local.IsReferenceBinding || local.IsPatternBinding);

        if (currentType is not null && LookupField(currentType.Type, e.Name) is { } field)
        {
            if (!hasThis)
                return ErrorExpression(Catalog.InstanceMemberUnavailable(e.Location, "field", e.Name, noThisContext));
            CheckVisible(e.Location, field.Field.Reach, $"field '{field.Field.Owner.Name}.{e.Name}'");
            return new BoundFieldAccess(e.Location, new BoundThis(e.Location, currentType.Type), field.Field, TypeMap.For(field.Owner).Apply(field.Field.Type));
        }

        if (currentType is not null && LookupStaticField(currentType.Type, e.Name) is { } statics)
        {
            CheckVisible(e.Location, statics.Field.Reach, $"static field '{statics.Field.Owner.Name}.{e.Name}'");
            return new BoundStaticFieldAccess(e.Location, statics.Field, TypeMap.For(statics.Owner).Apply(statics.Field.Type));
        }

        if (LookupType(e.Name, e.Location) is not null || LookupTypeParameter(e.Name) is not null)
            return ErrorExpression(Catalog.TypeNotValue(e.Location, e.Name));
        if (LookupFunctions(e.Name, e.Location) is not null || (currentType is not null && LookupMethods(currentType.Type, e.Name).Count > 0))
            return ErrorExpression(Catalog.FunctionNotValue(e.Location, "function", e.Name, e.Name, CanReference(e)));
        if (ExpectedVariant(e.Name, expected) is { } variant)
            return BindVariant(e.Location, variant.Owner, expected, variant, null);
        if (HiddenElsewhere(e.Name, e.Location) is { } hidden)
            return ErrorExpression(hidden);
        return ErrorExpression(UndefinedName(e.Location, e.Name));
    }

    BoundExpression BindThis(ThisExpr e)
    {
        if (!hasThis)
        {
            return ErrorExpression(currentType is null ? Catalog.ThisOutsideType(e.Location) : Catalog.ThisUnavailable(e.Location, noThisContext));
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
        return ErrorExpression(receiver.Type.IsNullableReference ? MayReferToNothing(receiver.Location, receiver) : Catalog.NullableReceiver(receiver.Location, TextOf(receiver.Location)));
    }

    Diagnostic MayReferToNothing(Location at, BoundExpression reference)
    {
        var type = (NullableReferenceType)reference.Type;
        return Catalog.MayReferToNothing(at, TextOf(reference.Location), $"is {FunctionSymbol.RefPrefix(type.Kind)}{type.Referenced} name");
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

    static NullableOperand NullableOperands(params BoundExpression[] operands) =>
        operands.Any(o => o.Type.IsNullableValue) ? NullableOperand.Value
        : operands.Any(o => o.Type.IsNullableReference) ? NullableOperand.Reference
        : NullableOperand.None;

    BoundExpression BindMemberAccess(MemberAccessExpr e, WylType? expected)
    {
        if (e.TypeArguments.Count > 0)
            return ErrorExpression(Catalog.TypeArgumentsOnMember(e.NameLocation));
        if (e.Target is SuperExpr)
            return ErrorExpression(Catalog.SuperField(e.NameLocation, e.Name));
        if (AsEnumName(e.Target, expected) is var (enumSymbol, enumType) && enumSymbol.FindVariant(e.Name) is { } variant)
            return BindVariant(e.Location, enumSymbol, enumType, variant, null);
        if (expected?.NonNull is FunctionType function && ReferencedFunctions(e) is { } referenced)
            return BindFunctionReference(e, referenced, function);

        if (AsTypeName(e.Target) is { } typeName)
        {
            if (typeName == WylType.Error)
                return new BoundErrorExpression(e.Location);
            if (LookupStaticField(typeName, e.Name) is { } statics)
            {
                CheckVisible(e.NameLocation, statics.Field.Reach, $"static field '{statics.Field.Owner.Name}.{e.Name}'");
                return new BoundStaticFieldAccess(e.Location, statics.Field, TypeMap.For(statics.Owner).Apply(statics.Field.Type));
            }
            return ErrorExpression(LookupMethods(typeName, e.Name).Count > 0 ? Catalog.FunctionNotValue(e.NameLocation, "method", $"{typeName.Symbol!.Name}.{e.Name}", e.Name, CanReference(e)) : Catalog.NoStaticMember(e.NameLocation, typeName.Symbol!.Name, e.Name));
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
                : ErrorExpression(Catalog.NoFixedArrayMember(nameLocation, target.Type.ToString(), name));
        }
        if (LookupStaticField(target.Type, name) is { } statics)
            return ErrorExpression(Catalog.StaticFieldThroughValue(nameLocation, name, statics.Field.Owner.Name));
        if (!target.Type.IsStruct && !target.Type.IsClass)
        {
            return ErrorExpression(LookupMethods(target.Type, name).Count > 0 ? Catalog.MethodNotField(nameLocation, $"{DisplayName(target.Type)}.{name}", name) : target.Type.IsEnum && target.Type.Symbol!.ValueFields.Any(f => f.Name == name) ? Catalog.VariantFieldAccess(nameLocation, name) : Catalog.NoMember(nameLocation, target.Type.ToString(), name));
        }
        if (LookupField(target.Type, name) is not var (field, owner))
        {
            return ErrorExpression(LookupMethods(target.Type, name).Count > 0 ? Catalog.MethodNotField(nameLocation, $"{DisplayName(target.Type)}.{name}", name) : Catalog.NoField(nameLocation, DisplayName(target.Type), name));
        }
        CheckVisible(nameLocation, field.Reach, $"field '{field.Owner.Name}.{name}'");
        return new BoundFieldAccess(location, target, field, TypeMap.For(owner).Apply(field.Type));
    }

    bool HasMembers(WylType type) => type.IsStructOrEnum || type.IsReference || type.IsTypeParameter || stdlib.MemberHost(type) != type;

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
            return ErrorExpression(Catalog.UnaryOperatorNotApplicable(e.Location, SyntaxFacts.GetText(e.Operator), operand.Type.ToString(), NullableOperands(operand)));
        return new BoundUnary(e.Location, op, operand);
    }

    BoundExpression BindNonNull(NonNullExpr e)
    {
        var operand = BindExpression(e.Operand);
        if (operand.Type == WylType.Error)
            return new BoundErrorExpression(e.Location);
        if (!operand.Type.MayBeNull)
            return ErrorExpression(Catalog.NonNullOnNonNullable(e.Location, operand.Type.ToString()));
        return new BoundNonNull(e.Location, operand);
    }

    BoundExpression BindCoalesce(CoalesceExpr e)
    {
        var left = BindExpression(e.Left);
        var right = BindExpression(e.Right);
        if (left.Type == WylType.Error || right.Type == WylType.Error)
            return new BoundErrorExpression(e.Location);
        if (left.Type.IsNullableReference)
            return ErrorExpression(Catalog.CoalesceOnReference(e.OperatorLocation, left.Type.ToString()));
        if (!left.Type.IsNullable)
            return ErrorExpression(Catalog.CoalesceOnNonNullable(e.Left.Location, left.Type.ToString()));
        // Its result would refer to a function that may be made only for this operation.
        if (left.Type.IsNullableNonEscapingFunction)
            return ErrorExpression(Catalog.CoalesceNonEscaping(e.OperatorLocation));

        // Prefer a non-null result: `a ?? b` is non-null when `b` is.
        WylType[] candidates = [left.Type.NonNull, right.Type, left.Type];
        var type = candidates.FirstOrDefault(t =>
            t != WylType.Null && Conversions.IsImplicit(left.Type.NonNull, t, currentFunction) && (Conversions.IsImplicit(right.Type, t, currentFunction) || IsConvertibleConstant(right, t)));
        if (type is null)
            return ErrorExpression(Catalog.BinaryOperatorNotApplicable(e.OperatorLocation, "??", left.Type.ToString(), right.Type.ToString()));
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
            return ErrorExpression(Catalog.TryOnNonResult(e.Operand.Location, operand.Type.ToString()));
        if (inferring is { } lambda && lambda.Function == currentFunction)
            return ErrorExpression(Catalog.TryInInferredLambda(e.Location));
        var function = currentFunction;
        if (function is null || function.IsConstructor || function.IsDestructor || !IsResult(function.ReturnType))
            return ErrorExpression(Catalog.TryOutsideResult(e.Location));
        var (error, expectedError) = (operand.Type.TypeArguments[1], function.ReturnType.TypeArguments[1]);
        if (error != expectedError)
        {
            return ErrorExpression(Catalog.TryErrorType(e.Location, Describe(function), expectedError.ToString(), error.ToString()));
        }
        return new BoundTry(e.Location, operand, operand.Type.TypeArguments[0]);
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
            : Conversions.IsImplicit(t, f, currentFunction) ? f
            : Conversions.IsImplicit(f, t, currentFunction) ? t
            : t == WylType.Null && f.CanBeNullable ? f.Nullable
            : f == WylType.Null && t.CanBeNullable ? t.Nullable
            : null;
        if (type is null || type == WylType.Void || type == WylType.Null)
            return ErrorExpression(Catalog.ConditionalTypes(location, t.ToString(), f.ToString()));
        if (type.IsRefStruct)
            return ErrorExpression(Catalog.ConditionalView(location, type.ToString()));
        if (type.IsNullableReference)
            return ErrorExpression(Catalog.ConditionalReference(location));
        // A nullable non-escaping choice would refer to a function made only for the choice.
        if (type.IsNullableNonEscapingFunction)
            type = EscapingForm(type);
        return new BoundConditional(location, condition, Convert(whenTrue, type), Convert(whenFalse, type), type);
    }
}
