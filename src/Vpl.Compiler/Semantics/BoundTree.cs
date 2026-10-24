using System.Text;
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

/// <summary>
/// <see cref="Value"/> is the C# value of the same type as <see cref="BoundExpression.Type"/>: an
/// <c>int</c> for an <c>int</c>, a <c>byte</c> for a <c>byte</c>, a <see cref="Rune"/> for a
/// <c>char</c>, and so on. Build numbers with <see cref="Number"/>.
/// </summary>
public sealed record BoundLiteral(Location Location, VplType Type, object Value) : BoundExpression(Location, Type)
{
    public object Value { get; } = Validate(Type, Value);

    static object Validate(VplType type, object value)
    {
        bool matches = value switch
        {
            sbyte => type == VplType.SByte,
            byte => type == VplType.Byte,
            short => type == VplType.Short,
            ushort => type == VplType.UShort,
            int => type == VplType.Int,
            uint => type == VplType.UInt,
            long => type == VplType.Long,
            ulong => type == VplType.ULong,
            nint => type == VplType.NInt,
            nuint => type == VplType.NUInt,
            Rune => type == VplType.Char,
            float => type == VplType.Float,
            double => type == VplType.Double,
            bool => type == VplType.Bool,
            string => type == VplType.String,
            _ => false,
        };
        return matches ? value : throw new ArgumentException($"literal value {value} ({value.GetType().Name}) does not match type '{type}'");
    }

    /// <summary>The value of an integer literal.</summary>
    public Int128? Integer => Type.IsInteger ? IntegerValue(Value) : null;

    static Int128 IntegerValue(object value) => value switch
    {
        sbyte v => v,
        byte v => v,
        short v => v,
        ushort v => v,
        int v => v,
        uint v => v,
        long v => v,
        ulong v => v,
        nint v => v,
        nuint v => v,
        _ => throw new InvalidOperationException($"not an integer: {value.GetType().Name}"),
    };

    /// <summary>A literal of a numeric type, which must represent <paramref name="value"/>.</summary>
    public static BoundLiteral Number(Location location, VplType type, Int128 value)
    {
        object boxed = type == VplType.SByte ? (object)(sbyte)value
            : type == VplType.Byte ? (object)(byte)value
            : type == VplType.Short ? (object)(short)value
            : type == VplType.UShort ? (object)(ushort)value
            : type == VplType.Int ? (object)(int)value
            : type == VplType.UInt ? (object)(uint)value
            : type == VplType.Long ? (object)(long)value
            : type == VplType.ULong ? (object)(ulong)value
            : type == VplType.NInt ? (object)(nint)(long)value
            : type == VplType.NUInt ? (object)(nuint)(ulong)value
            : type == VplType.Float ? (object)(float)value
            : type == VplType.Double ? (object)(double)value
            : throw new ArgumentException($"'{type}' is not a numeric type");
        return new BoundLiteral(location, type, boxed);
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

/// <summary>
/// The argument for a field a memberwise constructor call leaves out. It is not evaluated at the
/// call: the constructor sets the field to its initializer, or to its type's default value.
/// </summary>
public sealed record BoundFieldDefault(Location Location, FieldSymbol Field, VplType Type) : BoundExpression(Location, Type);

public sealed record BoundConstruction(
    Location Location,
    VplType Type,
    MethodReference Constructor,
    IReadOnlyList<BoundExpression> Arguments) : BoundExpression(Location, Type);

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
    : BoundExpression(Location, VplType.Lambda)
{
    /// <summary>Its body, if generic inference has bound it to learn the return type.</summary>
    public InferredLambda? Inferred { get; set; }

    /// <summary>
    /// For a function reference, the lambda calling the function, which passes by <c>ref</c> the
    /// parameters its type takes by <c>ref</c>. <see cref="Syntax"/> is that lambda for no such parameters.
    /// </summary>
    public Func<IReadOnlyList<FunctionParameterType>, LambdaExpr>? Reference { get; init; }

    public LambdaExpr SyntaxFor(IReadOnlyList<FunctionParameterType> parameters) => Reference?.Invoke(parameters) ?? Syntax;
}

/// <summary>
/// A lambda bound before its return type is known, as generic inference does. <see cref="Values"/>
/// are the values its returns give, not yet converted to the return type.
/// </summary>
public sealed record InferredLambda(FunctionSymbol Function, BoundBlock Body, IReadOnlyList<BoundExpression> Values);

/// <summary>
/// A <c>?:</c> or switch expression passed as an argument, choosing between functions that include
/// lambdas, whose function type is known only once overload resolution picks a parameter for it.
/// Its conditions, patterns, and other options are bound already. <see cref="Options"/> are the
/// functions it could be, through nested choices; <see cref="Finish"/> gives it its type.
/// </summary>
public sealed record BoundPendingChoice(Location Location, IReadOnlyList<BoundExpression> Options, Func<VplType, BoundExpression> Finish)
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

/// <summary>
/// What a call, a call of a function value, a construction, or a <c>new</c> runs, and what it
/// passes. A function value's call is described by its <see cref="BoundInvocation.Signature"/>.
/// </summary>
public readonly record struct CallSite(MethodReference Method, BoundExpression? Receiver, IReadOnlyList<BoundExpression> Arguments);

/// <summary>The expressions directly inside an expression, in evaluation order.</summary>
public static class BoundTree
{
    public static CallSite? CallOf(BoundExpression expression) => expression switch
    {
        BoundCall call => new CallSite(call.Method, call.Receiver, call.Arguments),
        BoundInvocation invocation => new CallSite(invocation.Signature, null, invocation.Arguments),
        BoundNew creation => new CallSite(creation.Constructor, null, creation.Arguments),
        BoundConstruction construction => new CallSite(construction.Constructor, null, construction.Arguments),
        _ => null,
    };

    /// <summary>
    /// The local whose nullable value a place lies in, reached through narrowing, as <c>v.x</c> is in
    /// a <c>Vec3? v</c>. Assigning the local null destroys that value, so a reference to the place
    /// must not outlive the operation using it. A local that isn't <c>mut</c> keeps its value, so
    /// references into it can be kept.
    /// </summary>
    public static LocalSymbol? NullableValueRoot(BoundExpression place) => place switch
    {
        BoundLocalAccess { AssumesNonNull: true, Local: { Type.IsNullableValue: true, IsWritable: false, RefKind: RefKind.None } } => null,
        BoundLocalAccess { AssumesNonNull: true, Local.Type.IsNullableValue: true } access => access.Local,
        BoundFieldAccess { Receiver.Type.IsReference: false } field => NullableValueRoot(field.Receiver),
        BoundIndex { Target.Type.IsFixedArray: true } index => NullableValueRoot(index.Target),
        BoundCall { Method.Symbol.ReturnRefKind: not RefKind.None, Receiver: { Type.IsReference: false } receiver } => NullableValueRoot(receiver),
        _ => null,
    };

    /// <summary>
    /// The local whose nullable value a span or other <c>ref struct</c> views, as <see cref="NullableValueRoot"/>.
    /// A view of an object's or a string's contents views storage the value only refers to.
    /// </summary>
    public static LocalSymbol? ViewedNullableValue(BoundExpression view) => view switch
    {
        BoundConversion conversion => ViewedNullableValue(conversion.Operand),
        BoundCall { Receiver: { } receiver } call when call.Type.IsRefStruct =>
            receiver.Type.IsRefStruct ? ViewedNullableValue(receiver)
            : receiver.Type.IsReference || receiver.Type == VplType.String ? null
            : NullableValueRoot(receiver),
        BoundConstruction { Type.IsRefStruct: true } construction => construction.Arguments
            .Select(a => a is BoundArrayData data ? NullableValueRoot(data.Array) : a.Type.IsRefStruct ? ViewedNullableValue(a) : null)
            .FirstOrDefault(root => root is not null),
        _ => null,
    };

    /// <summary>
    /// The names an expression's patterns bind, which a statement evaluating it declares in the
    /// enclosing block. A switch expression declares its cases' names inside itself.
    /// </summary>
    public static IEnumerable<LocalSymbol> PatternLocals(BoundExpression? expression) => expression switch
    {
        null => [],
        BoundIs pattern => [.. PatternLocals(pattern.Operand), .. Patterns.Bindings(pattern.Pattern)],
        BoundSwitchExpression switchExpression => PatternLocals(switchExpression.Value),
        _ => Children(expression).SelectMany(PatternLocals),
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
    /// What a lambda uses of the code around it: the first use of each local or parameter declared
    /// outside it, in order, and the first use of <c>this</c>. Lambdas inside it count too.
    /// </summary>
    public static (IReadOnlyList<BoundLocalAccess> Locals, BoundThis? This) Captures(BoundLambda lambda)
    {
        var expressions = AllExpressions(lambda.Body).ToList();
        var seen = new HashSet<LocalSymbol>();
        var locals = expressions.OfType<BoundLocalAccess>().Where(a => IsDeclaredAround(a.Local, lambda.Function) && seen.Add(a.Local)).ToList();
        return (locals, expressions.OfType<BoundThis>().FirstOrDefault());
    }

    /// <summary>
    /// Whether a function or lambda enclosing <paramref name="lambda"/> declares <paramref name="local"/>.
    /// Compiler temporaries have no owner, since only the code that makes one uses it.
    /// </summary>
    public static bool IsDeclaredAround(LocalSymbol local, FunctionSymbol lambda)
    {
        for (var function = lambda.Enclosing; function is not null; function = function.Enclosing)
        {
            if (function == local.Owner)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The functions a function-valued <c>?:</c> or switch expression chooses from, through nested
    /// choices.
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
        BoundConstruction construction => construction.Arguments,
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
