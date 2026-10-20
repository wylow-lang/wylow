using System.Numerics;
using System.Runtime.CompilerServices;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Semantics;

/// <summary>
/// A VPL type. Declared types are interned per type-argument list and each type has one nullable
/// form, so types compare by reference.
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

    /// <summary>The type of the <c>null</c> literal, which converts to every nullable type.</summary>
    public static readonly VplType Null = new("null");

    /// <summary>The type of an expression that already produced an error; converts to anything to avoid cascades.</summary>
    public static readonly VplType Error = new("<error>");

    string? name;
    VplType? nullable;

    VplType(string? name, TypeSymbol? symbol = null, IReadOnlyList<VplType>? typeArguments = null, TypeParameterSymbol? typeParameter = null)
    {
        this.name = name;
        Symbol = symbol;
        TypeArguments = typeArguments ?? [];
        TypeParameter = typeParameter;
        NonNull = this;
    }

    VplType(VplType nonNull)
    {
        Symbol = nonNull.Symbol;
        TypeArguments = nonNull.TypeArguments;
        NonNull = nonNull;
    }

    internal static VplType ForSymbol(TypeSymbol symbol, IReadOnlyList<VplType> typeArguments) =>
        new(typeArguments.Count == 0 ? symbol.Name : null, symbol, typeArguments);

    internal static VplType ForTypeParameter(TypeParameterSymbol parameter) => new(parameter.Name, typeParameter: parameter);

    // Built on demand: deeply nested generic types share structure, but their names would not.
    public string Name => name ??= IsNullable ? NonNull.Name + "?" : $"{Symbol!.Name}<{string.Join(", ", TypeArguments)}>";

    /// <summary>The declared struct, class, or interface, also for its nullable form.</summary>
    public TypeSymbol? Symbol { get; }
    public IReadOnlyList<VplType> TypeArguments { get; }
    public TypeParameterSymbol? TypeParameter { get; }

    /// <summary>The type without <c>?</c>; the type itself when it is not nullable.</summary>
    public VplType NonNull { get; }

    public bool IsNullable => NonNull != this;

    /// <summary>The <c>T?</c> form. Only class and interface types have one.</summary>
    public VplType Nullable => IsNullable ? this : nullable ??= new VplType(this);

    public bool IsStruct => Symbol?.Kind == TypeKind.Struct;
    public bool IsClass => Symbol?.Kind == TypeKind.Class;
    public bool IsInterface => Symbol?.Kind == TypeKind.Interface;

    /// <summary>Class and interface types, nullable or not: values that are references to heap objects.</summary>
    public bool IsReference => IsClass || IsInterface;

    public bool IsTypeParameter => TypeParameter is not null;
    public bool IsInteger => this == Int || this == Long;
    public bool IsFloatingPoint => this == Float || this == Double;
    public bool IsNumeric => IsInteger || IsFloatingPoint;
    public bool ContainsTypeParameters => IsTypeParameter || TypeArguments.Any(a => a.ContainsTypeParameters);

    /// <summary>The base class with this type's type arguments applied.</summary>
    public VplType? BaseType => Symbol?.BaseType is { } baseType ? TypeMap.For(this).Apply(baseType) : null;

    /// <summary>The directly implemented or extended interfaces with this type's type arguments applied.</summary>
    public IEnumerable<VplType> Interfaces => Symbol is { } s ? s.Interfaces.Select(TypeMap.For(this).Apply) : [];

    public override string ToString() => Name;
}

/// <summary>A substitution of type parameters by types.</summary>
public sealed class TypeMap
{
    public static readonly TypeMap Empty = new([]);

    readonly Dictionary<TypeParameterSymbol, VplType> map;

    TypeMap(Dictionary<TypeParameterSymbol, VplType> map) => this.map = map;

    /// <summary>Maps a generic type's own type parameters to the arguments of <paramref name="instance"/>.</summary>
    public static TypeMap For(VplType instance) =>
        instance.Symbol is { IsGeneric: true } s ? Empty.With(s.TypeParameters, instance.TypeArguments) : Empty;

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
        type.IsNullable ? Apply(type.NonNull).Nullable
        : type.TypeParameter is { } parameter ? map.GetValueOrDefault(parameter, type)
        : type.Symbol is { } s && type.TypeArguments.Count > 0 ? s.Construct([.. type.TypeArguments.Select(Apply)])
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
    /// <summary>
    /// Only conversions that can never lose information are implicit: numeric widening, and for
    /// references, upcasts and adding nullability.
    /// </summary>
    public static bool IsImplicit(VplType from, VplType to) =>
        from == to
        || from == VplType.Error
        || to == VplType.Error
        || (from == VplType.Int && (to == VplType.Long || to == VplType.Double))
        || (from == VplType.Float && to == VplType.Double)
        || (from == VplType.Null && to.IsNullable)
        || (from.IsReference && to.IsReference && (to.IsNullable || !from.IsNullable) && IsSubtype(from.NonNull, to.NonNull));

    public static bool IsExplicit(VplType from, VplType to) =>
        IsImplicit(from, to) || (from.IsNumeric && to.IsNumeric);

    /// <summary>
    /// Whether <paramref name="type"/> is <paramref name="target"/> or inherits from or implements it.
    /// A type parameter is a subtype of its bounds. Also used for structs, which may implement
    /// interfaces as generic bounds without converting to them.
    /// </summary>
    public static bool IsSubtype(VplType type, VplType target) => type == target || Supertypes(type).Contains(target);

    /// <summary>Every proper supertype: base classes, then all interfaces, with type arguments applied.</summary>
    public static IEnumerable<VplType> Supertypes(VplType type)
    {
        var seen = new HashSet<VplType>();
        var pending = new Queue<VplType>(DirectSupertypes(type));
        while (pending.TryDequeue(out var next))
        {
            // A cycle is already a reported error; don't loop forever on it.
            if (!seen.Add(next))
                continue;
            yield return next;
            foreach (var super in DirectSupertypes(next))
                pending.Enqueue(super);
        }
    }

    static IEnumerable<VplType> DirectSupertypes(VplType type)
    {
        // `Player?` is no subtype of an interface: a bound method could not be called on null.
        if (type.IsNullable)
            return [];
        if (type.TypeParameter is { } parameter)
            return parameter.Bounds;
        return type.BaseType is { } baseType ? [baseType, .. type.Interfaces] : type.Interfaces;
    }

    public static bool IsExactlyRepresentable(long value, VplType floatingType) => floatingType == VplType.Float
        ? new BigInteger((float)value) == value
        : new BigInteger((double)value) == value;

    /// <summary>
    /// Whether <c>==</c> is defined: numbers, bools, references (identity), and structs whose fields
    /// all support it.
    /// </summary>
    public static bool SupportsEquality(VplType type) => SupportsEquality(type, []);

    /// <summary>The first field of a struct type whose type does not support <c>==</c>.</summary>
    public static FieldSymbol? FindNonEquatableField(VplType structType) => FindNonEquatableField(structType, []);

    static bool SupportsEquality(VplType type, HashSet<VplType> visiting) => !type.IsStruct
        ? type != VplType.String && type != VplType.Void && !type.IsTypeParameter
        : FindNonEquatableField(type, visiting) is null;

    static FieldSymbol? FindNonEquatableField(VplType structType, HashSet<VplType> visiting)
    {
        // A cycle is already a reported error; don't recurse forever on it.
        if (!visiting.Add(structType))
            return null;
        var map = TypeMap.For(structType);
        return structType.Symbol!.Fields.FirstOrDefault(f => !SupportsEquality(map.Apply(f.Type), visiting));
    }

    /// <summary>
    /// Whether a type has a default value. Non-null references do not, and neither do type
    /// parameters, since they could stand for a non-null reference.
    /// </summary>
    public static bool IsDefaultable(VplType type) => IsDefaultable(type, []);

    /// <summary>The first field without an initializer whose type has no default value.</summary>
    public static FieldSymbol? FindNonDefaultableField(VplType structType) => FindNonDefaultableField(structType, []);

    static bool IsDefaultable(VplType type, HashSet<VplType> visiting) =>
        type.IsNullable
        || (!type.IsTypeParameter && !type.IsReference && (!type.IsStruct || FindNonDefaultableField(type, visiting) is null));

    static FieldSymbol? FindNonDefaultableField(VplType structType, HashSet<VplType> visiting)
    {
        if (!visiting.Add(structType))
            return null;
        var map = TypeMap.For(structType);
        return structType.Symbol!.Fields.FirstOrDefault(f =>
            f.Declaration.Initializer is null && !IsDefaultable(map.Apply(f.Type), visiting));
    }
}
