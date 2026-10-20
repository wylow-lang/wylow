using Vpl.Compiler.Text;

namespace Vpl.Compiler.Syntax;

public abstract record Node(Location Location);

public sealed record CompilationUnit(SourceText Source, IReadOnlyList<FunctionDecl> Functions);

public sealed record TypeSyntax(Location Location, string Name) : Node(Location);

public sealed record ParameterSyntax(Location Location, TypeSyntax Type, string Name) : Node(Location);

public sealed record FunctionDecl(
    Location Location,
    TypeSyntax ReturnType,
    string Name,
    Location NameLocation,
    IReadOnlyList<ParameterSyntax> Parameters,
    BlockStmt Body) : Node(Location);

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

public sealed record UnaryExpr(Location Location, TokenKind Operator, Expr Operand) : Expr(Location);

public sealed record BinaryExpr(Location Location, Expr Left, TokenKind Operator, Location OperatorLocation, Expr Right)
    : Expr(Location);

public sealed record CallExpr(Location Location, Expr Callee, IReadOnlyList<Expr> Arguments) : Expr(Location);

public sealed record CastExpr(Location Location, TypeSyntax Type, Expr Operand) : Expr(Location);

public sealed record ConditionalExpr(Location Location, Expr Condition, Expr WhenTrue, Expr WhenFalse) : Expr(Location);

public sealed record ErrorExpr(Location Location) : Expr(Location);
