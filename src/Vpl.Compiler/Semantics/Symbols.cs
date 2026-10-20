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
    public bool IsInstanceMethod => ContainingStruct is not null && !IsStatic && !IsConstructor;

    public string Signature =>
        $"{Name}({string.Join(", ", Parameters.Select(p => RefPrefix(p.RefKind) + p.Type))})";

    public static string RefPrefix(RefKind kind) => kind switch
    {
        RefKind.Ref => "ref ",
        RefKind.In => "in ",
        _ => "",
    };

    public override string ToString() => Signature;
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
    public StructSymbol(StructDecl declaration)
    {
        Name = declaration.Name;
        Declaration = declaration;
        Type = VplType.ForStruct(this);
        DefaultConstructor = new FunctionSymbol(Name, [], VplType.Void)
        {
            ContainingStruct = this,
            IsConstructor = true,
            IsImplicit = true,
        };
    }

    public string Name { get; }
    public StructDecl Declaration { get; }
    public VplType Type { get; }
    public FunctionSymbol DefaultConstructor { get; }
    public List<FieldSymbol> Fields { get; } = [];
    public List<FunctionSymbol> Constructors { get; } = [];
    public Dictionary<string, List<FunctionSymbol>> Methods { get; } = new();

    /// <summary>Declared constructors and methods, in source order.</summary>
    public List<FunctionSymbol> Members { get; } = [];

    public FieldSymbol? FindField(string name) => Fields.FirstOrDefault(f => f.Name == name);

    /// <summary>The first field, possibly through nested structs, whose type has no <c>==</c>.</summary>
    public FieldSymbol? FindNonEquatableField() => FindNonEquatableField([]);

    FieldSymbol? FindNonEquatableField(HashSet<StructSymbol> visiting)
    {
        // A cycle is already a reported error; don't recurse forever on it.
        if (!visiting.Add(this))
            return null;
        foreach (var field in Fields)
        {
            bool equatable = field.Type.Struct is { } nested
                ? nested.FindNonEquatableField(visiting) is null
                : Conversions.SupportsEquality(field.Type);
            if (!equatable)
                return field;
        }
        return null;
    }

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
