using System.Runtime.CompilerServices;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.Semantics;

/// <summary>
/// A Wylow type. Each kind of type is a subclass (<c>TypeKinds.cs</c>) that only its own factory
/// builds, interning it: a declared type per type-argument list, and every other kind once per
/// the types it is built from, so types compare by reference. What every type can be asked is here;
/// what only one kind has is on that kind.
/// </summary>
public abstract class WylType
{
    public static readonly WylType Void = new Builtin("void");
    public static readonly WylType Bool = new Builtin("bool");
    public static readonly WylType SByte = Integer("sbyte", sbyte.MinValue, sbyte.MaxValue);
    public static readonly WylType Byte = Integer("byte", byte.MinValue, byte.MaxValue);
    public static readonly WylType Short = Integer("short", short.MinValue, short.MaxValue);
    public static readonly WylType UShort = Integer("ushort", ushort.MinValue, ushort.MaxValue);
    public static readonly WylType Int = Integer("int", int.MinValue, int.MaxValue);
    public static readonly WylType UInt = Integer("uint", uint.MinValue, uint.MaxValue);
    public static readonly WylType Long = Integer("long", long.MinValue, long.MaxValue);
    public static readonly WylType ULong = Integer("ulong", ulong.MinValue, ulong.MaxValue);
    public static readonly WylType NInt = Integer("nint", long.MinValue, long.MaxValue, new(int.MinValue, int.MaxValue));
    public static readonly WylType NUInt = Integer("nuint", ulong.MinValue, ulong.MaxValue, new(uint.MinValue, uint.MaxValue));
    public static readonly WylType Float = new Builtin("float");
    public static readonly WylType Double = new Builtin("double");
    public static readonly WylType String = new Builtin("String");

    /// <summary>A Unicode scalar value. It converts to the integer types like one, but is not a number.</summary>
    public static readonly WylType Char = new Builtin("char", new(0, 0x10FFFF));

    public static IReadOnlyList<WylType> Integers { get; } = [SByte, Byte, Short, UShort, Int, UInt, Long, ULong, NInt, NUInt];

    static Builtin Integer(string name, Int128 min, Int128 max, IntegerRange? held = null) => new(name, new(min, max), held);

    /// <summary>The type of the <c>null</c> literal, which converts to every nullable type.</summary>
    public static readonly WylType Null = new Builtin("null");

    /// <summary>The type of an expression that already produced an error; converts to anything to avoid cascades.</summary>
    public static readonly WylType Error = new Builtin("<error>");

    /// <summary>The type of a lambda argument before overload resolution gives it a function type.</summary>
    public static readonly WylType Lambda = new Builtin("lambda");

    private protected WylType() { }

    /// <summary>The types this one is built from, which <see cref="MapParts"/> replaces; empty for a type with none.</summary>
    public abstract IReadOnlyList<WylType> Parts { get; }

    /// <summary>The same kind of type, built from <paramref name="map"/> applied to each of <see cref="Parts"/>.</summary>
    public abstract WylType MapParts(Func<WylType, WylType> map);

    string? builtName;

    // Built on demand: deeply nested generic types share structure, but their names would not.
    public string Name => builtName ??= BuildName();

    private protected abstract string BuildName();

    // A suffix after a function type would belong to its return type.
    private protected static string Grouped(WylType type) => type.IsFunction ? $"({type.Name})" : type.Name;

    private protected static T Publish<T>(ref T? field, T created) where T : class =>
        Interlocked.CompareExchange(ref field, created, null) ?? created;

    /// <summary>The declared struct, class, interface, or enum, also for its nullable form if it is a reference.</summary>
    public virtual TypeSymbol? Symbol => null;
    public virtual IReadOnlyList<WylType> TypeArguments => [];

    /// <summary>The type without <c>?</c>; the type itself when it is not nullable.</summary>
    public virtual WylType NonNull => this;

    /// <summary>
    /// The values of an integer type or <c>char</c> on some target. <c>nint</c> and <c>nuint</c>
    /// have 64-bit ranges here, and only 32-bit ones in <see cref="HeldRange"/>.
    /// </summary>
    public virtual IntegerRange? Range => null;

    /// <summary>The values an integer type or <c>char</c> holds on every target.</summary>
    public virtual IntegerRange? HeldRange => null;

    public bool IsNullable => this is NullableType;
    public bool IsPointer => this is PointerType;
    public bool IsFixedArray => this is FixedArrayType;
    public bool IsNullableReference => this is NullableReferenceType;
    public bool IsTypeParameter => this is TypeParameterType;

    /// <summary>
    /// A function type. Its nullable form is not one: like <c>int?</c>, it is a nullable value
    /// holding a function or nothing.
    /// </summary>
    public bool IsFunction => this is FunctionType;

    /// <summary>The escaping form of a function type; see <see cref="FunctionType"/>.</summary>
    public virtual bool IsEscaping => false;

    public virtual bool IsShared => false;

    public bool IsNonEscapingFunction => this is FunctionType { IsEscaping: false };

    /// <summary>A nullable non-escaping function, which only a parameter passing it on may be.</summary>
    public bool IsNullableNonEscapingFunction => IsNullable && NonNull.IsNonEscapingFunction;

    /// <summary>
    /// A second-class value (spec memory.second-class): a <c>ref struct</c> or a
    /// non-escaping function, nullable or not, which may point into storage it doesn't own, so it
    /// lives only in locals and parameters and keeps what it was first given.
    /// </summary>
    public bool IsSecondClass => IsRefStruct || NonNull.IsNonEscapingFunction;

    /// <summary>
    /// Whether a value of the type may point into storage it doesn't own: a reference, a
    /// second-class value, or a pointer.
    /// </summary>
    public bool MayPointAway => IsNullableReference || IsSecondClass || IsPointer;

    /// <summary>The type a value of this type has where a null check proved it not null.</summary>
    public WylType Narrowed => this is NullableReferenceType r ? r.Referenced : NonNull;

    /// <summary>
    /// The type of what a reference of this type refers to: the <c>T</c> of <c>ref? T</c>, and
    /// otherwise the type itself, as a <c>ref</c> local's type is.
    /// </summary>
    public WylType Referred => this is NullableReferenceType r ? r.Referenced : this;

    /// <summary>A nullable value or reference, or a reference that may refer to nothing.</summary>
    public bool MayBeNull => IsNullable || IsNullableReference;

    /// <summary>
    /// A nullable type that is not a reference: <c>int?</c>, <c>Vec3?</c>, and <c>T?</c>, since
    /// <c>T</c> may be a struct. It is a value with a presence flag, so the value inside it is
    /// destroyed when null is assigned.
    /// </summary>
    public bool IsNullableValue => IsNullable && !NonNull.IsReference;

    /// <summary>
    /// A reference that may be null: <c>T?</c> where <c>T</c> is a class or interface, or a
    /// <c>ref?</c>/<c>in?</c> reference. A <c>NotNull</c> test applies to one, and <c>!</c> makes
    /// one non-null; a nullable value's <c>!</c> checks that it has a value instead.
    /// </summary>
    public bool IsNullableObject => IsNullableReference || (IsNullable && NonNull.IsReference);

    /// <summary>Pointers can already be null, and a <c>ref struct</c> can't be stored in a nullable.</summary>
    public bool CanBeNullable => this != Void && this != Null && this != Error && this != Lambda && !IsPointer && !IsRefStruct && !IsNullableReference;

    /// <summary>The <c>T?</c> form; see <see cref="CanBeNullable"/>. A nullable function keeps its form, escaping or not.</summary>
    public WylType Nullable => IsNullable ? this : NullableType.Of(this);

    public PointerType PointerTo => PointerType.To(this);

    public FixedArrayType FixedArrayOf(int length) => FixedArrayType.Of(this, length);

    /// <summary>The <c>ref? T</c> or <c>in? T</c> referring to this type.</summary>
    public NullableReferenceType NullableReference(RefKind kind) => NullableReferenceType.To(this, kind);

    /// <summary>The non-escaping function type <c>fn(parameters) -> this</c>.</summary>
    public FunctionType FunctionReturning(IReadOnlyList<FunctionParameterType> parameters) => FunctionType.Of(parameters, this);

    /// <summary>
    /// The type with what C++ doesn't tell apart erased, at the top or in any of its parts, so that
    /// whatever is decided per C++ type is decided alike for the types it is. References lose their
    /// nullability: <c>List&lt;Node?&gt;</c> and <c>List&lt;Node&gt;</c> are one C++ type, and
    /// <c>List&lt;int?&gt;</c> another. <c>nint</c> and <c>nuint</c> are <c>long</c> and <c>ulong</c>,
    /// which they are on 64-bit targets; on others, deciding alike for both is what one of them needs.
    /// </summary>
    public WylType CppIdentity =>
        this == NInt ? Long
        : this == NUInt ? ULong
        : this is NullableType { NonNull.IsReference: true } ? NonNull.CppIdentity
        : this is FunctionType { IsShared: true } shared ? ((FunctionType)shared.MapParts(t => t.CppIdentity)).NonShared
        : MapParts(t => t.CppIdentity);

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

    public bool IsInteger => Range is not null && this != Char;
    public bool IsSigned => Range?.Min < 0;

    /// <summary>The bits of an integer type, which a shift count may select, on every target.</summary>
    public int ShiftWidth => HeldRange is { } held ? (int)Int128.Log2(held.Max - held.Min + 1) : throw new InvalidOperationException($"'{this}' is not an integer type");

    public bool IsFloatingPoint => this == Float || this == Double;
    public bool IsNumeric => IsInteger || IsFloatingPoint;
    /// <summary>The type parameters this type is or is built from.</summary>
    public IEnumerable<TypeParameterSymbol> ContainedTypeParameters =>
        this is TypeParameterType parameter ? [parameter.Parameter] : Parts.SelectMany(p => p.ContainedTypeParameters);

    public bool ContainsTypeParameters => ContainedTypeParameters.Any();

    /// <summary>The base class with this type's type arguments applied.</summary>
    public WylType? BaseType => Symbol?.BaseType is { } baseType ? TypeMap.For(this).Apply(baseType) : null;

    /// <summary>The directly implemented or extended interfaces with this type's type arguments applied.</summary>
    public IEnumerable<WylType> Interfaces => Symbol is { } s ? s.Interfaces.Select(TypeMap.For(this).Apply) : [];

    public override string ToString() => Name;

    /// <summary>The types the language defines, which are built from nothing.</summary>
    sealed class Builtin(string name, IntegerRange? range = null, IntegerRange? held = null) : WylType
    {
        public override IntegerRange? Range => range;
        public override IntegerRange? HeldRange => held ?? range;
        public override IReadOnlyList<WylType> Parts => [];
        public override WylType MapParts(Func<WylType, WylType> map) => this;
        private protected override string BuildName() => name;
    }
}

public readonly record struct IntegerRange(Int128 Min, Int128 Max)
{
    public bool Contains(Int128 value) => value >= Min && value <= Max;
    public bool Contains(IntegerRange other) => other.Min >= Min && other.Max <= Max;
}

/// <summary>A substitution of type parameters by types.</summary>
public sealed class TypeMap
{
    public static readonly TypeMap Empty = new([]);

    readonly Dictionary<TypeParameterSymbol, WylType> map;

    TypeMap(Dictionary<TypeParameterSymbol, WylType> map) => this.map = map;

    /// <summary>Maps a generic type's own type parameters to the arguments of <paramref name="instance"/>.</summary>
    public static TypeMap For(WylType instance) =>
        instance.Symbol is { IsGeneric: true } s ? Empty.With(s.TypeParameters, instance.TypeArguments) : Empty;

    public TypeMap With(IReadOnlyList<TypeParameterSymbol> parameters, IReadOnlyList<WylType> arguments)
    {
        if (parameters.Count == 0)
            return this;
        var combined = new Dictionary<TypeParameterSymbol, WylType>(map);
        foreach (var (parameter, argument) in parameters.Zip(arguments))
            combined[parameter] = argument;
        return new TypeMap(combined);
    }

    /// <summary>
    /// This map applied to the types <paramref name="inner"/> maps its parameters to: a callee's
    /// type arguments in the caller's terms, as when generic code runs a generic method with its own
    /// type parameters as the arguments.
    /// </summary>
    public TypeMap Compose(TypeMap inner)
    {
        if (inner.map.Count == 0)
            return this;
        var combined = new Dictionary<TypeParameterSymbol, WylType>(map);
        foreach (var (parameter, type) in inner.map)
            combined[parameter] = Apply(type);
        return new TypeMap(combined);
    }

    public WylType Apply(WylType type) =>
        type is TypeParameterType p ? map.GetValueOrDefault(p.Parameter, type) : type.MapParts(Apply);
}

/// <summary>A parameter of a function type, which has a mode but no name, and prints escaping since its two forms print alike.</summary>
public readonly record struct FunctionParameterType(RefKind RefKind, WylType Type)
{
    public override string ToString() => (Type.NonNull.IsEscaping ? "escaping " : "") + FunctionSymbol.ParameterText(RefKind, Type);
}

sealed class FunctionParameterListComparer : IEqualityComparer<IReadOnlyList<FunctionParameterType>>
{
    public static readonly FunctionParameterListComparer Instance = new();

    public bool Equals(IReadOnlyList<FunctionParameterType>? x, IReadOnlyList<FunctionParameterType>? y) =>
        ReferenceEquals(x, y) || (x is not null && y is not null && x.SequenceEqual(y));

    public int GetHashCode(IReadOnlyList<FunctionParameterType> list)
    {
        var hash = new HashCode();
        foreach (var parameter in list)
        {
            hash.Add(parameter.RefKind);
            hash.Add(RuntimeHelpers.GetHashCode(parameter.Type));
        }
        return hash.ToHashCode();
    }
}

sealed class TypeListComparer : IEqualityComparer<IReadOnlyList<WylType>>
{
    public static readonly TypeListComparer Instance = new();

    public bool Equals(IReadOnlyList<WylType>? x, IReadOnlyList<WylType>? y) =>
        ReferenceEquals(x, y) || (x is not null && y is not null && x.SequenceEqual(y));

    public int GetHashCode(IReadOnlyList<WylType> list)
    {
        var hash = new HashCode();
        foreach (var type in list)
            hash.Add(RuntimeHelpers.GetHashCode(type));
        return hash.ToHashCode();
    }
}
