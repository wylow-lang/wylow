using System.Text;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Syntax;

[Flags]
public enum Modifiers
{
    None = 0,
    Static = 8,
    Virtual = 32,
    Override = 64,
    Abstract = 128,
    Final = 256,
    Unsafe = 1024,
    Extern = 2048,

    /// <summary>On a struct: <c>ref struct</c>, a second-class type.</summary>
    Ref = 4096,

    /// <summary>On a class field: assignable after construction. On a struct or interface method: may change <c>this</c>.</summary>
    Mut = 8192,

    /// <summary>On a struct or enum: moved rather than copied.</summary>
    Unique = 16384,

    Shared = 32768,
}

/// <summary>
/// How far a declaration is visible (spec programs.visibility), narrowest first, so that the lesser
/// of two is the narrower.
/// </summary>
public enum Visibility
{
    /// <summary>Written as nothing: the file it is declared in.</summary>
    File,
    Module,
    Package,
    Public,
}

public enum RefKind { None, Ref, In }

public enum TypeKind { Struct, Class, Interface, Enum }

public abstract record Node(Location Location);

public sealed record CompilationUnit(SourceText Source, IReadOnlyList<ImportDecl> Imports, IReadOnlyList<Declaration> Declarations);

/// <summary>One name of a <c>::</c> path, where it is written.</summary>
public sealed record PathName(string Name, Location Location);

/// <summary>
/// <c>import a::b::C;</c>, <c>import a::{B, C as D};</c>, or <c>import a::*;</c> (spec
/// programs.imports): <see cref="Module"/> is the path before the last <c>::</c>, empty for <c>import std;</c>,
/// and <see cref="Names"/> what is imported from it, empty for <c>*</c>.
/// </summary>
public sealed record ImportDecl(Location Location, Visibility Visibility, IReadOnlyList<PathName> Module, IReadOnlyList<ImportedName> Names, bool IsAll)
    : Node(Location);

/// <summary>A name an import brings in, and what the file calls it if <c>as</c> renames it.</summary>
public sealed record ImportedName(PathName Name, PathName? Alias)
{
    public PathName Local => Alias ?? Name;
}

public abstract record Declaration(Location Location, Modifiers Modifiers) : Node(Location)
{
    public IReadOnlyList<AnnotationSyntax> Annotations { get; init; } = [];

    /// <summary>As written; an interface method writes none, and is as visible as its interface.</summary>
    public Visibility Visibility { get; init; }
}

/// <summary>
/// <c>@Name</c> or <c>@Name(arguments)</c> before a declaration, a parameter, or a local variable.
/// Each argument is a <see cref="TypeSyntax"/>, as in <c>@Repr(uint)</c>, or an <see cref="Expr"/>,
/// as in <c>@Align(16)</c>.
/// </summary>
public sealed record AnnotationSyntax(Location Location, string Name, IReadOnlyList<Node> Arguments) : Node(Location);

/// <summary><see cref="Bounds"/> are the interfaces after <c>extends</c>, joined by <c>&amp;</c>.</summary>
public sealed record TypeParameterSyntax(Location Location, string Name, IReadOnlyList<TypeSyntax> Bounds) : Node(Location);

/// <summary>
/// A struct, class, interface, or enum. Only classes and unique structs have a
/// <see cref="Destructor"/>; only enums have <see cref="Variants"/>.
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
    IReadOnlyList<VariantDecl> Variants) : Declaration(Location, Modifiers);

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

    /// <summary>For <c>ref T name(...)</c> or <c>in T name(...)</c>, which return a reference.</summary>
    public RefKind ReturnRefKind { get; init; }

    /// <summary>The <c>where</c> clauses of a member, which bound its type's type parameters (spec <c>generics.bounds</c>).</summary>
    public IReadOnlyList<TypeBoundSyntax> Where { get; init; } = [];
}

/// <summary>A <c>where T extends Bound &amp; Bound</c> clause of a member.</summary>
public sealed record TypeBoundSyntax(Location Location, string Parameter, Location ParameterLocation, IReadOnlyList<TypeSyntax> Bounds)
    : Node(Location);

public sealed record DestructorDecl(Location Location, Location NameLocation, BlockStmt Body) : Node(Location);

public enum TypeShape { Named, FixedArray, Array, Pointer, Function, NullableReference }

/// <summary>
/// A type as written. A <see cref="TypeShape.Named"/> type has a <see cref="Name"/> and type
/// arguments; the other shapes are built from their <see cref="Element"/>: <c>T[N]</c>, <c>T[]</c>,
/// <c>T*</c>, the return type of <c>fn(Parameters) -> T</c>, and the <c>T</c> of <c>ref? T</c> or
/// <c>in? T</c>, which is null for <c>ref? var</c>.
/// </summary>
public sealed record TypeSyntax(Location Location, string Name, IReadOnlyList<TypeSyntax> TypeArguments, bool IsNullable = false)
    : Node(Location)
{
    /// <summary>The modules a <see cref="TypeShape.Named"/> type is written in, as in <c>memory::CycleStats</c>; empty for a plain name.</summary>
    public IReadOnlyList<PathName> Modules { get; init; } = [];

    public TypeShape Shape { get; init; }
    public TypeSyntax? Element { get; init; }

    /// <summary>The <c>N</c> of <c>T[N]</c>.</summary>
    public ulong Length { get; init; }

    /// <summary>Whether <c>ref? T</c> or <c>in? T</c>.</summary>
    public RefKind ReferenceKind { get; init; }

    public IReadOnlyList<FunctionTypeParameter> Parameters { get; init; } = [];

    public bool IsShared { get; init; }
}

/// <summary>A parameter of a function type, which has a mode but no name: the <c>ref Vec3</c> of <c>fn(ref Vec3) -> void</c>.</summary>
public sealed record FunctionTypeParameter(RefKind RefKind, TypeSyntax Type)
{
    public IReadOnlyList<AnnotationSyntax> Annotations { get; init; } = [];

    /// <summary>Where <c>escaping</c> is written, if it is.</summary>
    public Location? Escaping { get; init; }
}

public sealed record ParameterSyntax(Location Location, RefKind RefKind, TypeSyntax Type, string Name) : Node(Location)
{
    public IReadOnlyList<AnnotationSyntax> Annotations { get; init; } = [];
    public bool IsMutable { get; init; }

    /// <summary>Written <c>this T name</c>, which a method takes as its by-value receiver.</summary>
    public bool IsReceiver { get; init; }

    /// <summary>Where <c>escaping</c> is written, if it is.</summary>
    public Location? Escaping { get; init; }

    /// <summary>The value the parameter takes when a call leaves it out, as in <c>int count = 1</c>.</summary>
    public Expr? Default { get; init; }
}

public abstract record Stmt(Location Location) : Node(Location);

public sealed record BlockStmt(Location Location, IReadOnlyList<Stmt> Statements) : Stmt(Location);

public sealed record UnsafeStmt(Location Location, BlockStmt Body) : Stmt(Location);

/// <summary><see cref="Type"/> is null for <c>var</c>. A <c>ref</c> or <c>in</c> local refers to the place it is initialized with.</summary>
public sealed record VarDeclStmt(Location Location, TypeSyntax? Type, string Name, Location NameLocation, Expr? Initializer, RefKind RefKind = RefKind.None)
    : Stmt(Location)
{
    public IReadOnlyList<AnnotationSyntax> Annotations { get; init; } = [];
    public bool IsMutable { get; init; }

    /// <summary>Where <c>escaping</c> is written, if it is.</summary>
    public Location? Escaping { get; init; }
}

public sealed record IfStmt(Location Location, Expr Condition, BlockStmt Then, Stmt? Else) : Stmt(Location);

public sealed record WhileStmt(Location Location, Expr Condition, BlockStmt Body) : Stmt(Location);

public sealed record ForStmt(Location Location, Stmt? Initializer, Expr? Condition, Stmt? Update, BlockStmt Body)
    : Stmt(Location);

/// <summary>A for-each loop variable; <see cref="Type"/> is null for <c>var</c>.</summary>
public sealed record ForEachVariable(Location Location, RefKind RefKind, TypeSyntax? Type, string Name, Location NameLocation) : Node(Location)
{
    public bool IsMutable { get; init; }
}

/// <summary>
/// <c>for (element : iterable)</c>, or with <see cref="Index"/>, <c>for (int i, element : iterable)</c>.
/// With <see cref="RangeEnd"/>, the loop runs over the integers from <see cref="Iterable"/> to it.
/// </summary>
public sealed record ForEachStmt(
    Location Location,
    ForEachVariable? Index,
    ForEachVariable Element,
    Expr Iterable,
    Expr? RangeEnd,
    bool IsInclusive,
    BlockStmt Body) : Stmt(Location);

/// <summary><see cref="IsRef"/> for <c>return ref place;</c>, which returns a reference.</summary>
public sealed record ReturnStmt(Location Location, Expr? Value, bool IsRef = false) : Stmt(Location);

public sealed record BreakStmt(Location Location) : Stmt(Location);

public sealed record ContinueStmt(Location Location) : Stmt(Location);

public sealed record ExprStmt(Location Location, Expr Expression) : Stmt(Location);

public sealed record AssignStmt(Location Location, Expr Target, TokenKind Operator, Location OperatorLocation, Expr Value)
    : Stmt(Location);

public sealed record IncDecStmt(Location Location, Expr Target, bool IsIncrement) : Stmt(Location);

/// <summary><c>_ = value;</c>, which evaluates a value and ignores it.</summary>
public sealed record DiscardStmt(Location Location, Expr Value) : Stmt(Location);

/// <summary><c>discard name;</c>: ends a by-value parameter's lifetime without its destructor's body (spec <c>structs.unique.destructors</c>).</summary>
public sealed record DiscardLocalStmt(Location Location, string Name, Location NameLocation) : Stmt(Location);

public sealed record SwitchStmt(Location Location, Expr Value, IReadOnlyList<SwitchCase> Cases) : Stmt(Location);

/// <summary>
/// A <c>case</c>, a <c>default</c> (no <see cref="Patterns"/>), or <c>case null, default</c>. In a
/// switch statement the body is <see cref="Body"/>; in a switch expression it is <see cref="Value"/>.
/// </summary>
public sealed record SwitchCase(Location Location, IReadOnlyList<PatternSyntax> Patterns, Stmt? Body, Expr? Value) : Node(Location)
{
    /// <summary>Where <c>default</c> is written, if the case includes it.</summary>
    public Location? Default { get; init; }

    /// <summary><c>when condition</c>, which the case matches only if it is also true.</summary>
    public Expr? Guard { get; init; }

    public bool IsDefault => Default is not null;
}

public abstract record Expr(Location Location) : Node(Location);

public sealed record IntLiteralExpr(Location Location, ulong Value, IntegerSuffix Suffix) : Expr(Location);

public sealed record FloatLiteralExpr(Location Location, double Value, bool IsDouble) : Expr(Location);

public sealed record BoolLiteralExpr(Location Location, bool Value) : Expr(Location);

public sealed record CharLiteralExpr(Location Location, Rune Value) : Expr(Location);

public sealed record StringLiteralExpr(Location Location, string Value) : Expr(Location);

/// <summary><c>$"a{b}c"</c>: <see cref="Texts"/> surround the <see cref="Values"/>, one more of them than values.</summary>
public sealed record InterpolatedStringExpr(Location Location, IReadOnlyList<string> Texts, IReadOnlyList<InterpolatedValue> Values) : Expr(Location);

/// <summary>A hole <c>{value,alignment:format}</c>, whose alignment and format may be left out.</summary>
public sealed record InterpolatedValue(Location Location, Expr Value, int? Alignment, string? Format, Location FormatLocation) : Node(Location);

public sealed record NullLiteralExpr(Location Location) : Expr(Location);

/// <summary><see cref="TypeArguments"/> is non-empty for generic calls and constructions, like <c>max&lt;int&gt;(...)</c>.</summary>
public sealed record NameExpr(Location Location, string Name, IReadOnlyList<TypeSyntax> TypeArguments) : Expr(Location);

/// <summary>
/// A name in a module, as in <c>gen::seed</c> or <c>world::Chunk</c>: <see cref="Modules"/> are
/// walked with <c>::</c>, and <see cref="Name"/> is looked up in the last of them. It is a node of
/// its own, not a <see cref="NameExpr"/>, so that nothing binds it as a name in scope by mistake.
/// </summary>
public sealed record PathExpr(Location Location, IReadOnlyList<PathName> Modules, NameExpr Name) : Expr(Location);

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

/// <summary><c>target[arguments]</c>: an element of an array, or a call of an indexer.</summary>
public sealed record IndexExpr(Location Location, Expr Target, IReadOnlyList<Expr> Arguments) : Expr(Location);

/// <summary><c>[a, b, c]</c>, a fixed array.</summary>
public sealed record ArrayLiteralExpr(Location Location, IReadOnlyList<Expr> Elements) : Expr(Location);

/// <summary>A call argument written with <c>ref</c>.</summary>
public sealed record RefArgExpr(Location Location, Expr Operand) : Expr(Location);

/// <summary>An argument passed by its parameter's name: <c>spread: 2.0</c>. <see cref="Value"/> may be a <see cref="RefArgExpr"/>.</summary>
public sealed record NamedArgExpr(Location Location, string Name, Location NameLocation, Expr Value) : Expr(Location);

public sealed record NewExpr(Location Location, TypeSyntax Type, IReadOnlyList<Expr> Arguments) : Expr(Location);

/// <summary><c>new T[length]</c>, a heap array.</summary>
public sealed record NewArrayExpr(Location Location, TypeSyntax ElementType, Expr Length) : Expr(Location);

public sealed record CastExpr(Location Location, TypeSyntax Type, Expr Operand) : Expr(Location);

public sealed record ConditionalExpr(Location Location, Expr Condition, Expr WhenTrue, Expr WhenFalse) : Expr(Location);

/// <summary>A lambda parameter. <see cref="Type"/> is null when it comes from the lambda's target, as in <c>x -> x + 1</c>.</summary>
public sealed record LambdaParameterSyntax(Location Location, RefKind RefKind, TypeSyntax? Type, string Name, Location NameLocation)
    : Node(Location)
{
    public IReadOnlyList<AnnotationSyntax> Annotations { get; init; } = [];
    public bool IsMutable { get; init; }

    /// <summary>Where <c>escaping</c> is written, if it is.</summary>
    public Location? Escaping { get; init; }
}

/// <summary><c>x -> value</c> or <c>(a, b) -> { ... }</c>. Exactly one of <see cref="Value"/> and <see cref="Body"/> is set.</summary>
public sealed record LambdaExpr(Location Location, IReadOnlyList<LambdaParameterSyntax> Parameters, Expr? Value, BlockStmt? Body)
    : Expr(Location)
{
    /// <summary>Made by the binder for a function used as a value, which this lambda calls; see <c>Binder.FunctionReferences.cs</c>.</summary>
    public bool IsFunctionReference { get; init; }
}

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

/// <summary>
/// <c>Struct(p1, p2)</c> written with a module path or type arguments, which only a struct can be.
/// <c>Name(p1, p2)</c> is a <see cref="VariantPatternSyntax"/>, a struct pattern if the value is a struct.
/// </summary>
public sealed record StructPatternSyntax(Location Location, TypeSyntax Type, IReadOnlyList<PatternSyntax> Fields) : PatternSyntax(Location);

/// <summary>A literal to compare with.</summary>
public sealed record ConstantPatternSyntax(Location Location, Expr Value) : PatternSyntax(Location);

/// <summary>
/// <c>ref T name</c> or <c>in T name</c>, which tests that a <c>ref?</c> or <c>in?</c> refers to a
/// place and names it. <see cref="Type"/> is null for <c>ref var name</c>.
/// </summary>
public sealed record ReferencePatternSyntax(Location Location, RefKind RefKind, TypeSyntax? Type, string Name, Location NameLocation)
    : PatternSyntax(Location);
