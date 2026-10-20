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
    Virtual = 32,
    Override = 64,
    Abstract = 128,
    Final = 256,
    Weak = 512,
}

public enum RefKind { None, Ref, In }

public enum TypeKind { Struct, Class, Interface, Enum }

public abstract record Node(Location Location);

public sealed record CompilationUnit(SourceText Source, IReadOnlyList<Declaration> Declarations);

public abstract record Declaration(Location Location, Modifiers Modifiers) : Node(Location);

/// <summary><see cref="Bounds"/> are the interfaces after <c>extends</c>, joined by <c>&amp;</c>.</summary>
public sealed record TypeParameterSyntax(Location Location, string Name, IReadOnlyList<TypeSyntax> Bounds) : Node(Location);

/// <summary>
/// A struct, class, interface, or enum. Only classes have a <see cref="BaseType"/> or
/// <see cref="Destructor"/>; only enums have <see cref="Variants"/> or an <see cref="UnderlyingType"/>.
/// </summary>
public sealed record TypeDecl(
    Location Location,
    Modifiers Modifiers,
    TypeKind Kind,
    string Name,
    Location NameLocation,
    IReadOnlyList<TypeParameterSyntax> TypeParameters,
    TypeSyntax? BaseType,
    IReadOnlyList<TypeSyntax> Interfaces,
    IReadOnlyList<FieldDecl> Fields,
    IReadOnlyList<FunctionDecl> Methods,
    DestructorDecl? Destructor,
    IReadOnlyList<VariantDecl> Variants,
    TypeSyntax? UnderlyingType) : Declaration(Location, Modifiers);

/// <summary>An enum variant. <see cref="Fields"/> is null when the variant carries no data.</summary>
public sealed record VariantDecl(Location Location, string Name, Location NameLocation, IReadOnlyList<ParameterSyntax>? Fields, Expr? Value)
    : Node(Location);

public sealed record FieldDecl(Location Location, Modifiers Modifiers, TypeSyntax Type, string Name, Location NameLocation, Expr? Initializer)
    : Declaration(Location, Modifiers);

/// <summary>
/// A free function, method, or constructor. Constructors have no <see cref="ReturnType"/>;
/// abstract and interface methods have no <see cref="Body"/>.
/// </summary>
public sealed record FunctionDecl(
    Location Location,
    Modifiers Modifiers,
    TypeSyntax? ReturnType,
    string Name,
    Location NameLocation,
    IReadOnlyList<TypeParameterSyntax> TypeParameters,
    IReadOnlyList<ParameterSyntax> Parameters,
    BlockStmt? Body) : Declaration(Location, Modifiers)
{
    public bool IsConstructor => ReturnType is null;
}

public sealed record DestructorDecl(Location Location, Location NameLocation, BlockStmt Body) : Node(Location);

public sealed record TypeSyntax(Location Location, string Name, IReadOnlyList<TypeSyntax> TypeArguments, bool IsNullable = false)
    : Node(Location);

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

/// <summary><c>_ = value;</c>, which evaluates a value and ignores it.</summary>
public sealed record DiscardStmt(Location Location, Expr Value) : Stmt(Location);

public sealed record SwitchStmt(Location Location, Expr Value, IReadOnlyList<SwitchCase> Cases) : Stmt(Location);

/// <summary>
/// A <c>case</c> or, with no <see cref="Patterns"/>, a <c>default</c>. In a switch statement the
/// body is <see cref="Body"/>; in a switch expression it is <see cref="Value"/>.
/// </summary>
public sealed record SwitchCase(Location Location, IReadOnlyList<PatternSyntax> Patterns, Stmt? Body, Expr? Value) : Node(Location)
{
    public bool IsDefault => Patterns.Count == 0;
}

public abstract record Expr(Location Location) : Node(Location);

public sealed record IntLiteralExpr(Location Location, ulong Value, bool IsLong) : Expr(Location);

public sealed record FloatLiteralExpr(Location Location, double Value, bool IsDouble) : Expr(Location);

public sealed record BoolLiteralExpr(Location Location, bool Value) : Expr(Location);

public sealed record StringLiteralExpr(Location Location, string Value) : Expr(Location);

public sealed record NullLiteralExpr(Location Location) : Expr(Location);

/// <summary><see cref="TypeArguments"/> is non-empty for generic calls and constructions, like <c>max&lt;int&gt;(...)</c>.</summary>
public sealed record NameExpr(Location Location, string Name, IReadOnlyList<TypeSyntax> TypeArguments) : Expr(Location);

public sealed record ThisExpr(Location Location) : Expr(Location);

/// <summary><c>super</c>, as the callee of <c>super(...)</c> or the target of <c>super.method(...)</c>.</summary>
public sealed record SuperExpr(Location Location) : Expr(Location);

public sealed record MemberAccessExpr(Location Location, Expr Target, string Name, Location NameLocation, IReadOnlyList<TypeSyntax> TypeArguments)
    : Expr(Location);

/// <summary><c>target?.name</c>; a call <c>target?.name(...)</c> is a <see cref="CallExpr"/> with this as its callee.</summary>
public sealed record ConditionalAccessExpr(Location Location, Expr Target, string Name, Location NameLocation) : Expr(Location);

public sealed record UnaryExpr(Location Location, TokenKind Operator, Expr Operand) : Expr(Location);

/// <summary>Postfix <c>operand!</c>.</summary>
public sealed record NonNullExpr(Location Location, Expr Operand) : Expr(Location);

public sealed record BinaryExpr(Location Location, Expr Left, TokenKind Operator, Location OperatorLocation, Expr Right)
    : Expr(Location);

public sealed record CoalesceExpr(Location Location, Expr Left, Location OperatorLocation, Expr Right) : Expr(Location);

public sealed record IsExpr(Location Location, Expr Operand, PatternSyntax Pattern) : Expr(Location);

public sealed record SwitchExpr(Location Location, Expr Value, IReadOnlyList<SwitchCase> Cases) : Expr(Location);

public sealed record TryExpr(Location Location, Expr Operand) : Expr(Location);

public sealed record CallExpr(Location Location, Expr Callee, IReadOnlyList<Expr> Arguments) : Expr(Location);

/// <summary>A call argument written with <c>ref</c>.</summary>
public sealed record RefArgExpr(Location Location, Expr Operand) : Expr(Location);

public sealed record NewExpr(Location Location, TypeSyntax Type, IReadOnlyList<Expr> Arguments) : Expr(Location);

/// <summary><c>Type { field = value, ... }</c> or <c>Type(args) { field = value, ... }</c>.</summary>
public sealed record InitializerExpr(Location Location, Expr Target, IReadOnlyList<FieldInit> Fields) : Expr(Location);

public sealed record FieldInit(Location Location, string Name, Expr Value) : Node(Location);

public sealed record CastExpr(Location Location, TypeSyntax Type, Expr Operand) : Expr(Location);

public sealed record ConditionalExpr(Location Location, Expr Condition, Expr WhenTrue, Expr WhenFalse) : Expr(Location);

public sealed record ErrorExpr(Location Location) : Expr(Location);

public abstract record PatternSyntax(Location Location) : Node(Location);

/// <summary><c>_</c>, which matches anything.</summary>
public sealed record DiscardPatternSyntax(Location Location) : PatternSyntax(Location);

/// <summary><c>var name</c>, which matches anything and binds it.</summary>
public sealed record VarPatternSyntax(Location Location, string Name, Location NameLocation) : PatternSyntax(Location);

/// <summary><c>Type</c> or <c>Type name</c>: a type test, or for a value type an annotated binding.</summary>
public sealed record TypePatternSyntax(Location Location, TypeSyntax Type, string? Name, Location NameLocation) : PatternSyntax(Location);

/// <summary>
/// <c>Variant</c>, <c>Enum.Variant</c>, or either followed by field patterns in parentheses.
/// <see cref="Fields"/> is null without parentheses. A bare name may also turn out to be a type test.
/// </summary>
public sealed record VariantPatternSyntax(Location Location, TypeSyntax? Enum, string Name, Location NameLocation, IReadOnlyList<PatternSyntax>? Fields)
    : PatternSyntax(Location);

/// <summary>A literal to compare with.</summary>
public sealed record ConstantPatternSyntax(Location Location, Expr Value) : PatternSyntax(Location);
