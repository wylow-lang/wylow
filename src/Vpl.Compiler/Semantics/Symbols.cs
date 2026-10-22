using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Semantics;

public sealed class LocalSymbol(string name, VplType type, bool isParameter, RefKind refKind = RefKind.None)
{
    public string Name { get; } = name;
    public VplType Type { get; } = type;
    public bool IsParameter { get; } = isParameter;
    public RefKind RefKind { get; } = refKind;

    /// <summary>A compiler-introduced local, which has no source name.</summary>
    public bool IsTemporary { get; init; }

    /// <summary>The variable a for-each loop copies each element into.</summary>
    public bool IsLoopElement { get; init; }

    /// <summary>
    /// A loop element that refers to the element in place rather than copying it, because nothing
    /// in the loop can store over the element; set by <see cref="Analysis.ElementAliasing"/>.
    /// </summary>
    public bool AliasesElement { get; set; }

    /// <summary>
    /// A lambda assigns it, so any call after the lambda is created may change it: it is never
    /// narrowed, and is held while in use like an object's field. Set by <see cref="FlowAnalyzer"/>.
    /// </summary>
    public bool IsAssignedByLambda { get; set; }

    /// <summary>The function or lambda that declares it; null for compiler temporaries, which no other function uses.</summary>
    public FunctionSymbol? Owner { get; set; }

    /// <summary>
    /// Changed after its declaration: assigned, wholly or in part, or used where it could be changed,
    /// such as by 'ref'. An escaping lambda can only capture a local that isn't. Set by the binder.
    /// </summary>
    public bool IsModified { get; set; }

    public override string ToString() => Name;
}

public sealed class FunctionSymbol(string name, IReadOnlyList<LocalSymbol> parameters, VplType returnType)
{
    public string Name { get; } = name;
    public IReadOnlyList<LocalSymbol> Parameters { get; } = parameters;

    /// <summary>Constructors and destructors return <c>void</c>; the construction expression has the type itself.</summary>
    public VplType ReturnType { get; private set; } = returnType;

    /// <summary>Gives a lambda bound before its return type was known the type that generic inference chose.</summary>
    public void InferReturnType(VplType type)
    {
        if (!IsLambda || ReturnType != VplType.Error)
            throw new InvalidOperationException("only a lambda whose return type is being inferred can be given one");
        ReturnType = type;
    }

    public FunctionDecl? Declaration { get; init; }

    /// <summary>The body to bind: the declaration's, or for a destructor, which has no declaration, its own.</summary>
    public BlockStmt? Body { get; init; }

    public IReadOnlyList<TypeParameterSymbol> TypeParameters { get; init; } = [];

    /// <summary>A method returning <c>ref</c> or <c>in</c> returns a reference into its receiver.</summary>
    public RefKind ReturnRefKind { get; init; }

    /// <summary>A get-only property, which is used without parentheses.</summary>
    public bool IsProperty { get; init; }

    public bool IsIndexer => Name == SyntaxFacts.IndexerName;
    public TypeSymbol? ContainingType { get; init; }
    public bool IsStatic { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsPrivate { get; init; }
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

    /// <summary>The parameters and return type of a lambda, whose body is a <see cref="BoundLambda"/>'s.</summary>
    public bool IsLambda { get; init; }

    /// <summary>A lambda converted to an escaping function type, which copies what it captures.</summary>
    public bool IsEscaping { get; init; }

    /// <summary>For a lambda, the function or lambda whose body creates it.</summary>
    public FunctionSymbol? Enclosing { get; init; }

    public string? BuiltinCppName { get; init; }
    public bool IsNoReturn { get; init; }

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

    /// <summary>The methods it overrides, and the interface methods it implements for some class or struct; set by the binder.</summary>
    public List<FunctionSymbol> Overridden { get; } = [];

    /// <summary>Whether a subclass may override it.</summary>
    public bool IsOverridable => (IsVirtual || IsAbstract || IsOverride) && !IsFinal;

    public string Signature
    {
        get
        {
            if (IsProperty)
                return Name;
            var typeParameters = IsGeneric ? $"<{string.Join(", ", TypeParameters.Select(p => p.Name))}>" : "";
            var parameters = string.Join(", ", Parameters.Select(p => (p.Type.NonNull.IsEscaping ? "@Escaping " : "") + RefPrefix(p.RefKind) + p.Type));
            return IsIndexer ? $"this[{parameters}]" : $"{Name}{typeParameters}({parameters})";
        }
    }

    public static string RefPrefix(RefKind kind) => kind switch
    {
        RefKind.Ref => "ref ",
        RefKind.In => "in ",
        _ => "",
    };

    public override string ToString() => Signature;
}

public sealed class TypeParameterSymbol
{
    public TypeParameterSymbol(string name)
    {
        Name = name;
        Type = VplType.ForTypeParameter(this);
    }

    public string Name { get; }
    public VplType Type { get; }

    /// <summary>Interfaces that every type argument must implement.</summary>
    public List<VplType> Bounds { get; } = [];

    public override string ToString() => Name;
}

/// <summary>
/// A function as called: <see cref="Owner"/> is the type it is a member of, with type arguments,
/// and <see cref="TypeArguments"/> are the function's own type arguments.
/// </summary>
public sealed class MethodReference
{
    public MethodReference(FunctionSymbol symbol, VplType? owner, IReadOnlyList<VplType> typeArguments)
    {
        Symbol = symbol;
        Owner = owner;
        TypeArguments = typeArguments;
        Map = (owner is null ? TypeMap.Empty : TypeMap.For(owner)).With(symbol.TypeParameters, typeArguments);
    }

    public FunctionSymbol Symbol { get; }
    public VplType? Owner { get; }
    public IReadOnlyList<VplType> TypeArguments { get; }
    public TypeMap Map { get; }

    public VplType ParameterType(int index) => Map.Apply(Symbol.Parameters[index].Type);
    public RefKind ParameterRefKind(int index) => Symbol.Parameters[index].RefKind;
    public VplType ReturnType => Map.Apply(Symbol.ReturnType);

    public override string ToString() => Symbol.Signature;
}

public sealed class FieldSymbol(string name, VplType type, TypeSymbol owner, FieldDecl declaration)
{
    public string Name { get; } = name;
    public VplType Type { get; } = type;
    public TypeSymbol Owner { get; } = owner;
    public FieldDecl Declaration { get; } = declaration;
    public bool IsPrivate => (Declaration.Modifiers & Modifiers.Private) != 0;

    public BoundExpression? Initializer { get; set; }

    /// <summary>Whether a constructor must assign it before using <c>this</c> and before returning.</summary>
    public bool RequiresAssignment => Declaration.Initializer is null && !Conversions.IsDefaultable(Type);

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
        Constructor = new FunctionSymbol(name, parameters, VplType.Void) { ContainingType = owner };
        InferringConstructor = new FunctionSymbol(name, parameters, VplType.Void) { TypeParameters = owner.TypeParameters };
    }

    public string Name { get; }
    public TypeSymbol Owner { get; }
    public int Index { get; }
    public VariantDecl Declaration { get; }
    public IReadOnlyList<FieldSymbol> Fields { get; }

    /// <summary>The integer the variant converts to, for enums without data.</summary>
    public long Value { get; set; }

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
    readonly Dictionary<IReadOnlyList<VplType>, VplType> instances = new(TypeListComparer.Instance);

    public TypeSymbol(TypeDecl declaration, IReadOnlyList<TypeParameterSymbol> typeParameters)
    {
        Name = declaration.Name;
        Kind = declaration.Kind;
        Declaration = declaration;
        TypeParameters = typeParameters;
        Type = Construct([.. typeParameters.Select(p => p.Type)]);
        DefaultConstructor = new FunctionSymbol(Name, [], VplType.Void)
        {
            ContainingType = this,
            IsConstructor = true,
            IsImplicit = true,
        };
    }

    public string Name { get; }
    public TypeKind Kind { get; }
    public TypeDecl Declaration { get; }
    public IReadOnlyList<TypeParameterSymbol> TypeParameters { get; }
    public bool IsGeneric => TypeParameters.Count > 0;
    public bool IsAbstract => (Declaration.Modifiers & Modifiers.Abstract) != 0;
    public bool IsFinal => (Declaration.Modifiers & Modifiers.Final) != 0;

    /// <summary>A <c>ref struct</c>: second-class, like a <c>ref</c>, so it can only be a local or parameter.</summary>
    public bool IsRefStruct => (Declaration.Modifiers & Modifiers.Ref) != 0;

    /// <summary>An <c>extern struct</c> of the standard library, implemented by a C++ class in the runtime.</summary>
    public bool IsExtern => (Declaration.Modifiers & Modifiers.Extern) != 0;

    /// <summary>A class declared <c>@Acyclic</c>, whose objects the cycle collector never looks at.</summary>
    public bool IsAcyclic => Declaration.Annotations.Any(a => a.Name == "Acyclic");

    /// <summary>Every instantiation constructed so far, including ones with type parameters as arguments.</summary>
    public IReadOnlyCollection<VplType> Instances => instances.Values;

    /// <summary>The type of <c>this</c>: the type applied to its own type parameters.</summary>
    public VplType Type { get; }

    public VplType Construct(IReadOnlyList<VplType> typeArguments)
    {
        if (!instances.TryGetValue(typeArguments, out var type))
            instances[typeArguments] = type = VplType.ForSymbol(this, typeArguments);
        return type;
    }

    /// <summary>A class's base class, in terms of this type's own type parameters.</summary>
    public VplType? BaseType { get; set; }

    /// <summary>Interfaces implemented by a class or struct, or extended by an interface.</summary>
    public List<VplType> Interfaces { get; } = [];

    /// <summary>
    /// For structs, the constructor every struct has. For classes, the one a class has when it
    /// declares none. It is never in <see cref="Constructors"/>.
    /// </summary>
    public FunctionSymbol DefaultConstructor { get; }

    public List<FieldSymbol> Fields { get; } = [];

    public List<VariantSymbol> Variants { get; } = [];

    /// <summary>For an enum, whether any variant carries data.</summary>
    public bool HasData => Variants.Any(v => v.Fields.Count > 0);

    /// <summary>The integer type an enum without data converts to; set when declared or values are given.</summary>
    public VplType? UnderlyingType { get; set; }

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

    /// <summary>Declared constructors, methods, and the destructor, in source order.</summary>
    public List<FunctionSymbol> Members { get; } = [];

    public FieldSymbol? FindField(string name) => Fields.FirstOrDefault(f => f.Name == name);

    public override string ToString() => Name;
}

public static class Builtins
{
    public static IEnumerable<FunctionSymbol> All
    {
        get
        {
            VplType[] values = [VplType.Int, VplType.Long, VplType.Float, VplType.Double, VplType.Bool, VplType.String];
            foreach (var type in values.Concat(values.Select(v => v.Nullable)))
            {
                yield return new FunctionSymbol("print", [new LocalSymbol("value", type, isParameter: true)], VplType.Void)
                {
                    BuiltinCppName = "vpl::print",
                };
            }

            yield return new FunctionSymbol("panic", [new LocalSymbol("message", VplType.String, isParameter: true)], VplType.Void)
            {
                BuiltinCppName = "vpl::panic",
                IsNoReturn = true,
                PassesLocation = true,
            };
        }
    }
}
