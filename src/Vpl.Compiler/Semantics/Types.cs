using System.Numerics;
using System.Runtime.CompilerServices;

namespace Vpl.Compiler.Semantics;

/// <summary>
/// A VPL type. Struct types are interned per type-argument list, so types compare by reference.
/// </summary>
public sealed class VplType
{
    public static readonly VplType Void = new("void");
    public static readonly VplType Bool = new("bool");
    public static readonly VplType Int = new("int");
    public static readonly VplType Long = new("long");
    public static readonly VplType Float = new("float");
    public static readonly VplType Double = new("double");
    public static readonly VplType String = new("String");

    /// <summary>The type of an expression that already produced an error; converts to anything to avoid cascades.</summary>
    public static readonly VplType Error = new("<error>");

    string? name;

    VplType(string? name, StructSymbol? @struct = null, IReadOnlyList<VplType>? typeArguments = null, TypeParameterSymbol? typeParameter = null)
    {
        this.name = name;
        Struct = @struct;
        TypeArguments = typeArguments ?? [];
        TypeParameter = typeParameter;
    }

    internal static VplType ForStruct(StructSymbol symbol, IReadOnlyList<VplType> typeArguments) =>
        new(typeArguments.Count == 0 ? symbol.Name : null, symbol, typeArguments);

    internal static VplType ForTypeParameter(TypeParameterSymbol parameter) => new(parameter.Name, typeParameter: parameter);

    // Built on demand: deeply nested generic types share structure, but their names would not.
    public string Name => name ??= $"{Struct!.Name}<{string.Join(", ", TypeArguments)}>";
    public StructSymbol? Struct { get; }
    public IReadOnlyList<VplType> TypeArguments { get; }
    public TypeParameterSymbol? TypeParameter { get; }

    public bool IsStruct => Struct is not null;
    public bool IsTypeParameter => TypeParameter is not null;
    public bool IsInteger => this == Int || this == Long;
    public bool IsFloatingPoint => this == Float || this == Double;
    public bool IsNumeric => IsInteger || IsFloatingPoint;
    public bool ContainsTypeParameters => IsTypeParameter || TypeArguments.Any(a => a.ContainsTypeParameters);

    public override string ToString() => Name;
}

/// <summary>A substitution of type parameters by types.</summary>
public sealed class TypeMap
{
    public static readonly TypeMap Empty = new([]);

    readonly Dictionary<TypeParameterSymbol, VplType> map;

    TypeMap(Dictionary<TypeParameterSymbol, VplType> map) => this.map = map;

    /// <summary>Maps a generic struct's own type parameters to the arguments of <paramref name="instance"/>.</summary>
    public static TypeMap For(VplType instance) =>
        instance.Struct is { IsGeneric: true } s ? Empty.With(s.TypeParameters, instance.TypeArguments) : Empty;

    public TypeMap With(IReadOnlyList<TypeParameterSymbol> parameters, IReadOnlyList<VplType> arguments)
    {
        if (parameters.Count == 0)
            return this;
        var combined = new Dictionary<TypeParameterSymbol, VplType>(map);
        foreach (var (parameter, argument) in parameters.Zip(arguments))
            combined[parameter] = argument;
        return new TypeMap(combined);
    }

    public VplType Apply(VplType type) =>
        type.TypeParameter is { } parameter ? map.GetValueOrDefault(parameter, type)
        : type.Struct is { } s && type.TypeArguments.Count > 0 ? s.Construct([.. type.TypeArguments.Select(Apply)])
        : type;
}

sealed class TypeListComparer : IEqualityComparer<IReadOnlyList<VplType>>
{
    public static readonly TypeListComparer Instance = new();

    public bool Equals(IReadOnlyList<VplType>? x, IReadOnlyList<VplType>? y) =>
        ReferenceEquals(x, y) || (x is not null && y is not null && x.SequenceEqual(y));

    public int GetHashCode(IReadOnlyList<VplType> list)
    {
        var hash = new HashCode();
        foreach (var type in list)
            hash.Add(RuntimeHelpers.GetHashCode(type));
        return hash.ToHashCode();
    }
}

public static class Conversions
{
    /// <summary>Only conversions that can never lose information are implicit.</summary>
    public static bool IsImplicit(VplType from, VplType to) =>
        from == to
        || from == VplType.Error
        || to == VplType.Error
        || (from == VplType.Int && (to == VplType.Long || to == VplType.Double))
        || (from == VplType.Float && to == VplType.Double);

    public static bool IsExplicit(VplType from, VplType to) =>
        IsImplicit(from, to) || (from.IsNumeric && to.IsNumeric);

    public static bool IsExactlyRepresentable(long value, VplType floatingType) => floatingType == VplType.Float
        ? new BigInteger((float)value) == value
        : new BigInteger((double)value) == value;

    /// <summary>Whether <c>==</c> is defined: numbers, bools, and structs whose fields all support it.</summary>
    public static bool SupportsEquality(VplType type) => SupportsEquality(type, []);

    /// <summary>The first field of a struct type whose type does not support <c>==</c>.</summary>
    public static FieldSymbol? FindNonEquatableField(VplType structType) => FindNonEquatableField(structType, []);

    static bool SupportsEquality(VplType type, HashSet<VplType> visiting) => type.Struct is null
        ? type != VplType.String && type != VplType.Void && !type.IsTypeParameter
        : FindNonEquatableField(type, visiting) is null;

    static FieldSymbol? FindNonEquatableField(VplType structType, HashSet<VplType> visiting)
    {
        // A cycle is already a reported error; don't recurse forever on it.
        if (!visiting.Add(structType))
            return null;
        var map = TypeMap.For(structType);
        return structType.Struct!.Fields.FirstOrDefault(f => !SupportsEquality(map.Apply(f.Type), visiting));
    }

    /// <summary>
    /// Whether a type has a default value. Type parameters do not, since they could stand for a type
    /// that has none (such as a non-null class reference).
    /// </summary>
    public static bool IsDefaultable(VplType type) => IsDefaultable(type, []);

    /// <summary>The first field without an initializer whose type has no default value.</summary>
    public static FieldSymbol? FindNonDefaultableField(VplType structType) => FindNonDefaultableField(structType, []);

    static bool IsDefaultable(VplType type, HashSet<VplType> visiting) =>
        !type.IsTypeParameter && (type.Struct is null || FindNonDefaultableField(type, visiting) is null);

    static FieldSymbol? FindNonDefaultableField(VplType structType, HashSet<VplType> visiting)
    {
        if (!visiting.Add(structType))
            return null;
        var map = TypeMap.For(structType);
        return structType.Struct!.Fields.FirstOrDefault(f =>
            f.Declaration.Initializer is null && !IsDefaultable(map.Apply(f.Type), visiting));
    }
}
