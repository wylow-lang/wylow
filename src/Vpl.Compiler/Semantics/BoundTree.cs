using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

/// <summary><see cref="Functions"/> holds free functions, methods, and constructors.</summary>
public sealed record BoundProgram(
    SourceText Source,
    IReadOnlyList<StructSymbol> Structs,
    IReadOnlyList<BoundFunction> Functions,
    FunctionSymbol? Main);

public sealed record BoundFunction(FunctionSymbol Symbol, BoundBlock Body);

public abstract record BoundNode(Location Location);

public abstract record BoundStatement(Location Location) : BoundNode(Location);

public sealed record BoundBlock(Location Location, IReadOnlyList<BoundStatement> Statements) : BoundStatement(Location);

public sealed record BoundVariableDeclaration(Location Location, LocalSymbol Local, BoundExpression? Initializer)
    : BoundStatement(Location);

/// <summary><see cref="Target"/> is a local, or a field path rooted at a local or <c>this</c>.</summary>
public sealed record BoundAssignment(Location Location, BoundExpression Target, BoundExpression Value) : BoundStatement(Location);

public sealed record BoundExpressionStatement(Location Location, BoundExpression Expression) : BoundStatement(Location);

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

public sealed record BoundBreak(Location Location) : BoundStatement(Location);

public sealed record BoundContinue(Location Location) : BoundStatement(Location);

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

public sealed record BoundLocalAccess(Location Location, LocalSymbol Local) : BoundExpression(Location, Local.Type);

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

/// <summary>Both operands have the same type, except for shifts.</summary>
public sealed record BoundBinary(Location Location, BoundExpression Left, BoundBinaryOperator Operator, BoundExpression Right, VplType Type)
    : BoundExpression(Location, Type);

public sealed record BoundThis(Location Location, VplType Type) : BoundExpression(Location, Type);

/// <summary><see cref="BoundExpression.Type"/> is the field's type with the receiver's type arguments applied.</summary>
public sealed record BoundFieldAccess(Location Location, BoundExpression Receiver, FieldSymbol Field, VplType Type)
    : BoundExpression(Location, Type);

/// <summary><see cref="Receiver"/> is set for instance methods. A <c>ref</c> argument is the place itself.</summary>
public sealed record BoundCall(Location Location, MethodReference Method, BoundExpression? Receiver, IReadOnlyList<BoundExpression> Arguments)
    : BoundExpression(Location, Method.ReturnType);

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
