using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

/// <summary><see cref="Functions"/> holds free functions, methods, and constructors.</summary>
public sealed record BoundProgram(
    SourceText Source,
    IReadOnlyList<TypeSymbol> Types,
    IReadOnlyList<BoundFunction> Functions,
    FunctionSymbol? Main,
    StdlibSymbols Stdlib);

public sealed record BoundFunction(FunctionSymbol Symbol, BoundBlock Body);

public abstract record BoundNode(Location Location);

public abstract record BoundStatement(Location Location) : BoundNode(Location);

public sealed record BoundBlock(Location Location, IReadOnlyList<BoundStatement> Statements) : BoundStatement(Location);

public sealed record BoundVariableDeclaration(Location Location, LocalSymbol Local, BoundExpression? Initializer)
    : BoundStatement(Location);

/// <summary><see cref="Target"/> is a local, or a field path rooted at a local, <c>this</c>, or an object.</summary>
public sealed record BoundAssignment(Location Location, BoundExpression Target, BoundExpression Value) : BoundStatement(Location);

/// <summary><see cref="IsDiscard"/> for <c>_ = value;</c>, which may discard any value.</summary>
public sealed record BoundExpressionStatement(Location Location, BoundExpression Expression, bool IsDiscard = false) : BoundStatement(Location);

public sealed record BoundIf(Location Location, BoundExpression Condition, BoundStatement Then, BoundStatement? Else)
    : BoundStatement(Location);

public sealed record BoundWhile(Location Location, BoundExpression Condition, BoundStatement Body) : BoundStatement(Location);

public sealed record BoundFor(
    Location Location,
    BoundStatement? Initializer,
    BoundExpression? Condition,
    BoundStatement? Update,
    BoundStatement Body) : BoundStatement(Location);

public sealed record BoundReturn(Location Location, BoundExpression? Value) : BoundStatement(Location);

/// <summary>
/// A loop over the integers from <see cref="Start"/> to <see cref="End"/>, which are evaluated once, in
/// that order. <see cref="Local"/> holds a copy of the counter in each iteration.
/// </summary>
public sealed record BoundForRange(
    Location Location,
    LocalSymbol Local,
    BoundExpression Start,
    BoundExpression End,
    bool IsInclusive,
    BoundStatement Body) : BoundStatement(Location);

public sealed record BoundBreak(Location Location) : BoundStatement(Location);

public sealed record BoundContinue(Location Location) : BoundStatement(Location);

/// <summary>A case of a switch; <see cref="Patterns"/> is empty for <c>default</c>. Each pattern is tried in turn.</summary>
public sealed record BoundSwitchCase<T>(Location Location, IReadOnlyList<BoundPattern> Patterns, T Body);

/// <summary>A switch statement. Its cases cover every value, which the binder has checked.</summary>
public sealed record BoundSwitch(Location Location, BoundExpression Value, IReadOnlyList<BoundSwitchCase<BoundStatement>> Cases)
    : BoundStatement(Location);

/// <summary>
/// A class constructor's call to its base class constructor, always its first statement. Implicit
/// when the source has no <c>super(...)</c>.
/// </summary>
public sealed record BoundSuperCall(Location Location, MethodReference Constructor, IReadOnlyList<BoundExpression> Arguments)
    : BoundStatement(Location);

public abstract record BoundExpression(Location Location, VplType Type) : BoundNode(Location);

/// <summary><see cref="Value"/> is an int, long, float, double, bool or string matching <see cref="BoundExpression.Type"/>.</summary>
public sealed record BoundLiteral(Location Location, VplType Type, object Value) : BoundExpression(Location, Type)
{
    public object Value { get; } = Validate(Type, Value);

    static object Validate(VplType type, object value)
    {
        bool matches = value switch
        {
            int => type == VplType.Int,
            long => type == VplType.Long,
            float => type == VplType.Float,
            double => type == VplType.Double,
            bool => type == VplType.Bool,
            string => type == VplType.String,
            _ => false,
        };
        return matches ? value : throw new ArgumentException($"literal value {value} ({value.GetType().Name}) does not match type '{type}'");
    }
}

public sealed record BoundNullLiteral(Location Location) : BoundExpression(Location, VplType.Null);

/// <summary>
/// With <see cref="AssumesNonNull"/>, a nullable local used as its non-null type; flow analysis
/// checks that it has been narrowed by a null check.
/// </summary>
public sealed record BoundLocalAccess(Location Location, LocalSymbol Local, bool AssumesNonNull = false)
    : BoundExpression(Location, AssumesNonNull ? Local.Type.NonNull : Local.Type);

public enum BoundUnaryOperator { Negate, LogicalNot, BitwiseNot }

public sealed record BoundUnary(Location Location, BoundUnaryOperator Operator, BoundExpression Operand)
    : BoundExpression(Location, Operand.Type);

public enum BoundBinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Remainder,
    BitwiseAnd,
    BitwiseOr,
    BitwiseXor,
    ShiftLeft,
    ShiftRight,
    LogicalAnd,
    LogicalOr,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

/// <summary>Both operands have the same type, except for shifts and reference comparisons.</summary>
public sealed record BoundBinary(Location Location, BoundExpression Left, BoundBinaryOperator Operator, BoundExpression Right, VplType Type)
    : BoundExpression(Location, Type);

public sealed record BoundThis(Location Location, VplType Type) : BoundExpression(Location, Type);

/// <summary><see cref="BoundExpression.Type"/> is the field's type with the receiver's type arguments applied.</summary>
public sealed record BoundFieldAccess(Location Location, BoundExpression Receiver, FieldSymbol Field, VplType Type)
    : BoundExpression(Location, Type);

/// <summary>An element of a fixed array, which is bounds-checked, or of a pointer, which is not.</summary>
public sealed record BoundIndex(Location Location, BoundExpression Target, BoundExpression Index)
    : BoundExpression(Location, Target.Type.ElementType!);

/// <summary>The length of a fixed array, after evaluating it.</summary>
public sealed record BoundArrayLength(Location Location, BoundExpression Operand) : BoundExpression(Location, VplType.Int);

public sealed record BoundArrayLiteral(Location Location, VplType Type, IReadOnlyList<BoundExpression> Elements) : BoundExpression(Location, Type);

/// <summary>A pointer to the first element of a fixed array, which a span then views.</summary>
public sealed record BoundArrayData(Location Location, BoundExpression Array)
    : BoundExpression(Location, Array.Type.ElementType!.PointerTo);

/// <summary>
/// <see cref="Receiver"/> is set for instance methods. A <c>ref</c> argument is the place itself.
/// <see cref="IsSuperCall"/> calls the base class's implementation without dispatch.
/// </summary>
public sealed record BoundCall(
    Location Location,
    MethodReference Method,
    BoundExpression? Receiver,
    IReadOnlyList<BoundExpression> Arguments,
    bool IsSuperCall = false) : BoundExpression(Location, Method.ReturnType);

/// <summary>Allocates a class object.</summary>
public sealed record BoundNew(Location Location, VplType Type, MethodReference Constructor, IReadOnlyList<BoundExpression> Arguments)
    : BoundExpression(Location, Type);

/// <summary><c>operand!</c>: panics if the operand is null.</summary>
public sealed record BoundNonNull(Location Location, BoundExpression Operand) : BoundExpression(Location, Operand.Type.NonNull);

/// <summary><c>left ?? right</c>; <see cref="Right"/> is evaluated only when <see cref="Left"/> is null.</summary>
public sealed record BoundCoalesce(Location Location, BoundExpression Left, BoundExpression Right, VplType Type)
    : BoundExpression(Location, Type);

/// <summary>
/// <c>receiver?.member</c>: evaluates the receiver into <see cref="Value"/>, then, if it is not null,
/// <see cref="WhenNotNull"/>, which accesses the member through <see cref="Value"/>.
/// </summary>
public sealed record BoundConditionalAccess(
    Location Location,
    BoundExpression Receiver,
    LocalSymbol Value,
    BoundExpression WhenNotNull,
    VplType Type) : BoundExpression(Location, Type);

/// <summary><c>operand is pattern</c>, which on success assigns the pattern's bindings.</summary>
public sealed record BoundIs(Location Location, BoundExpression Operand, BoundPattern Pattern) : BoundExpression(Location, VplType.Bool);

/// <summary>
/// A switch expression. A case whose value cannot complete, such as a call to <c>panic</c>, has type
/// <c>void</c>; every other case's value has <see cref="BoundExpression.Type"/>.
/// </summary>
public sealed record BoundSwitchExpression(
    Location Location,
    BoundExpression Value,
    IReadOnlyList<BoundSwitchCase<BoundExpression>> Cases,
    VplType Type) : BoundExpression(Location, Type);

/// <summary>Creates an enum value; <see cref="Arguments"/> are the variant's fields.</summary>
public sealed record BoundVariant(Location Location, VplType Type, VariantSymbol Variant, IReadOnlyList<BoundExpression> Arguments)
    : BoundExpression(Location, Type);

/// <summary>
/// <c>try operand</c>: the value of an <c>Ok</c> result, or for an <c>Err</c>, a return of the error
/// from the enclosing function. Only the whole value of a statement can be a <c>try</c>.
/// </summary>
public sealed record BoundTry(Location Location, BoundExpression Operand, VplType Type) : BoundExpression(Location, Type);

public sealed record BoundFieldInitializer(FieldSymbol Field, BoundExpression Value);

public sealed record BoundConstruction(
    Location Location,
    VplType Type,
    MethodReference Constructor,
    IReadOnlyList<BoundExpression> Arguments,
    IReadOnlyList<BoundFieldInitializer> Initializers) : BoundExpression(Location, Type);

public sealed record BoundConversion(Location Location, VplType Type, BoundExpression Operand) : BoundExpression(Location, Type);

public sealed record BoundConditional(
    Location Location,
    BoundExpression Condition,
    BoundExpression WhenTrue,
    BoundExpression WhenFalse,
    VplType Type) : BoundExpression(Location, Type);

public sealed record BoundErrorExpression(Location Location) : BoundExpression(Location, VplType.Error);

/// <summary>A lambda with its function type. Creating it evaluates nothing; its body runs when the function is called.</summary>
public sealed record BoundLambda(Location Location, VplType Type, FunctionSymbol Function, BoundBlock Body) : BoundExpression(Location, Type);

/// <summary>
/// A lambda argument whose function type is known only once overload resolution picks a parameter
/// for it. <see cref="DeclaredParameters"/> are its parameter types, if it declares them.
/// </summary>
public sealed record BoundPendingLambda(Location Location, LambdaExpr Syntax, IReadOnlyList<FunctionParameterType>? DeclaredParameters)
    : BoundExpression(Location, VplType.Lambda);

/// <summary>
/// A call of a function value. <see cref="Signature"/> describes the function type's parameters
/// as a method's, so the checks of 'ref' arguments apply as for calls.
/// </summary>
public sealed record BoundInvocation(Location Location, BoundExpression Callee, MethodReference Signature, IReadOnlyList<BoundExpression> Arguments)
    : BoundExpression(Location, Signature.ReturnType);

public abstract record BoundPattern(Location Location, VplType Type);

/// <summary><c>_</c>, or a variant's fields when only the variant is named.</summary>
public sealed record BoundDiscardPattern(Location Location, VplType Type) : BoundPattern(Location, Type);

/// <summary>Matches anything and assigns it to <see cref="Local"/>, whose type the value converts to.</summary>
public sealed record BoundBindingPattern(Location Location, VplType Type, LocalSymbol Local) : BoundPattern(Location, Type);

/// <summary>Tests that a reference is non-null and refers to a <see cref="Target"/>, optionally binding it.</summary>
public sealed record BoundTypePattern(Location Location, VplType Type, VplType Target, LocalSymbol? Binding)
    : BoundPattern(Location, Type);

/// <summary><see cref="Fields"/> has one pattern per field of the variant.</summary>
public sealed record BoundVariantPattern(Location Location, VplType Type, VariantSymbol Variant, IReadOnlyList<BoundPattern> Fields)
    : BoundPattern(Location, Type);

/// <summary>Compares with a literal of the matched type.</summary>
public sealed record BoundConstantPattern(Location Location, BoundLiteral Value) : BoundPattern(Location, Value.Type);

public static class Patterns
{
    public static IEnumerable<LocalSymbol> Bindings(BoundPattern pattern) => pattern switch
    {
        BoundBindingPattern binding => [binding.Local],
        BoundTypePattern { Binding: { } binding } => [binding],
        BoundVariantPattern variant => variant.Fields.SelectMany(Bindings),
        _ => [],
    };
}

/// <summary>The expressions directly inside an expression, in evaluation order.</summary>
public static class BoundTree
{
    /// <summary>
    /// The local whose nullable value a place lies in, reached through narrowing, as <c>v.x</c> is in
    /// a <c>Vec3? v</c>. Assigning the local null destroys that value, so a reference to the place
    /// must not outlive the operation using it.
    /// </summary>
    public static LocalSymbol? NullableValueRoot(BoundExpression place) => place switch
    {
        BoundLocalAccess { AssumesNonNull: true, Local.Type.IsNullableValue: true } access => access.Local,
        BoundFieldAccess { Receiver.Type.IsReference: false } field => NullableValueRoot(field.Receiver),
        BoundIndex { Target.Type.IsFixedArray: true } index => NullableValueRoot(index.Target),
        BoundCall { Method.Symbol.ReturnRefKind: not RefKind.None, Receiver: { Type.IsReference: false } receiver } => NullableValueRoot(receiver),
        _ => null,
    };

    /// <summary>The local whose nullable value a span or other <c>ref struct</c> views, as <see cref="NullableValueRoot"/>.</summary>
    public static LocalSymbol? ViewedNullableValue(BoundExpression view) => view switch
    {
        BoundConversion conversion => ViewedNullableValue(conversion.Operand),
        BoundCall { Receiver: { } receiver } call when call.Type.IsRefStruct =>
            receiver.Type.IsRefStruct ? ViewedNullableValue(receiver) : receiver.Type.IsReference ? null : NullableValueRoot(receiver),
        BoundConstruction { Type.IsRefStruct: true } construction => construction.Arguments
            .Select(a => a is BoundArrayData data ? NullableValueRoot(data.Array) : a.Type.IsRefStruct ? ViewedNullableValue(a) : null)
            .FirstOrDefault(root => root is not null),
        _ => null,
    };

    /// <summary>The statements directly inside a statement.</summary>
    public static IEnumerable<BoundStatement> InnerStatements(BoundStatement statement) => statement switch
    {
        BoundBlock block => block.Statements,
        BoundIf ifStatement => ifStatement.Else is null ? [ifStatement.Then] : [ifStatement.Then, ifStatement.Else],
        BoundWhile whileStatement => [whileStatement.Body],
        BoundFor forStatement => [.. new[] { forStatement.Initializer, forStatement.Update }.OfType<BoundStatement>(), forStatement.Body],
        BoundForRange range => [range.Body],
        BoundSwitch switchStatement => switchStatement.Cases.Select(c => c.Body),
        _ => [],
    };

    /// <summary>The expressions a statement evaluates itself, not those of the statements inside it.</summary>
    public static IEnumerable<BoundExpression> OwnExpressions(BoundStatement statement) => statement switch
    {
        BoundVariableDeclaration { Initializer: { } initializer } => [initializer],
        BoundAssignment assignment => [assignment.Target, assignment.Value],
        BoundExpressionStatement expressionStatement => [expressionStatement.Expression],
        BoundSuperCall super => super.Arguments,
        BoundIf ifStatement => [ifStatement.Condition],
        BoundWhile whileStatement => [whileStatement.Condition],
        BoundFor { Condition: { } condition } => [condition],
        BoundForRange range => [range.Start, range.End],
        BoundReturn { Value: { } value } => [value],
        BoundSwitch switchStatement => [switchStatement.Value],
        _ => [],
    };

    /// <summary>Every expression in a statement, including in the statements and lambda bodies inside it.</summary>
    public static IEnumerable<BoundExpression> AllExpressions(BoundStatement statement) =>
        OwnExpressions(statement).SelectMany(AllExpressions).Concat(InnerStatements(statement).SelectMany(AllExpressions));

    public static IEnumerable<BoundExpression> AllExpressions(BoundExpression expression) =>
        [expression, .. expression is BoundLambda lambda ? AllExpressions(lambda.Body) : Children(expression).SelectMany(AllExpressions)];

    /// <summary>
    /// The functions a function-valued <c>?:</c> or switch expression chooses from: lambdas and
    /// function-typed locals, neither of which has effects to make or copy.
    /// </summary>
    public static IEnumerable<BoundExpression> FunctionOptions(BoundExpression expression) => expression switch
    {
        BoundConditional conditional => [.. FunctionOptions(conditional.WhenTrue), .. FunctionOptions(conditional.WhenFalse)],
        BoundSwitchExpression switchExpression => switchExpression.Cases.Select(c => c.Body).Where(b => b.Type.IsFunction).SelectMany(FunctionOptions),
        _ => [expression],
    };

    public static IEnumerable<BoundExpression> Children(BoundExpression expression) => expression switch
    {
        BoundUnary unary => [unary.Operand],
        BoundBinary binary => [binary.Left, binary.Right],
        BoundCall call => call.Receiver is null ? call.Arguments : [call.Receiver, .. call.Arguments],
        BoundInvocation invocation => [invocation.Callee, .. invocation.Arguments],
        BoundConstruction construction => [.. construction.Arguments, .. construction.Initializers.Select(i => i.Value)],
        BoundNew creation => creation.Arguments,
        BoundConversion conversion => [conversion.Operand],
        BoundConditional conditional => [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse],
        BoundFieldAccess field => [field.Receiver],
        BoundIndex index => [index.Target, index.Index],
        BoundArrayLength length => [length.Operand],
        BoundArrayData data => [data.Array],
        BoundArrayLiteral literal => literal.Elements,
        BoundNonNull nonNull => [nonNull.Operand],
        BoundCoalesce coalesce => [coalesce.Left, coalesce.Right],
        BoundConditionalAccess access => [access.Receiver, access.WhenNotNull],
        BoundIs pattern => [pattern.Operand],
        BoundSwitchExpression switchExpression => [switchExpression.Value, .. switchExpression.Cases.Select(c => c.Body)],
        BoundVariant variant => variant.Arguments,
        BoundTry attempt => [attempt.Operand],
        _ => [],
    };
}
