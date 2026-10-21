using System.Collections.Concurrent;
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
    VplType? pointer;
    ConcurrentDictionary<int, VplType>? fixedArrays;

    VplType(string? name, TypeSymbol? symbol = null, IReadOnlyList<VplType>? typeArguments = null, TypeParameterSymbol? typeParameter = null)
    {
        this.name = name;
        Symbol = symbol;
        TypeArguments = typeArguments ?? [];
        TypeParameter = typeParameter;
        NonNull = this;
    }

    // A nullable reference is still a reference to the same class, so it keeps the symbol; a
    // nullable value is a different type, which must not be mistaken for its struct or enum.
    VplType(VplType nonNull)
    {
        if (nonNull.IsReference)
        {
            Symbol = nonNull.Symbol;
            TypeArguments = nonNull.TypeArguments;
        }
        else
        {
            TypeArguments = [];
        }
        NonNull = nonNull;
    }

    VplType(VplType element, int? length)
    {
        ElementType = element;
        FixedLength = length ?? 0;
        IsPointer = length is null;
        TypeArguments = [];
        NonNull = this;
    }

    internal static VplType ForSymbol(TypeSymbol symbol, IReadOnlyList<VplType> typeArguments) =>
        new(typeArguments.Count == 0 ? symbol.Name : null, symbol, typeArguments);

    internal static VplType ForTypeParameter(TypeParameterSymbol parameter) => new(parameter.Name, typeParameter: parameter);

    // Built on demand: deeply nested generic types share structure, but their names would not.
    public string Name => name ??=
        IsNullable ? NonNull.Name + "?"
        : IsPointer ? ElementType!.Name + "*"
        : IsFixedArray ? ArrayBase.Name + ArraySuffix
        : $"{Symbol!.Name}<{string.Join(", ", TypeArguments)}>";

    // Like C, the outer dimension comes first: `int[4][2]` is four `int[2]`s.
    VplType ArrayBase => IsFixedArray ? ElementType!.ArrayBase : this;
    string ArraySuffix => IsFixedArray ? $"[{FixedLength}]" + ElementType!.ArraySuffix : "";

    /// <summary>The declared struct, class, interface, or enum, also for its nullable form.</summary>
    public TypeSymbol? Symbol { get; }
    public IReadOnlyList<VplType> TypeArguments { get; }
    public TypeParameterSymbol? TypeParameter { get; }

    /// <summary>The type without <c>?</c>; the type itself when it is not nullable.</summary>
    public VplType NonNull { get; }

    /// <summary>The element type of a fixed array <c>T[N]</c> or a pointer <c>T*</c>.</summary>
    public VplType? ElementType { get; }

    /// <summary>The <c>N</c> of a fixed array <c>T[N]</c>.</summary>
    public int FixedLength { get; }

    public bool IsPointer { get; }
    public bool IsFixedArray => ElementType is not null && !IsPointer;

    // The built-in types are shared by compilations running in parallel (as in the test runner),
    // so their derived types must be created once, or two compilations' `int[3]`s would differ.
    public VplType FixedArrayOf(int length) =>
        LazyInitializer.EnsureInitialized(ref fixedArrays).GetOrAdd(length, n => new VplType(this, n));

    public VplType PointerTo => pointer ?? Publish(ref pointer, new VplType(this, null));

    public bool IsNullable => NonNull != this;

    /// <summary>The <c>T?</c> form; see <see cref="CanBeNullable"/>.</summary>
    public VplType Nullable => IsNullable ? this : nullable ?? Publish(ref nullable, new VplType(this));

    /// <summary>
    /// A nullable type that is not a reference: <c>int?</c>, <c>Vec3?</c>, and <c>T?</c>, since
    /// <c>T</c> may be a struct. It is a value with a presence flag, so the value inside it is
    /// destroyed when null is assigned.
    /// </summary>
    public bool IsNullableValue => IsNullable && !NonNull.IsReference;

    /// <summary>Pointers can already be null, and a <c>ref struct</c> can't be stored in a nullable.</summary>
    public bool CanBeNullable => this != Void && this != Null && this != Error && !IsPointer && !IsRefStruct;

    static VplType Publish(ref VplType? field, VplType created) =>
        Interlocked.CompareExchange(ref field, created, null) ?? created;

    public bool IsStruct => Symbol?.Kind == TypeKind.Struct;
    public bool IsClass => Symbol?.Kind == TypeKind.Class;
    public bool IsInterface => Symbol?.Kind == TypeKind.Interface;
    public bool IsEnum => Symbol?.Kind == TypeKind.Enum;
    public bool IsRefStruct => Symbol?.IsRefStruct == true;

    /// <summary>Declared value types, which are stored inline and copied.</summary>
    public bool IsStructOrEnum => IsStruct || IsEnum;

    /// <summary>Values stored inline that may contain other values: structs, enums, and fixed arrays.</summary>
    public bool IsAggregate => IsStructOrEnum || IsFixedArray;

    /// <summary>Class and interface types, nullable or not: values that are references to heap objects.</summary>
    public bool IsReference => IsClass || IsInterface;

    public bool IsTypeParameter => TypeParameter is not null;
    public bool IsInteger => this == Int || this == Long;
    public bool IsFloatingPoint => this == Float || this == Double;
    public bool IsNumeric => IsInteger || IsFloatingPoint;
    public bool ContainsTypeParameters => NonNull.IsTypeParameter || ElementType?.ContainsTypeParameters == true || TypeArguments.Any(a => a.ContainsTypeParameters);

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
        : type.IsPointer ? Apply(type.ElementType!).PointerTo
        : type.IsFixedArray ? Apply(type.ElementType!).FixedArrayOf(type.FixedLength)
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
    /// references, upcasts and adding nullability. A value converts to a nullable of any type it converts
    /// to, and a nullable value to another nullable whose value type it converts to.
    /// </summary>
    public static bool IsImplicit(VplType from, VplType to) =>
        from == to
        || from == VplType.Error
        || to == VplType.Error
        || (from == VplType.Int && (to == VplType.Long || to == VplType.Double))
        || (from == VplType.Float && to == VplType.Double)
        || (from == VplType.Null && (to.IsNullable || to.IsPointer))
        || (to.IsNullableValue && from != VplType.Null && !from.IsReference && IsImplicit(from.NonNull, to.NonNull))
        || (from.IsReference && to.IsReference && (to.IsNullable || !from.IsNullable) && IsSubtype(from.NonNull, to.NonNull));

    /// <summary>Casts: between numbers, and between integers and enums without data.</summary>
    public static bool IsExplicit(VplType from, VplType to) =>
        IsImplicit(from, to)
        || (from.IsNumeric && to.IsNumeric)
        || (from.IsInteger && to.IsEnum && !to.Symbol!.HasData)
        || (from.IsEnum && !from.Symbol!.HasData && to.IsInteger);

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
    /// Whether <c>==</c> is defined: numbers, bools, references (identity), and structs and enums
    /// whose fields all support it.
    /// </summary>
    public static bool SupportsEquality(VplType type) => SupportsEquality(type, []);

    /// <summary>The first field of a struct type, or variant field of an enum type, whose type does not support <c>==</c>.</summary>
    public static FieldSymbol? FindNonEquatableField(VplType structType) => FindNonEquatableField(structType, []);

    static bool SupportsEquality(VplType type, HashSet<VplType> visiting) =>
        type.IsNullableValue ? SupportsEquality(type.NonNull, visiting)
        : type.IsFixedArray ? SupportsEquality(type.ElementType!, visiting)
        : !type.IsStructOrEnum ? type != VplType.String && type != VplType.Void && !type.IsTypeParameter
        : !type.Symbol!.IsExtern && FindNonEquatableField(type, visiting) is null;

    static FieldSymbol? FindNonEquatableField(VplType structType, HashSet<VplType> visiting)
    {
        // A cycle is already a reported error; don't recurse forever on it.
        if (!visiting.Add(structType))
            return null;
        var map = TypeMap.For(structType);
        return structType.Symbol!.ValueFields.FirstOrDefault(f => !SupportsEquality(map.Apply(f.Type), visiting));
    }

    /// <summary>
    /// Whether a type has a default value. Non-null references do not, and neither do type
    /// parameters, since they could stand for a non-null reference. An enum's default is its first
    /// variant, so it has one only if that variant carries no data.
    /// </summary>
    public static bool IsDefaultable(VplType type) => IsDefaultable(type, []);

    /// <summary>The first field without an initializer whose type has no default value.</summary>
    public static FieldSymbol? FindNonDefaultableField(VplType structType) => FindNonDefaultableField(structType, []);

    static bool IsDefaultable(VplType type, HashSet<VplType> visiting) =>
        type.IsNullable || type.IsPointer
        || (type.IsFixedArray ? IsDefaultable(type.ElementType!, visiting)
            : type.IsEnum ? type.Symbol!.Variants.FirstOrDefault()?.Fields.Count == 0
            : !type.IsTypeParameter && !type.IsReference && (!type.IsStruct || FindNonDefaultableField(type, visiting) is null));

    static FieldSymbol? FindNonDefaultableField(VplType structType, HashSet<VplType> visiting)
    {
        if (!visiting.Add(structType))
            return null;
        var map = TypeMap.For(structType);
        return structType.Symbol!.Fields.FirstOrDefault(f =>
            f.Declaration.Initializer is null && !IsDefaultable(map.Apply(f.Type), visiting));
    }
}
