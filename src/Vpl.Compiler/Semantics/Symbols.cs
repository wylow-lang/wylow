using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Semantics;

public sealed class LocalSymbol(string name, VplType type, bool isParameter, RefKind refKind = RefKind.None)
{
    public string Name { get; } = name;
    public VplType Type { get; } = type;
    public bool IsParameter { get; } = isParameter;
    public RefKind RefKind { get; } = refKind;

    public override string ToString() => Name;
}

public sealed class FunctionSymbol(string name, IReadOnlyList<LocalSymbol> parameters, VplType returnType)
{
    public string Name { get; } = name;
    public IReadOnlyList<LocalSymbol> Parameters { get; } = parameters;

    /// <summary>Constructors return <c>void</c>; the construction expression has the struct type.</summary>
    public VplType ReturnType { get; } = returnType;

    public FunctionDecl? Declaration { get; init; }
    public IReadOnlyList<TypeParameterSymbol> TypeParameters { get; init; } = [];
    public StructSymbol? ContainingStruct { get; init; }
    public bool IsStatic { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsPrivate { get; init; }
    public bool IsConstructor { get; init; }

    /// <summary>The compiler-provided default constructor.</summary>
    public bool IsImplicit { get; init; }

    public string? BuiltinCppName { get; init; }
    public bool IsNoReturn { get; init; }

    /// <summary>The C++ call gets the call site's source location appended, for panic messages.</summary>
    public bool PassesLocation { get; init; }

    public bool IsBuiltin => BuiltinCppName is not null;
    public bool IsGeneric => TypeParameters.Count > 0;
    public bool IsInstanceMethod => ContainingStruct is not null && !IsStatic && !IsConstructor;

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

    public override string ToString() => Name;
}

/// <summary>
/// A function as called: <see cref="Owner"/> is the struct type it is a member of, with type arguments,
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

public sealed class FieldSymbol(string name, VplType type, StructSymbol owner, FieldDecl declaration)
{
    public string Name { get; } = name;
    public VplType Type { get; } = type;
    public StructSymbol Owner { get; } = owner;
    public FieldDecl Declaration { get; } = declaration;
    public bool IsPrivate => (Declaration.Modifiers & Modifiers.Private) != 0;
    public BoundExpression? Initializer { get; set; }

    public override string ToString() => $"{Owner.Name}.{Name}";
}

public sealed class StructSymbol
{
    readonly Dictionary<IReadOnlyList<VplType>, VplType> instances = new(TypeListComparer.Instance);

    public StructSymbol(StructDecl declaration, IReadOnlyList<TypeParameterSymbol> typeParameters)
    {
        Name = declaration.Name;
        Declaration = declaration;
        TypeParameters = typeParameters;
        Type = Construct([.. typeParameters.Select(p => p.Type)]);
        DefaultConstructor = new FunctionSymbol(Name, [], VplType.Void)
        {
            ContainingStruct = this,
            IsConstructor = true,
            IsImplicit = true,
        };
    }

    public string Name { get; }
    public StructDecl Declaration { get; }
    public IReadOnlyList<TypeParameterSymbol> TypeParameters { get; }
    public bool IsGeneric => TypeParameters.Count > 0;

    /// <summary>The type of <c>this</c>: the struct applied to its own type parameters.</summary>
    public VplType Type { get; }

    public VplType Construct(IReadOnlyList<VplType> typeArguments)
    {
        if (!instances.TryGetValue(typeArguments, out var type))
            instances[typeArguments] = type = VplType.ForStruct(this, typeArguments);
        return type;
    }
    public FunctionSymbol DefaultConstructor { get; }
    public List<FieldSymbol> Fields { get; } = [];
    public List<FunctionSymbol> Constructors { get; } = [];
    public Dictionary<string, List<FunctionSymbol>> Methods { get; } = new();

    /// <summary>Declared constructors and methods, in source order.</summary>
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
