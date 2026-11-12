using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    bool CanConvert(BoundExpression expression, WylType to) =>
        expression is BoundPendingLambda lambda ? (lambda.Inferred is { } inferred ? InferredLambdaFits(inferred, to) : LambdaFits(lambda, to))
        : expression is BoundPendingChoice choice ? to.NonNull.IsFunction && choice.Options.All(o => CanConvert(o, to))
        : Conversions.IsImplicit(expression.Type, to, currentFunction) || IsConvertibleConstant(expression, to) || IsNarrowable(expression, to)
        || IsWeakConversion(expression.Type, to) || SpanConversion(expression, to) is not null || IsNullableWrapping(expression, to)
        || IsPassedByIn(expression, to) || IsBoxing(expression, to);

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
    /// A sendable value converts to a box of any type it converts to: it converts to the box's
    /// content type first, so the box holds a value of that type.
    /// </summary>
    bool IsBoxing(BoundExpression expression, WylType to) =>
        to.Symbol is { IsIsolated: true } && to.TypeArguments is [var content]
        && expression.Type.IsSendableIn(currentFunction) && CanConvert(expression, content);

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
    bool IsNarrowable(BoundExpression expression, WylType to) =>
        expression is BoundLocalAccess { AssumesNonNull: false, Type: var type }
        && (type.IsNullableReference ? !to.IsNullableReference : type.IsNullable && !to.IsNullable)
        && Conversions.IsImplicit(type.Narrowed, to, currentFunction);

    BoundExpression Convert(BoundExpression expression, WylType to)
    {
        if (expression.Type == to || expression.Type == WylType.Error || to == WylType.Error)
            return expression;

        if (expression is BoundPendingLambda lambda)
        {
            if (to.NonNull is not FunctionType function)
                return ErrorExpression(Catalog.FunctionToNonFunction(expression.Location, lambda.Syntax.IsFunctionReference, to.ToString()));
            if (function.IsExtern)
            {
                return lambda.Referenced is var (reference, overloads)
                    ? Convert(BindCallback(reference, overloads, function), to)
                    : ErrorExpression(Catalog.LambdaToExternFunction(expression.Location, function.ToString()));
            }
            var bound = lambda.Inferred is { } inferred
                ? RetargetLambda(lambda, inferred, function)
                : BindLambda(lambda.SyntaxFor(function.Parameters), function, lambda.DeclaredParameters);
            return Convert(bound, to);
        }
        if (expression is BoundPendingChoice choice)
        {
            return to.NonNull.IsFunction
                ? Convert(choice.Finish(ChoiceTarget(to)!.NonNull), to)
                : ErrorExpression(Catalog.ChoiceToNonFunction(expression.Location, to.ToString()));
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
        if (IsBoxing(expression, to))
            return new BoundConversion(expression.Location, to, Convert(expression, to.TypeArguments[0]));
        if (Conversions.IsImplicit(expression.Type, to, currentFunction) || IsWeakConversion(expression.Type, to))
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
        if (to.Symbol is { IsIsolated: true } && !expression.Type.IsSendableIn(currentFunction))
        {
            var field = expression.Type.FindNonSendableField(currentFunction);
            return ErrorExpression(Catalog.IsolatedNotSendable(expression.Location, expression.Type.ToString(),
                field?.Name, field?.Declaration.NameLocation ?? expression.Location,
                field is null ? null : TypeMap.For(expression.Type).Apply(field.Type).ToString()));
        }
        var (help, nullable) = Conversions.IsExplicit(expression.Type, to, currentFunction) ? (ConversionHelp.Cast, null)
            : expression.Type.MayBeNull && Conversions.IsImplicit(expression.Type.Narrowed, to, currentFunction) ? (ConversionHelp.NullCheck, null)
            : SpanConversionHelp(expression, to);
        return ErrorExpression(Catalog.ImplicitConversion(expression.Location, to.ToString(), expression.Type.ToString(), help, nullable));
    }

    BoundExpression BindCast(CastExpr e)
    {
        var operand = BindExpression(e.Operand);
        var type = BindType(e.Type, allowVoid: false);
        if (operand.Type == WylType.Error || type == WylType.Error)
            return new BoundErrorExpression(e.Location);
        if (operand.Type == type)
            return operand;
        if (operand.Type.IsPointer && type.IsPointer && !type.IsVoidPointer)
            return InUnsafe ? new BoundConversion(e.Location, type, operand) : ErrorExpression(Catalog.PointerCast(e.Location));
        if (!Conversions.IsExplicit(operand.Type, type, currentFunction))
            return ErrorExpression(Catalog.InvalidCast(e.Location, operand.Type.ToString(), type.ToString()));
        return new BoundConversion(e.Location, type, operand);
    }
}
