using System.Text;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

/// <summary><see cref="Functions"/> holds free functions, methods, and constructors.</summary>
/// <summary>
/// A bound package with the standard library: <see cref="Sources"/> are the package's own files,
/// <see cref="Functions"/> every function body, and <see cref="FreeFunctions"/> every top-level
/// function declared, extern ones with no body among them.
/// </summary>
public sealed record BoundProgram(
    Package Package,
    IReadOnlyList<SourceText> Sources,
    IReadOnlyList<TypeSymbol> Types,
    IReadOnlyList<BoundFunction> Functions,
    IReadOnlyList<FunctionSymbol> FreeFunctions,
    FunctionSymbol? Main,
    StdlibSymbols Stdlib)
{
    IReadOnlyList<TypeSymbol>? borrowableClasses;

    /// <summary>Whether an object of exactly the type is a <c>Borrowable</c>.</summary>
    public bool IsBorrowable(WylType type) => Stdlib.Borrowable is { } borrowable && Conversions.IsSubtype(type, borrowable.Type);

    /// <summary>Whether an object of the type may be a <c>Borrowable</c>, which some class in the program it could be implements.</summary>
    public bool MayBeBorrowable(WylType type)
    {
        if (type is TypeParameterType parameter)
            return parameter.Parameter.Bounds.Any(MayBeBorrowable);
        borrowableClasses ??= [.. Types.Where(s => s.Kind == TypeKind.Class && IsBorrowable(s.Type))];
        return borrowableClasses.Any(c => c == type.NonNull.Symbol || Conversions.Supertypes(c.Type).Any(t => t.Symbol == type.NonNull.Symbol));
    }
}

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

public abstract record BoundExpression(Location Location, WylType Type) : BoundNode(Location);

/// <summary>
/// <see cref="Value"/> is the C# value of the same type as <see cref="BoundExpression.Type"/>: an
/// <c>int</c> for an <c>int</c>, a <c>byte</c> for a <c>byte</c>, a <see cref="Rune"/> for a
/// <c>char</c>, and so on. Build numbers with <see cref="Number"/>.
/// </summary>
public sealed record BoundLiteral(Location Location, WylType Type, object Value) : BoundExpression(Location, Type)
{
    public object Value { get; } = Validate(Type, Value);

    static object Validate(WylType type, object value)
    {
        bool matches = value switch
        {
            sbyte => type == WylType.SByte,
            byte => type == WylType.Byte,
            short => type == WylType.Short,
            ushort => type == WylType.UShort,
            int => type == WylType.Int,
            uint => type == WylType.UInt,
            long => type == WylType.Long,
            ulong => type == WylType.ULong,
            nint => type == WylType.NInt,
            nuint => type == WylType.NUInt,
            Rune => type == WylType.Char,
            float => type == WylType.Float,
            double => type == WylType.Double,
            bool => type == WylType.Bool,
            string => type == WylType.String,
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
    public static BoundLiteral Number(Location location, WylType type, Int128 value)
    {
        object boxed = type == WylType.SByte ? (object)(sbyte)value
            : type == WylType.Byte ? (object)(byte)value
            : type == WylType.Short ? (object)(short)value
            : type == WylType.UShort ? (object)(ushort)value
            : type == WylType.Int ? (object)(int)value
            : type == WylType.UInt ? (object)(uint)value
            : type == WylType.Long ? (object)(long)value
            : type == WylType.ULong ? (object)(ulong)value
            : type == WylType.NInt ? (object)(nint)(long)value
            : type == WylType.NUInt ? (object)(nuint)(ulong)value
            : type == WylType.Float ? (object)(float)value
            : type == WylType.Double ? (object)(double)value
            : throw new ArgumentException($"'{type}' is not a numeric type");
        return new BoundLiteral(location, type, boxed);
    }
}

public sealed record BoundNullLiteral(Location Location) : BoundExpression(Location, WylType.Null);

/// <summary>
/// With <see cref="AssumesNonNull"/>, a nullable local used as its non-null type, or a <c>ref?</c>
/// local as the place it refers to; flow analysis checks that it has been narrowed by a null check.
/// </summary>
public sealed record BoundLocalAccess(Location Location, LocalSymbol Local, bool AssumesNonNull = false)
    : BoundExpression(Location, AssumesNonNull ? Local.Type.Narrowed : Local.Type);

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
public sealed record BoundBinary(Location Location, BoundExpression Left, BoundBinaryOperator Operator, BoundExpression Right, WylType Type)
    : BoundExpression(Location, Type);

public sealed record BoundThis(Location Location, WylType Type) : BoundExpression(Location, Type);

/// <summary><see cref="BoundExpression.Type"/> is the field's type with the receiver's type arguments applied.</summary>
public sealed record BoundFieldAccess(Location Location, BoundExpression Receiver, FieldSymbol Field, WylType Type)
    : BoundExpression(Location, Type);

/// <summary>An element of a fixed array, which is bounds-checked, or of a pointer, which is not.</summary>
public sealed record BoundIndex(Location Location, BoundExpression Target, BoundExpression Index)
    : BoundExpression(Location, ((IndexedType)Target.Type).Element);

/// <summary>The length of a fixed array, after evaluating it.</summary>
public sealed record BoundArrayLength(Location Location, BoundExpression Operand) : BoundExpression(Location, WylType.Int);

public sealed record BoundArrayLiteral(Location Location, WylType Type, IReadOnlyList<BoundExpression> Elements) : BoundExpression(Location, Type);

/// <summary>A pointer to the first element of a fixed array, which a span then views.</summary>
public sealed record BoundArrayData(Location Location, BoundExpression Array)
    : BoundExpression(Location, ((FixedArrayType)Array.Type).Element.PointerTo);

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
public sealed record BoundNew(Location Location, WylType Type, MethodReference Constructor, IReadOnlyList<BoundExpression> Arguments)
    : BoundExpression(Location, Type);

/// <summary><c>operand!</c>: panics if the operand is null. Of a <c>ref?</c>, it is the place referred to.</summary>
public sealed record BoundNonNull(Location Location, BoundExpression Operand) : BoundExpression(Location, Operand.Type.Narrowed);

/// <summary><c>left ?? right</c>; <see cref="Right"/> is evaluated only when <see cref="Left"/> is null.</summary>
public sealed record BoundCoalesce(Location Location, BoundExpression Left, BoundExpression Right, WylType Type)
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
    WylType Type) : BoundExpression(Location, Type);

/// <summary><c>operand is pattern</c>, which on success assigns the pattern's bindings.</summary>
public sealed record BoundIs(Location Location, BoundExpression Operand, BoundPattern Pattern) : BoundExpression(Location, WylType.Bool);

/// <summary>
/// A switch expression. A case whose value cannot complete, such as a call to <c>panic</c>, has type
/// <c>void</c>; every other case's value has <see cref="BoundExpression.Type"/>.
/// </summary>
public sealed record BoundSwitchExpression(
    Location Location,
    BoundExpression Value,
    IReadOnlyList<BoundSwitchCase<BoundExpression>> Cases,
    WylType Type) : BoundExpression(Location, Type);

/// <summary>Creates an enum value; <see cref="Arguments"/> are the variant's fields.</summary>
public sealed record BoundVariant(Location Location, WylType Type, VariantSymbol Variant, IReadOnlyList<BoundExpression> Arguments)
    : BoundExpression(Location, Type);

/// <summary>
/// <c>try operand</c>: the value of an <c>Ok</c> result, or for an <c>Err</c>, a return of the error
/// from the enclosing function. Only the whole value of a statement can be a <c>try</c>.
/// </summary>
public sealed record BoundTry(Location Location, BoundExpression Operand, WylType Type) : BoundExpression(Location, Type);

/// <summary>
/// The argument for a field a memberwise constructor call leaves out. It is not evaluated at the
/// call: the constructor sets the field to its initializer, or to its type's default value.
/// </summary>
public sealed record BoundFieldDefault(Location Location, FieldSymbol Field, WylType Type) : BoundExpression(Location, Type);

public sealed record BoundConstruction(
    Location Location,
    WylType Type,
    MethodReference Constructor,
    IReadOnlyList<BoundExpression> Arguments) : BoundExpression(Location, Type);

public sealed record BoundConversion(Location Location, WylType Type, BoundExpression Operand) : BoundExpression(Location, Type);

public sealed record BoundConditional(
    Location Location,
    BoundExpression Condition,
    BoundExpression WhenTrue,
    BoundExpression WhenFalse,
    WylType Type) : BoundExpression(Location, Type);

public sealed record BoundErrorExpression(Location Location) : BoundExpression(Location, WylType.Error);

/// <summary>A lambda with its function type. Creating it evaluates nothing; its body runs when the function is called.</summary>
public sealed record BoundLambda(Location Location, WylType Type, FunctionSymbol Function, BoundBlock Body) : BoundExpression(Location, Type);

/// <summary>
/// A lambda argument whose function type is known only once overload resolution picks a parameter
/// for it. <see cref="DeclaredParameters"/> are its parameter types, if it declares them.
/// </summary>
public sealed record BoundPendingLambda(Location Location, LambdaExpr Syntax, IReadOnlyList<FunctionParameterType>? DeclaredParameters)
    : BoundExpression(Location, WylType.Lambda)
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
public sealed record BoundPendingChoice(Location Location, IReadOnlyList<BoundExpression> Options, Func<WylType, BoundExpression> Finish)
    : BoundExpression(Location, WylType.Lambda);

/// <summary>
/// A call of a function value. <see cref="Signature"/> describes the function type's parameters
/// as a method's, so the checks of 'ref' arguments apply as for calls.
/// </summary>
public sealed record BoundInvocation(Location Location, BoundExpression Callee, MethodReference Signature, IReadOnlyList<BoundExpression> Arguments)
    : BoundExpression(Location, Signature.ReturnType);

public abstract record BoundPattern(Location Location, WylType Type);

/// <summary><c>_</c>, or a variant's fields when only the variant is named.</summary>
public sealed record BoundDiscardPattern(Location Location, WylType Type) : BoundPattern(Location, Type);

/// <summary>Matches anything and assigns it to <see cref="Local"/>, whose type the value converts to.</summary>
public sealed record BoundBindingPattern(Location Location, WylType Type, LocalSymbol Local) : BoundPattern(Location, Type);

/// <summary>Tests that a reference is non-null and refers to a <see cref="Target"/>, optionally binding it.</summary>
public sealed record BoundTypePattern(Location Location, WylType Type, WylType Target, LocalSymbol? Binding)
    : BoundPattern(Location, Type);

/// <summary><see cref="Fields"/> has one pattern per field of the variant.</summary>
public sealed record BoundVariantPattern(Location Location, WylType Type, VariantSymbol Variant, IReadOnlyList<BoundPattern> Fields)
    : BoundPattern(Location, Type);

/// <summary>
/// <see cref="Fields"/> has one pattern per field of the struct, in declaration order. On a nullable
/// struct, it also tests that the value is not null.
/// </summary>
public sealed record BoundStructPattern(Location Location, WylType Type, IReadOnlyList<BoundPattern> Fields)
    : BoundPattern(Location, Type)
{
    public IReadOnlyList<FieldSymbol> Symbols => Type.NonNull.Symbol!.Fields;
}

/// <summary>Compares with a literal of the matched type.</summary>
public sealed record BoundConstantPattern(Location Location, BoundLiteral Value) : BoundPattern(Location, Value.Type);

/// <summary>Tests that a <c>ref?</c> or <c>in?</c> refers to a place, and binds <see cref="Binding"/> to it.</summary>
public sealed record BoundReferencePattern(Location Location, WylType Type, LocalSymbol Binding) : BoundPattern(Location, Type);

public static class Patterns
{
    public static IEnumerable<LocalSymbol> Bindings(BoundPattern pattern) => pattern switch
    {
        BoundBindingPattern binding => [binding.Local],
        BoundReferencePattern reference => [reference.Binding],
        BoundTypePattern { Binding: { } binding } => [binding],
        BoundVariantPattern variant => variant.Fields.SelectMany(Bindings),
        BoundStructPattern structPattern => structPattern.Fields.SelectMany(Bindings),
        _ => [],
    };

    /// <summary>Whether a pattern matches every value of its type, so it tests nothing.</summary>
    public static bool AlwaysMatches(BoundPattern pattern) => pattern switch
    {
        BoundDiscardPattern or BoundBindingPattern => true,
        BoundTypePattern test => !test.Type.IsNullable && Conversions.IsSubtype(test.Type, test.Target),
        BoundStructPattern structPattern => !structPattern.Type.IsNullable && structPattern.Fields.All(AlwaysMatches),
        _ => false,
    };
}

/// <summary>The expressions directly inside an expression, in evaluation order.</summary>
public static class BoundTree
{
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
