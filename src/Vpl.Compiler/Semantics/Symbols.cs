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

    public override string ToString() => Name;
}

public sealed class FunctionSymbol(string name, IReadOnlyList<LocalSymbol> parameters, VplType returnType)
{
    public string Name { get; } = name;
    public IReadOnlyList<LocalSymbol> Parameters { get; } = parameters;

    /// <summary>Constructors and destructors return <c>void</c>; the construction expression has the type itself.</summary>
    public VplType ReturnType { get; } = returnType;

    public FunctionDecl? Declaration { get; init; }

    /// <summary>The body to bind: the declaration's, or for a destructor, which has no declaration, its own.</summary>
    public BlockStmt? Body { get; init; }

    public IReadOnlyList<TypeParameterSymbol> TypeParameters { get; init; } = [];
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

    /// <summary>The compiler-provided default constructor.</summary>
    public bool IsImplicit { get; init; }

    public string? BuiltinCppName { get; init; }
    public bool IsNoReturn { get; init; }

    /// <summary>The C++ call gets the call site's source location appended, for panic messages.</summary>
    public bool PassesLocation { get; init; }

    public bool IsBuiltin => BuiltinCppName is not null;
    public bool IsGeneric => TypeParameters.Count > 0;
    public bool IsInstanceMethod => ContainingType is not null && !IsStatic && !IsConstructor && !IsDestructor;

    /// <summary>Whether a call dispatches on the receiver's runtime type. Interface methods always do.</summary>
    public bool IsDispatched => IsVirtual || IsOverride || IsAbstract || ContainingType?.Kind == TypeKind.Interface;

    /// <summary>Whether a subclass may override it.</summary>
    public bool IsOverridable => (IsVirtual || IsAbstract || IsOverride) && !IsFinal;

    public string Signature
    {
        get
        {
            var typeParameters = IsGeneric ? $"<{string.Join(", ", TypeParameters.Select(p => p.Name))}>" : "";
            return $"{Name}{typeParameters}({string.Join(", ", Parameters.Select(p => RefPrefix(p.RefKind) + p.Type))})";
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

    /// <summary>A <c>weak</c> field does not keep its target alive and reads as null once the target is destroyed.</summary>
    public bool IsWeak => (Declaration.Modifiers & Modifiers.Weak) != 0;

    public BoundExpression? Initializer { get; set; }

    /// <summary>Whether a constructor must assign it before using <c>this</c> and before returning.</summary>
    public bool RequiresAssignment => Declaration.Initializer is null && !IsWeak && !Conversions.IsDefaultable(Type);

    public override string ToString() => $"{Owner.Name}.{Name}";
}

/// <summary>A declared struct, class, or interface.</summary>
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
            VplType[] printable = [VplType.Int, VplType.Long, VplType.Float, VplType.Double, VplType.Bool, VplType.String];
            foreach (var type in printable)
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
