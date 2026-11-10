using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed class LocalSymbol(string name, WylType type, bool isParameter, RefKind refKind = RefKind.None)
{
    public string Name { get; } = name;
    public WylType Type { get; } = type;
    public bool IsParameter { get; } = isParameter;
    public RefKind RefKind { get; } = refKind;

    /// <summary>A compiler-introduced local, which has no source name.</summary>
    public bool IsTemporary { get; init; }

    /// <summary>Where it is declared, if it is the variable a for-each loop copies each element into.</summary>
    public Location? LoopElement { get; init; }

    /// <summary>
    /// The name an <c>is ref T name</c> pattern gives a place. It is a <c>ref?</c> that is only ever
    /// assigned a place, so it is used as one wherever it is assigned.
    /// </summary>
    public bool IsReferenceBinding { get; init; }

    /// <summary>
    /// A <c>ref</c> or <c>in</c> name a pattern binds, which refers to a place wherever its case
    /// matched, so it is used as a place as an <c>is ref</c> name is, but is never nothing.
    /// </summary>
    public bool IsPatternBinding { get; init; }

    /// <summary>Declared <c>mut</c>: it may be reassigned, and a value in it changed in place.</summary>
    public bool IsMutable { get; init; }

    /// <summary>
    /// Declared without a value, so its first assignment initializes it. Unless it is mutable, that
    /// is its only assignment, which the flow analysis checks.
    /// </summary>
    public bool IsDeferred { get; init; }

    /// <summary>The function or lambda that declares it; null for compiler temporaries, which no other function uses.</summary>
    public FunctionSymbol? Owner { get; set; }

    /// <summary>A parameter's default value as written, bound with <see cref="Default"/>.</summary>
    public Expr? DefaultSyntax { get; init; }

    /// <summary>
    /// The parameter whose default value a call that leaves this one out passes: itself if it
    /// declares one, or the parameter of the method it overrides or implements. Set by the binder.
    /// </summary>
    public LocalSymbol? DefaultDeclaration { get; set; }

    /// <summary>The bound <see cref="DefaultSyntax"/>, a constant or struct construction, typed by the declaring method's own type parameters.</summary>
    public BoundExpression? Default { get; set; }

    /// <summary>For a memberwise constructor's parameter, the field it sets, whose initializer or type's default value it defaults to.</summary>
    public FieldSymbol? Field { get; init; }

    /// <summary>
    /// Whether code can change it after it is initialized, reassigning it or changing its value in
    /// place: it is <c>mut</c>, a <c>ref</c>, or a compiler temporary.
    /// </summary>
    public bool IsWritable => IsMutable || IsTemporary || RefKind == RefKind.Ref;

    public override string ToString() => Name;
}

public sealed class FunctionSymbol(string name, IReadOnlyList<LocalSymbol> parameters, WylType returnType)
{
    public string Name { get; } = name;
    public IReadOnlyList<LocalSymbol> Parameters { get; } = parameters;

    /// <summary>Constructors and destructors return <c>void</c>; the construction expression has the type itself.</summary>
    public WylType ReturnType { get; private set; } = returnType;

    /// <summary>Gives a lambda bound before its return type was known the type that generic inference chose.</summary>
    public void InferReturnType(WylType type)
    {
        if (!IsLambda || ReturnType != WylType.Error)
            throw new InvalidOperationException("only a lambda whose return type is being inferred can be given one");
        ReturnType = type;
    }

    public FunctionDecl? Declaration { get; init; }

    /// <summary>The body to bind: the declaration's, or for a destructor, which has no declaration, its own.</summary>
    public BlockStmt? Body { get; init; }

    public IReadOnlyList<TypeParameterSymbol> TypeParameters { get; init; } = [];

    /// <summary>The interfaces a member's <c>where</c> clause requires of its type's parameters (spec <c>generics.bounds</c>).</summary>
    public List<MemberInterfaceBound> MemberInterfaceBounds { get; } = [];

    /// <summary>The type parameters a member's <c>where</c> clause says are copyable.</summary>
    public HashSet<TypeParameterSymbol> CopyableParameters { get; } = [];

    public HashSet<TypeParameterSymbol> SendableParameters { get; } = [];

    /// <summary>Whether a <c>where</c> clause bounds any of the declaring type's parameters.</summary>
    public bool HasMemberBounds => MemberInterfaceBounds.Count > 0 || CopyableParameters.Count > 0 || SendableParameters.Count > 0;

    /// <summary>A method returning <c>ref</c> or <c>in</c> returns a reference into its receiver.</summary>
    public RefKind ReturnRefKind { get; init; }

    public bool IsIndexer => Name == SyntaxFacts.IndexerName;
    public bool IsIndexerSetter => Name == SyntaxFacts.IndexerSetterName;
    public TypeSymbol? ContainingType { get; init; }
    public bool IsStatic { get; init; }
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// A struct or enum method that takes its receiver by value, written as its first parameter,
    /// <c>this T name</c> (spec <c>structs.methods.by-value</c>). It has no <c>this</c>.
    /// </summary>
    public bool HasByValueReceiver => ReceiverParameter is not null;

    /// <summary>The receiver of a method with a by-value receiver; it is not one of <see cref="Parameters"/>.</summary>
    public LocalSymbol? ReceiverParameter { get; init; }

    /// <summary>
    /// A top-level function that marks its first parameter <c>this</c>, which may be called like a
    /// method of that parameter's type (spec <c>functions.extensions</c>). Unlike a method's by-value
    /// receiver, the receiver stays the first of <see cref="Parameters"/>, so a call passes it as an
    /// ordinary argument.
    /// </summary>
    public bool IsExtension { get; init; }

    /// <summary>
    /// Where it can be named from, already within its type's. A symbol the binder doesn't give one
    /// is visible nowhere, so a missed one is an error rather than a hole.
    /// </summary>
    public Reach Reach { get; init; }

    public bool IsConstructor { get; init; }
    public bool IsDestructor { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsOverride { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsFinal { get; init; }

    /// <summary>Declared <c>unsafe</c>: its body is unsafe code, and only unsafe code may call it.</summary>
    public bool IsUnsafe { get; init; }

    /// <summary>The compiler-provided default constructor.</summary>
    public bool IsImplicit { get; init; }

    /// <summary>The constructor a struct without declared ones gets, taking each field as a parameter that defaults to it.</summary>
    public bool IsMemberwise { get; init; }

    /// <summary>The parameters and return type of a lambda, whose body is a <see cref="BoundLambda"/>'s.</summary>
    public bool IsLambda { get; init; }

    /// <summary>A lambda converted to an escaping function type, which copies what it captures.</summary>
    public bool IsEscaping { get; init; }

    public bool IsShared { get; init; }

    /// <summary>For a lambda, the function or lambda whose body creates it.</summary>
    public FunctionSymbol? Enclosing { get; init; }

    /// <summary>
    /// The member whose <c>where</c> clause bounds the type parameters in the function: the function
    /// itself, or for a lambda, the member whose body the lambda is in.
    /// </summary>
    public FunctionSymbol Member => Enclosing?.Member ?? this;

    public string? BuiltinCppName { get; init; }
    public bool IsNoReturn { get; init; }

    /// <summary>A builtin that can't panic or change anything, so it needs no more ordering than its arguments.</summary>
    public bool IsPure { get; init; }

    /// <summary>The C++ call gets the call site's source location appended, for panic messages.</summary>
    public bool PassesLocation { get; init; }

    public bool IsBuiltin => BuiltinCppName is not null;

    /// <summary>Declared <c>extern</c> in the standard library and implemented in the C++ runtime.</summary>
    public bool IsExtern => IsBuiltin && Declaration is not null;
    public bool IsGeneric => TypeParameters.Count > 0;
    public bool IsInstanceMethod => ContainingType is not null && !IsStatic && !IsConstructor && !IsDestructor;

    /// <summary>Whether a call dispatches on the receiver's runtime type. Interface methods always do.</summary>
    public bool IsDispatched => IsVirtual || IsOverride || IsAbstract || ContainingType?.Kind == TypeKind.Interface;

    /// <summary>
    /// A class method implementing an interface's, which overrides it in C++ without being declared
    /// <c>override</c>, or is called by a forwarder that does.
    /// </summary>
    public bool ImplementsInterface { get; set; }

    /// <summary>
    /// Whether a reference it returns may point into an object that the caller does not hold, so
    /// that the call also gives the caller that object's hold or borrow (spec
    /// <c>functions.ref-returns</c>). A dispatched method, a method that implements or overrides
    /// one, and a method that returns a place reached through its receiver, or a place that such a
    /// method returned, return a hold. The binder sets it once every body is bound.
    /// </summary>
    public bool ReturnsHold { get; set; }

    /// <summary>The methods it overrides, and the interface methods it implements for some class or struct; set by the binder.</summary>
    public List<FunctionSymbol> Overridden { get; } = [];

    /// <summary>
    /// For a method that overrides nothing: the base class's method of its signature that it can't
    /// see, so doesn't override (spec programs.visibility), though C++ would; set by the binder.
    /// </summary>
    public FunctionSymbol? Shadows { get; set; }

    /// <summary>Whether a subclass may override it.</summary>
    public bool IsOverridable => (IsVirtual || IsAbstract || IsOverride) && !IsFinal;

    public string Signature
    {
        get
        {
            var typeParameters = IsGeneric ? $"<{string.Join(", ", TypeParameters.Select(p => p.Name))}>" : "";
            var parameters = string.Join(", ", Parameters.Select(p => (p.Type.NonNull.IsEscaping ? "escaping " : "") + ParameterText(p.RefKind, p.Type)));
            return $"{Name}{typeParameters}({parameters})";
        }
    }

    public static string RefPrefix(RefKind kind) => kind switch
    {
        RefKind.Ref => "ref ",
        RefKind.In => "in ",
        _ => "",
    };

    /// <summary>
    /// The panic an abstract method's stub raises: a call reaching it while an object is still its
    /// base class, during the base's constructor or destructor (spec classes.construction).
    /// </summary>
    public string AbstractCallMessage =>
        $"abstract method '{ContainingType!.Name}.{Name}' called during construction or destruction";

    /// <summary>A parameter's mode and type as written: <c>ref int</c>, or <c>ref? int</c>, whose type names its mode.</summary>
    public static string ParameterText(RefKind kind, WylType type) => type.IsNullableReference ? type.Name : RefPrefix(kind) + type;

    /// <summary>The signature with its type parameters' bounds, which tell apart overloads that differ only in them.</summary>
    public string BoundedSignature
    {
        get
        {
            static string Bounded(TypeParameterSymbol p)
            {
                List<string> bounds = [.. p.IsCopyableBound ? ["copyable"] : Array.Empty<string>(), .. p.IsSendableBound ? ["sendable"] : Array.Empty<string>(), .. p.Bounds.Select(b => b.ToString())];
                return bounds.Count == 0 ? p.Name : $"{p.Name} extends {string.Join(" & ", bounds)}";
            }
            return IsGeneric ? $"{Name}<{string.Join(", ", TypeParameters.Select(Bounded))}>{Signature[Signature.IndexOf('(')..]}" : Signature;
        }
    }

    public override string ToString() => Signature;
}

/// <summary>An interface that a member requires of one of its type's type parameters.</summary>
public sealed record MemberInterfaceBound(TypeParameterSymbol Parameter, WylType Bound);

public sealed class TypeParameterSymbol
{
    public TypeParameterSymbol(string name)
    {
        Name = name;
        Type = TypeParameterType.Of(this);
    }

    public string Name { get; }
    public WylType Type { get; }

    /// <summary>Interfaces that every type argument must implement.</summary>
    public List<WylType> Bounds { get; } = [];

    /// <summary>Whether the declaration's <c>copyable</c> bound says a value of it is copied (spec <c>generics.bounds</c>).</summary>
    public bool IsCopyableBound { get; internal set; }

    public bool IsSendableBound { get; internal set; }

    /// <summary>
    /// The interfaces the parameter implements in <paramref name="function"/>: the declaration's
    /// bounds, and those that the <c>where</c> clause of the function's member adds (spec
    /// <c>generics.bounds</c>). Outside a function, only the declaration's bounds apply.
    /// </summary>
    public IEnumerable<WylType> BoundsIn(FunctionSymbol? function) =>
        function is null ? Bounds : [.. Bounds, .. function.Member.MemberInterfaceBounds.Where(b => b.Parameter == this).Select(b => b.Bound)];

    /// <summary>Whether a value of the parameter is copied in <paramref name="function"/>, by the declaration's bound or the member's <c>where</c> clause.</summary>
    public bool IsCopyableIn(FunctionSymbol? function) => IsCopyableBound || function?.Member.CopyableParameters.Contains(this) == true;

    /// <summary>Whether the parameter is sendable in <paramref name="function"/>, by the declaration's bound or the member's <c>where</c> clause.</summary>
    public bool IsSendableIn(FunctionSymbol? function) => IsSendableBound || function?.Member.SendableParameters.Contains(this) == true;

    public override string ToString() => Name;
}

/// <summary>
/// A function as called: <see cref="Owner"/> is the type it is a member of, with type arguments,
/// and <see cref="TypeArguments"/> are the function's own type arguments.
/// </summary>
public sealed class MethodReference
{
    public MethodReference(FunctionSymbol symbol, WylType? owner, IReadOnlyList<WylType> typeArguments)
    {
        Symbol = symbol;
        Owner = owner;
        TypeArguments = typeArguments;
        Map = (owner is null ? TypeMap.Empty : TypeMap.For(owner)).With(symbol.TypeParameters, typeArguments);
    }

    public FunctionSymbol Symbol { get; }
    public WylType? Owner { get; }
    public IReadOnlyList<WylType> TypeArguments { get; }
    public TypeMap Map { get; }

    /// <summary>Whether the call passes the receiver as its first argument, which is not one of <see cref="Symbol.Parameters"/>.</summary>
    public int ReceiverCount => Symbol.HasByValueReceiver ? 1 : 0;

    public WylType ParameterType(int index) => Map.Apply(Symbol.Parameters[index].Type);
    public RefKind ParameterRefKind(int index) => Symbol.Parameters[index].RefKind;

    /// <summary>The type of the argument at <paramref name="index"/>, counting a by-value receiver first.</summary>
    public WylType ArgumentType(int index) => index < ReceiverCount ? ReceiverType : ParameterType(index - ReceiverCount);

    public RefKind ArgumentRefKind(int index) => index < ReceiverCount ? RefKind.None : ParameterRefKind(index - ReceiverCount);

    public WylType ReceiverType => Map.Apply(Symbol.ReceiverParameter!.Type);
    public WylType ReturnType => Map.Apply(Symbol.ReturnType);

    public override string ToString() => Symbol.Signature;
}

public sealed class FieldSymbol(string name, WylType type, TypeSymbol owner, FieldDecl declaration)
{
    public string Name { get; } = name;
    public WylType Type { get; } = type;
    public TypeSymbol Owner { get; } = owner;
    public FieldDecl Declaration { get; } = declaration;
    public Reach Reach => Reach.Of(Declaration).Within(Owner.Reach);

    /// <summary>Declared <c>mut</c>, so it may be assigned after construction.</summary>
    public bool IsMutable => (Declaration.Modifiers & Modifiers.Mut) != 0;

    public bool IsStatic => (Declaration.Modifiers & Modifiers.Static) != 0;

    public BoundExpression? Initializer { get; set; }

    /// <summary>Whether a constructor must assign it before using <c>this</c> and before returning.</summary>
    public bool RequiresAssignment => !this.HasDefault(TypeMap.Empty);

    public override string ToString() => $"{Owner.Name}.{Name}";
}

/// <summary>
/// An enum variant. Its fields are <see cref="FieldSymbol"/>s owned by the enum, reachable only
/// through patterns. <see cref="Constructor"/> describes creating the variant like a call, with the
/// fields as parameters.
/// </summary>
public sealed class VariantSymbol
{
    public VariantSymbol(string name, TypeSymbol owner, int index, VariantDecl declaration, IReadOnlyList<FieldSymbol> fields)
    {
        Name = name;
        Owner = owner;
        Index = index;
        Declaration = declaration;
        Fields = fields;
        var parameters = fields.Select(f => new LocalSymbol(f.Name, f.Type, isParameter: true)).ToList();
        Constructor = new FunctionSymbol(name, parameters, WylType.Void) { ContainingType = owner, Reach = owner.Reach };
        InferringConstructor = new FunctionSymbol(name, parameters, WylType.Void) { TypeParameters = owner.TypeParameters };
    }

    public string Name { get; }
    public TypeSymbol Owner { get; }
    public int Index { get; }
    public VariantDecl Declaration { get; }
    public IReadOnlyList<FieldSymbol> Fields { get; }

    /// <summary>The integer the variant converts to, for enums without data.</summary>
    public Int128 Value { get; set; }

    public FunctionSymbol Constructor { get; }

    /// <summary>
    /// The constructor of a generic enum as a function generic over the enum's type parameters, for
    /// inferring the type arguments from the fields' values.
    /// </summary>
    public FunctionSymbol InferringConstructor { get; }

    public override string ToString() => $"{Owner.Name}.{Name}";
}

/// <summary>A declared struct, class, interface, or enum.</summary>
public sealed class TypeSymbol
{
    public TypeSymbol(TypeDecl declaration, IReadOnlyList<TypeParameterSymbol> typeParameters)
    {
        Name = declaration.Name;
        DisplayName = declaration.Name;
        Kind = declaration.Kind;
        Declaration = declaration;
        TypeParameters = typeParameters;
        Type = Construct([.. typeParameters.Select(p => p.Type)]);
        DefaultConstructor = new FunctionSymbol(Name, [], WylType.Void)
        {
            ContainingType = this,
            IsConstructor = true,
            IsImplicit = true,
            Reach = Reach.Of(declaration),
        };
    }

    public string Name { get; }

    /// <summary>
    /// How messages and runtime diagnostics name it: <see cref="Name"/>, unless another type of the
    /// program has that name, and then its package and module path (docs/diagnostics/README.md); set by the binder once every type is declared.
    /// </summary>
    public string DisplayName { get; internal set; }

    public TypeKind Kind { get; }
    public TypeDecl Declaration { get; }
    public IReadOnlyList<TypeParameterSymbol> TypeParameters { get; }
    public bool IsGeneric => TypeParameters.Count > 0;
    public bool IsAbstract => (Declaration.Modifiers & Modifiers.Abstract) != 0;
    public bool IsFinal => (Declaration.Modifiers & Modifiers.Final) != 0;
    public Reach Reach => Reach.Of(Declaration);

    /// <summary>A <c>ref struct</c>: second-class, like a <c>ref</c>, so it can only be a local or parameter.</summary>
    public bool IsRefStruct => (Declaration.Modifiers & Modifiers.Ref) != 0;

    /// <summary>A <c>unique struct</c> or <c>unique enum</c>: moved rather than copied.</summary>
    public bool IsUnique => (Declaration.Modifiers & Modifiers.Unique) != 0;

    /// <summary>An <c>extern struct</c> of the standard library, implemented by a C++ class in the runtime.</summary>
    public bool IsExtern => (Declaration.Modifiers & Modifiers.Extern) != 0;

    /// <summary>
    /// The name the C++ runtime declares the type by, which the emitter then gives it: an extern
    /// struct's class, and a standard library type the runtime's code names (<c>ReadOnlySpan</c>, which
    /// <c>String.bytes</c> returns), so the runtime never depends on how the emitter names things.
    /// </summary>
    public string? RuntimeName => IsExtern || IsNamedByRuntime ? "wyl::" + Name : null;

    /// <summary>Set by <see cref="StdlibSymbols.Register"/> for the hooks the runtime refers to.</summary>
    internal bool IsNamedByRuntime { get; set; }

    public bool IsShared { get; internal set; }

    /// <summary>The standard library's weak reference.</summary>
    public bool IsWeak { get; internal set; }

    internal bool IsSendableHandle { get; set; }

    /// <summary>The standard library's <c>Atomic</c>, whose type argument is an integer type (spec <c>concurrency.atomic</c>).</summary>
    public bool IsAtomic { get; internal set; }

    /// <summary>An isolated graph's box, which is sendable for any type argument (spec <c>concurrency.sendable</c>).</summary>
    public bool IsIsolated { get; internal set; }

    /// <summary>A class declared <c>@Acyclic</c>, whose objects the cycle collector never looks at.</summary>
    public bool IsAcyclic => Declaration.Annotations.Any(a => a.Name == "Acyclic");

    /// <summary>Every instantiation constructed so far, including ones with type parameters as arguments.</summary>
    public IReadOnlyCollection<WylType> Instances => NamedType.InstancesOf(this);

    /// <summary>The type of <c>this</c>: the type applied to its own type parameters.</summary>
    public WylType Type { get; }

    public WylType Construct(IReadOnlyList<WylType> typeArguments) => NamedType.Of(this, typeArguments);

    /// <summary>A class's base class, in terms of this type's own type parameters.</summary>
    public WylType? BaseType { get; set; }

    /// <summary>Interfaces implemented by a class or struct, or extended by an interface.</summary>
    public List<WylType> Interfaces { get; } = [];

    /// <summary>
    /// For structs, the constructor every struct has. For classes, the one a class has when it
    /// declares none. It is never in <see cref="Constructors"/>.
    /// </summary>
    public FunctionSymbol DefaultConstructor { get; }

    public List<FieldSymbol> Fields { get; } = [];

    public List<FieldSymbol> StaticFields { get; } = [];

    public List<VariantSymbol> Variants { get; } = [];

    /// <summary>For an enum, whether any variant carries data.</summary>
    public bool HasData => Variants.Any(v => v.Fields.Count > 0);

    /// <summary>The integer type an enum without data converts to; set when declared or values are given.</summary>
    public WylType? UnderlyingType { get; set; }

    /// <summary>The fields a value of this struct or enum holds: a struct's fields, or every variant's fields.</summary>
    public IEnumerable<FieldSymbol> ValueFields => Kind == TypeKind.Enum ? Variants.SelectMany(v => v.Fields) : Fields;

    public VariantSymbol? FindVariant(string name) => Variants.FirstOrDefault(v => v.Name == name);

    /// <summary>
    /// Interface methods this class implements with a method inherited from a base class that does
    /// not itself implement the interface. C++ needs an override in this class to connect them.
    /// </summary>
    public List<MethodReference> InheritedImplementations { get; } = [];
    public List<FunctionSymbol> Constructors { get; } = [];
    public Dictionary<string, List<FunctionSymbol>> Methods { get; } = new();
    public FunctionSymbol? Destructor { get; set; }

    /// <summary>
    /// Whether its destructor body is a method of its own, which the collector runs before releasing
    /// any field of a cycle's objects. The standard library's containers release their elements with
    /// their fields, so their destructor bodies run after every destructor of the cycle
    /// (spec memory.cycles.destroying).
    /// </summary>
    public bool HasSeparateDestructor =>
        Kind == TypeKind.Class && Destructor is not null && !Declaration.Location.Source.Module.Package.IsStd;

    /// <summary>Declared constructors, methods, and the destructor, in source order.</summary>
    public List<FunctionSymbol> Members { get; } = [];

    public FieldSymbol? FindField(string name) => Fields.FirstOrDefault(f => f.Name == name);

    public FieldSymbol? FindStaticField(string name) => StaticFields.FirstOrDefault(f => f.Name == name);

    public override string ToString() => DisplayName;
}

public static class Builtins
{
    internal static readonly WylType[] FormattedValues = [.. WylType.Integers, WylType.Char, WylType.Float, WylType.Double, WylType.Bool, WylType.String];

    /// <summary>The types the runtime writes as text itself (<c>wyl::format</c>), with their nullables.</summary>
    static IEnumerable<WylType> FormattedTypes => FormattedValues.Concat(FormattedValues.Select(v => v.Nullable));

    /// <summary>
    /// The runtime function an interpolated string calls with its parts in order, each a string or
    /// a formatted type, so that it is ordered and analyzed like any call.
    /// </summary>
    public static FunctionSymbol Interpolate(IEnumerable<WylType> partTypes) =>
        new("interpolate", [.. partTypes.Select(t => new LocalSymbol("part", t, isParameter: true))], WylType.String)
        {
            BuiltinCppName = "wyl::Interpolate",
            PassesLocation = true,
        };

    /// <summary>
    /// An interpolated value with a format specifier or alignment (<c>{x,8:F2}</c>): its specifier's
    /// letter (or 0), precision (or -1), and alignment (or 0). The runtime gives back a deferred
    /// <c>String</c> that the interpolation writes in place.
    /// </summary>
    public static FunctionSymbol Formatted(WylType type) =>
        new("formatted", [
            new LocalSymbol("value", type, isParameter: true),
            new LocalSymbol("kind", WylType.Int, isParameter: true),
            new LocalSymbol("precision", WylType.Int, isParameter: true),
            new LocalSymbol("alignment", WylType.Int, isParameter: true),
        ], WylType.String)
        {
            BuiltinCppName = "wyl::MakeFormatted",
            IsPure = true,
        };

    /// <summary>The functions the compiler declares, each with the path of the std module it is a name of.</summary>
    public static IEnumerable<(string[] Module, FunctionSymbol Function)> All
    {
        get
        {
            foreach (var type in FormattedTypes)
            {
                yield return ([], new FunctionSymbol("print", [new LocalSymbol("value", type, isParameter: true)], WylType.Void)
                {
                    BuiltinCppName = "wyl::print",
                    Reach = Reach.Everywhere,
                });
            }

            yield return (["panic"], new FunctionSymbol("panic", [new LocalSymbol("message", WylType.String, isParameter: true)], WylType.Void)
            {
                BuiltinCppName = "wyl::panic::panic",
                Reach = Reach.Everywhere,
                IsNoReturn = true,
                PassesLocation = true,
            });
        }
    }
}
