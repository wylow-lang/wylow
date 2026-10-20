using Vpl.Compiler.Text;

namespace Vpl.Compiler.Syntax;

[Flags]
public enum Modifiers
{
    None = 0,
    Public = 1,
    Private = 2,
    Protected = 4,
    Static = 8,
    ReadOnly = 16,
}

public enum RefKind { None, Ref, In }

public abstract record Node(Location Location);

public sealed record CompilationUnit(SourceText Source, IReadOnlyList<Declaration> Declarations);

public abstract record Declaration(Location Location, Modifiers Modifiers) : Node(Location);

public sealed record StructDecl(
    Location Location,
    Modifiers Modifiers,
    string Name,
    Location NameLocation,
    IReadOnlyList<FieldDecl> Fields,
    IReadOnlyList<FunctionDecl> Methods) : Declaration(Location, Modifiers);

public sealed record FieldDecl(Location Location, Modifiers Modifiers, TypeSyntax Type, string Name, Location NameLocation, Expr? Initializer)
    : Declaration(Location, Modifiers);

/// <summary>A free function, method, or constructor. Constructors have no <see cref="ReturnType"/>.</summary>
public sealed record FunctionDecl(
    Location Location,
    Modifiers Modifiers,
    TypeSyntax? ReturnType,
    string Name,
    Location NameLocation,
    IReadOnlyList<ParameterSyntax> Parameters,
    BlockStmt Body) : Declaration(Location, Modifiers)
{
    public bool IsConstructor => ReturnType is null;
}

public sealed record TypeSyntax(Location Location, string Name) : Node(Location);

public sealed record ParameterSyntax(Location Location, RefKind RefKind, TypeSyntax Type, string Name) : Node(Location);

public abstract record Stmt(Location Location) : Node(Location);

public sealed record BlockStmt(Location Location, IReadOnlyList<Stmt> Statements) : Stmt(Location);

/// <summary><see cref="Type"/> is null for <c>var</c>.</summary>
public sealed record VarDeclStmt(Location Location, TypeSyntax? Type, string Name, Location NameLocation, Expr? Initializer)
    : Stmt(Location);

public sealed record IfStmt(Location Location, Expr Condition, BlockStmt Then, Stmt? Else) : Stmt(Location);

public sealed record WhileStmt(Location Location, Expr Condition, BlockStmt Body) : Stmt(Location);

public sealed record ForStmt(Location Location, Stmt? Initializer, Expr? Condition, Stmt? Update, BlockStmt Body)
    : Stmt(Location);

public sealed record ReturnStmt(Location Location, Expr? Value) : Stmt(Location);

public sealed record BreakStmt(Location Location) : Stmt(Location);

public sealed record ContinueStmt(Location Location) : Stmt(Location);

public sealed record ExprStmt(Location Location, Expr Expression) : Stmt(Location);

public sealed record AssignStmt(Location Location, Expr Target, TokenKind Operator, Location OperatorLocation, Expr Value)
    : Stmt(Location);

public sealed record IncDecStmt(Location Location, Expr Target, bool IsIncrement) : Stmt(Location);

public abstract record Expr(Location Location) : Node(Location);

public sealed record IntLiteralExpr(Location Location, ulong Value, bool IsLong) : Expr(Location);

public sealed record FloatLiteralExpr(Location Location, double Value, bool IsDouble) : Expr(Location);

public sealed record BoolLiteralExpr(Location Location, bool Value) : Expr(Location);

public sealed record StringLiteralExpr(Location Location, string Value) : Expr(Location);

public sealed record NameExpr(Location Location, string Name) : Expr(Location);

public sealed record ThisExpr(Location Location) : Expr(Location);

public sealed record MemberAccessExpr(Location Location, Expr Target, string Name, Location NameLocation) : Expr(Location);

public sealed record UnaryExpr(Location Location, TokenKind Operator, Expr Operand) : Expr(Location);

public sealed record BinaryExpr(Location Location, Expr Left, TokenKind Operator, Location OperatorLocation, Expr Right)
    : Expr(Location);

public sealed record CallExpr(Location Location, Expr Callee, IReadOnlyList<Expr> Arguments) : Expr(Location);

/// <summary>A call argument written with <c>ref</c>.</summary>
public sealed record RefArgExpr(Location Location, Expr Operand) : Expr(Location);

/// <summary><c>Type { field = value, ... }</c> or <c>Type(args) { field = value, ... }</c>.</summary>
public sealed record InitializerExpr(Location Location, Expr Target, IReadOnlyList<FieldInit> Fields) : Expr(Location);

public sealed record FieldInit(Location Location, string Name, Expr Value) : Node(Location);

public sealed record CastExpr(Location Location, TypeSyntax Type, Expr Operand) : Expr(Location);

public sealed record ConditionalExpr(Location Location, Expr Condition, Expr WhenTrue, Expr WhenFalse) : Expr(Location);

public sealed record ErrorExpr(Location Location) : Expr(Location);
