using System.Collections.Concurrent;
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
    public static readonly VplType SByte = Integer("sbyte", sbyte.MinValue, sbyte.MaxValue);
    public static readonly VplType Byte = Integer("byte", byte.MinValue, byte.MaxValue);
    public static readonly VplType Short = Integer("short", short.MinValue, short.MaxValue);
    public static readonly VplType UShort = Integer("ushort", ushort.MinValue, ushort.MaxValue);
    public static readonly VplType Int = Integer("int", int.MinValue, int.MaxValue);
    public static readonly VplType UInt = Integer("uint", uint.MinValue, uint.MaxValue);
    public static readonly VplType Long = Integer("long", long.MinValue, long.MaxValue);
    public static readonly VplType ULong = Integer("ulong", ulong.MinValue, ulong.MaxValue);
    public static readonly VplType NInt = Integer("nint", long.MinValue, long.MaxValue, new(int.MinValue, int.MaxValue));
    public static readonly VplType NUInt = Integer("nuint", ulong.MinValue, ulong.MaxValue, new(uint.MinValue, uint.MaxValue));
    public static readonly VplType Float = new("float");
    public static readonly VplType Double = new("double");
    public static readonly VplType String = new("String");

    /// <summary>A Unicode scalar value. It converts to the integer types like one, but is not a number.</summary>
    public static readonly VplType Char = new("char") { Range = new(0, 0x10FFFF), HeldRange = new(0, 0x10FFFF) };

    public static IReadOnlyList<VplType> Integers { get; } = [SByte, Byte, Short, UShort, Int, UInt, Long, ULong, NInt, NUInt];

    static VplType Integer(string name, Int128 min, Int128 max, IntegerRange? held = null) =>
        new(name) { Range = new(min, max), HeldRange = held ?? new(min, max) };

    /// <summary>The type of the <c>null</c> literal, which converts to every nullable type.</summary>
    public static readonly VplType Null = new("null");

    /// <summary>The type of an expression that already produced an error; converts to anything to avoid cascades.</summary>
    public static readonly VplType Error = new("<error>");

    /// <summary>The type of a lambda argument before overload resolution gives it a function type.</summary>
    public static readonly VplType Lambda = new("lambda");

    string? name;
    VplType? nullable;
    VplType? pointer;
    VplType? nullableRef;
    VplType? nullableIn;
    ConcurrentDictionary<int, VplType>? fixedArrays;
    ConcurrentDictionary<IReadOnlyList<FunctionParameterType>, VplType>? functionTypes;

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

    VplType(IReadOnlyList<FunctionParameterType> parameters, VplType returnType)
    {
        FunctionParameters = parameters;
        FunctionReturnType = returnType;
        TypeArguments = [];
        NonNull = this;
    }

    VplType(VplType referenced, RefKind kind)
    {
        ReferencedType = referenced;
        ReferenceKind = kind;
        TypeArguments = [];
        NonNull = this;
    }

    // The escaping form of a non-escaping function type, which is the other's twin.
    VplType(VplType nonEscaping, bool escaping)
    {
        FunctionParameters = nonEscaping.FunctionParameters;
        FunctionReturnType = nonEscaping.FunctionReturnType;
        TypeArguments = [];
        NonNull = this;
        IsEscaping = escaping;
        twin = nonEscaping;
    }

    internal static VplType ForSymbol(TypeSymbol symbol, IReadOnlyList<VplType> typeArguments) =>
        new(typeArguments.Count == 0 ? symbol.Name : null, symbol, typeArguments);

    internal static VplType ForTypeParameter(TypeParameterSymbol parameter) => new(parameter.Name, typeParameter: parameter);

    // Built on demand: deeply nested generic types share structure, but their names would not.
    public string Name => name ??=
        IsNullableReference ? $"{FunctionSymbol.RefPrefix(ReferenceKind).Trim()}? {ReferencedType}"
        : IsNullable ? Grouped(NonNull) + "?"
        : IsPointer ? Grouped(ElementType!) + "*"
        : IsFixedArray ? Grouped(ArrayBase) + ArraySuffix
        : IsFunction ? $"({string.Join(", ", FunctionParameters)}) -> {FunctionReturnType}"
        : $"{Symbol!.Name}<{string.Join(", ", TypeArguments)}>";

    // A suffix after a function type would belong to its return type.
    static string Grouped(VplType type) => type.IsFunction ? $"({type.Name})" : type.Name;

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

    /// <summary>
    /// The <c>T</c> of <c>ref? T</c> or <c>in? T</c>, a reference that may refer to nothing. Such a
    /// type is only ever that of a <c>ref</c> or <c>in</c> local, parameter, or return, and of the
    /// expressions reading one; it is not nullable in the sense of <see cref="IsNullable"/>, so no
    /// operation on values accepts it until a null check narrows it to <c>T</c>.
    /// </summary>
    public VplType? ReferencedType { get; }

    /// <summary>Whether a <c>ref? T</c> or an <c>in? T</c>; see <see cref="ReferencedType"/>.</summary>
    public RefKind ReferenceKind { get; }

    public bool IsNullableReference => ReferencedType is not null;

    /// <summary>The <c>ref? T</c> or <c>in? T</c> referring to this type.</summary>
    public VplType NullableReference(RefKind kind) => kind == RefKind.In
        ? nullableIn ?? Publish(ref nullableIn, new VplType(this, RefKind.In))
        : nullableRef ?? Publish(ref nullableRef, new VplType(this, RefKind.Ref));

    /// <summary>The type a value of this type has where a null check proved it not null.</summary>
    public VplType Narrowed => ReferencedType ?? NonNull;

    /// <summary>A nullable value or reference, or a reference that may refer to nothing.</summary>
    public bool MayBeNull => IsNullable || IsNullableReference;

    /// <summary>The parameters of a function type <c>(A, ref B) -> R</c>; empty for other types.</summary>
    public IReadOnlyList<FunctionParameterType> FunctionParameters { get; } = [];

    /// <summary>The <c>R</c> of a function type <c>(A, ref B) -> R</c>.</summary>
    public VplType? FunctionReturnType { get; }

    /// <summary>
    /// A function type. Its nullable form is not one: like <c>int?</c>, it is a nullable value
    /// holding a function or nothing.
    /// </summary>
    public bool IsFunction => FunctionReturnType is not null;

    /// <summary>
    /// The escaping form of a function type, written the same way: a heap closure that may be
    /// stored. The other form may only be called and passed on during the call it was given to.
    /// </summary>
    public bool IsEscaping { get; }

    public bool IsEscapingFunction => IsFunction && IsEscaping;
    public bool IsNonEscapingFunction => IsFunction && !IsEscaping;

    /// <summary>A nullable non-escaping function, which only a parameter passing it on may be.</summary>
    public bool IsNullableNonEscapingFunction => IsNullable && NonNull.IsNonEscapingFunction;

    VplType? twin;

    /// <summary>The escaping form of a function type.</summary>
    public VplType Escaping => IsEscaping ? this : twin ?? Publish(ref twin, new VplType(this, escaping: true));

    /// <summary>The non-escaping form of a function type.</summary>
    public VplType NonEscaping => IsEscaping ? twin! : this;

    public VplType WithEscaping(bool escaping) => escaping ? Escaping : NonEscaping;

    /// <summary>The non-escaping function type <c>(parameters) -> this</c>.</summary>
    public VplType FunctionReturning(IReadOnlyList<FunctionParameterType> parameters) =>
        LazyInitializer.EnsureInitialized(ref functionTypes, () => new(FunctionParameterListComparer.Instance))
            .GetOrAdd(parameters, p => new VplType([.. p], this));

    // The built-in types are shared by compilations running in parallel (as in the test runner),
    // so their derived types must be created once, or two compilations' `int[3]`s would differ.
    public VplType FixedArrayOf(int length) =>
        LazyInitializer.EnsureInitialized(ref fixedArrays).GetOrAdd(length, n => new VplType(this, n));

    public VplType PointerTo => pointer ?? Publish(ref pointer, new VplType(this, null));

    public bool IsNullable => NonNull != this;

    /// <summary>The <c>T?</c> form; see <see cref="CanBeNullable"/>. A nullable function keeps its form, escaping or not.</summary>
    public VplType Nullable => IsNullable ? this : nullable ?? Publish(ref nullable, new VplType(this));

    /// <summary>
    /// The type without the nullability of references, at the top or in its type arguments, which
    /// C++ doesn't see: <c>List&lt;Node?&gt;</c> and <c>List&lt;Node&gt;</c> are one C++ type, so
    /// whatever is decided per C++ type must be decided alike for them. <c>List&lt;int?&gt;</c> is another.
    /// </summary>
    public VplType WithoutReferenceNullability =>
        IsNullable ? (IsNullableValue ? NonNull.WithoutReferenceNullability.Nullable : NonNull.WithoutReferenceNullability)
        : IsPointer ? ElementType!.WithoutReferenceNullability.PointerTo
        : IsFixedArray ? ElementType!.WithoutReferenceNullability.FixedArrayOf(FixedLength)
        : Symbol is { } s && TypeArguments.Count > 0 ? s.Construct([.. TypeArguments.Select(a => a.WithoutReferenceNullability)])
        : this;

    /// <summary>
    /// A nullable type that is not a reference: <c>int?</c>, <c>Vec3?</c>, and <c>T?</c>, since
    /// <c>T</c> may be a struct. It is a value with a presence flag, so the value inside it is
    /// destroyed when null is assigned.
    /// </summary>
    public bool IsNullableValue => IsNullable && !NonNull.IsReference;

    /// <summary>Pointers can already be null, and a <c>ref struct</c> can't be stored in a nullable.</summary>
    public bool CanBeNullable => this != Void && this != Null && this != Error && this != Lambda && !IsPointer && !IsRefStruct && !IsNullableReference;

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

    /// <summary>
    /// The values of an integer type or <c>char</c> on some target. <c>nint</c> and <c>nuint</c>
    /// have 64-bit ranges here, and only 32-bit ones in <see cref="HeldRange"/>.
    /// </summary>
    public IntegerRange? Range { get; private init; }

    /// <summary>The values an integer type or <c>char</c> holds on every target.</summary>
    public IntegerRange? HeldRange { get; private init; }

    public bool IsInteger => Range is not null && this != Char;
    public bool IsSigned => Range?.Min < 0;

    /// <summary>The bits a shift count may select on every target.</summary>
    public int ShiftWidth => this == SByte || this == Byte ? 8 : this == Short || this == UShort ? 16 : this == Long || this == ULong ? 64 : 32;

    public bool IsFloatingPoint => this == Float || this == Double;
    public bool IsNumeric => IsInteger || IsFloatingPoint;
    public bool ContainsTypeParameters => NonNull.IsTypeParameter || ElementType?.ContainsTypeParameters == true || ReferencedType?.ContainsTypeParameters == true
        || TypeArguments.Any(a => a.ContainsTypeParameters)
        || (NonNull.IsFunction && (NonNull.FunctionReturnType!.ContainsTypeParameters || NonNull.FunctionParameters.Any(p => p.Type.ContainsTypeParameters)));

    /// <summary>
    /// The type and the types of the values stored inline in a value of it: a nullable value's
    /// value, a fixed array's elements, and the fields of a struct or enum, with type arguments
    /// applied, recursively. Nullable values themselves are left out, as are the fields of an
    /// extern struct, whose storage is the runtime's.
    /// </summary>
    public IEnumerable<VplType> InlineTypes()
    {
        var seen = new HashSet<VplType>();
        var pending = new Stack<VplType>([this]);
        while (pending.TryPop(out var type))
        {
            if (!seen.Add(type))
                continue;
            if (type.IsNullableValue)
            {
                pending.Push(type.NonNull);
                continue;
            }
            yield return type;
            if (type.IsFixedArray)
                pending.Push(type.ElementType!);
            else if (type.IsStructOrEnum && !type.Symbol!.IsExtern)
            {
                var map = TypeMap.For(type);
                foreach (var field in type.Symbol.ValueFields)
                    pending.Push(map.Apply(field.Type));
            }
        }
    }

    /// <summary>
    /// Whether a value of the type may hold strong references to objects, directly or through a
    /// closure, so that releasing it may destroy one and run its destructor.
    /// </summary>
    public bool MayOwnObjects => InlineTypes().Any(t => t.IsReference || t.IsTypeParameter || t.IsEscaping);

    /// <summary>
    /// Whether copying or destroying a value of the type runs code: it counts references, strong
    /// or weak, or a string's bytes. An extern struct's storage is the runtime's, so it may.
    /// </summary>
    public bool IsCounted => InlineTypes().Any(t => t.IsReference || t.IsTypeParameter || t.IsEscaping || t == String || t.Symbol is { IsExtern: true });

    /// <summary>
    /// Whether the copies of a value share what a reference or view into it points to: an
    /// object's fields, a string's bytes, or a closure's captures. Such a value is held, not
    /// itself kept in place, while something points into it.
    /// </summary>
    public bool SharesStorage => NonNull.IsReference || this == String || NonNull.IsEscapingFunction;

    /// <summary>Whether the copies of a value may share their storage, as a type parameter's may.</summary>
    public bool MayShareStorage => SharesStorage || IsTypeParameter;

    /// <summary>
    /// Whether a value of the type may refer to an object or a closure, so that converting to it
    /// keeps the object's identity; a function value converted to another type refers to the same closure.
    /// </summary>
    public bool KeepsIdentity => NonNull.IsReference || NonNull.IsTypeParameter || NonNull.IsFunction;

    /// <summary>
    /// Whether the type's default value is more than zeroed storage. A generic struct's always is,
    /// since its type arguments' may be.
    /// </summary>
    public bool HasDefaultCode =>
        IsFixedArray ? ElementType!.HasDefaultCode
        : IsTypeParameter || (IsStruct && !Symbol!.IsExtern && (Symbol.IsGeneric || Symbol.Fields.Any(f => f.Initializer is not null || f.Type.HasDefaultCode)));

    /// <summary>
    /// Whether making the type's default value may run code: a struct's field initializers can
    /// call functions, and a type parameter may be such a struct. A default with no code may still
    /// be more than zeroed storage (<see cref="HasDefaultCode"/>).
    /// </summary>
    public bool DefaultRunsCode =>
        IsTypeParameter
        || (IsFixedArray && ElementType!.DefaultRunsCode)
        || (IsStruct && !Symbol!.IsExtern && Symbol.Fields.Any(f =>
            f.Initializer is not (null or BoundLiteral or BoundNullLiteral) || (f.Initializer is null && TypeMap.For(this).Apply(f.Type).DefaultRunsCode)));

    /// <summary>The base class with this type's type arguments applied.</summary>
    public VplType? BaseType => Symbol?.BaseType is { } baseType ? TypeMap.For(this).Apply(baseType) : null;

    /// <summary>The directly implemented or extended interfaces with this type's type arguments applied.</summary>
    public IEnumerable<VplType> Interfaces => Symbol is { } s ? s.Interfaces.Select(TypeMap.For(this).Apply) : [];

    public override string ToString() => Name;
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
        type.IsNullableReference ? Apply(type.ReferencedType!).NullableReference(type.ReferenceKind)
        : type.IsNullable ? Apply(type.NonNull).Nullable
        : type.IsPointer ? Apply(type.ElementType!).PointerTo
        : type.IsFixedArray ? Apply(type.ElementType!).FixedArrayOf(type.FixedLength)
        : type.IsFunction ? Apply(type.FunctionReturnType!).FunctionReturning([.. type.FunctionParameters.Select(p => p with { Type = Apply(p.Type) })]).WithEscaping(type.IsEscaping)
        : type.TypeParameter is { } parameter ? map.GetValueOrDefault(parameter, type)
        : type.Symbol is { } s && type.TypeArguments.Count > 0 ? s.Construct([.. type.TypeArguments.Select(Apply)])
        : type;
}

/// <summary>A parameter of a function type, which has a mode but no name.</summary>
/// <summary>A parameter of a function type, which prints @Escaping since its two forms print alike.</summary>
public readonly record struct FunctionParameterType(RefKind RefKind, VplType Type)
{
    public override string ToString() => (Type.NonNull.IsEscaping ? "@Escaping " : "") + FunctionSymbol.ParameterText(RefKind, Type);
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
    /// to, and a nullable value to another nullable whose value type it converts to. An escaping
    /// function converts to its non-escaping form, and a <c>ref? T</c> to an <c>in? T</c>.
    /// </summary>
    public static bool IsImplicit(VplType from, VplType to) =>
        from == to
        || from == VplType.Error
        || to == VplType.Error
        || (from.IsEscaping && to == from.NonEscaping)
        || IsNumericWidening(from, to)
        || (from == VplType.Null && (to.IsNullable || to.IsPointer || to.IsNullableReference))
        || (from.IsNullableReference && to.IsNullableReference && to.ReferenceKind == RefKind.In && from.ReferencedType == to.ReferencedType)
        || (to.IsNullableValue && from != VplType.Null && !from.IsReference && IsImplicit(from.NonNull, to.NonNull))
        || (from.IsReference && to.IsReference && (to.IsNullable || !from.IsNullable) && IsSubtype(from.NonNull, to.NonNull));

    /// <summary>
    /// An integer or <c>char</c> converts to an integer type that holds all its values on every
    /// target, and an integer to a floating-point type that represents all of them exactly.
    /// </summary>
    static bool IsNumericWidening(VplType from, VplType to) =>
        (from == VplType.Float && to == VplType.Double)
        || (from.Range is { } range && (to.IsInteger ? to.HeldRange!.Value.Contains(range)
            : from.IsInteger && to.IsFloatingPoint && ExactIntegers(to).Contains(range)));

    static IntegerRange ExactIntegers(VplType floatingType) =>
        floatingType == VplType.Float ? new(-(1 << 24), 1 << 24) : new(-(1L << 53), 1L << 53);

    /// <summary>Casts: between numbers, between integers and <c>char</c>, and between integers and enums without data.</summary>
    public static bool IsExplicit(VplType from, VplType to) =>
        IsImplicit(from, to)
        || (from.IsNumeric && to.IsNumeric)
        || (from.IsInteger && to == VplType.Char)
        || (from == VplType.Char && to.IsInteger)
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

    public static bool IsExactlyRepresentable(Int128 value, VplType floatingType) => floatingType == VplType.Float
        ? (Int128)(float)value == value
        : (Int128)(double)value == value;

    /// <summary>
    /// Whether <c>==</c> is defined: numbers, bools, strings (by contents), references (identity),
    /// and structs and enums whose fields all support it.
    /// </summary>
    public static bool SupportsEquality(VplType type) => SupportsEquality(type, []);

    /// <summary>The first field of a struct type, or variant field of an enum type, whose type does not support <c>==</c>.</summary>
    public static FieldSymbol? FindNonEquatableField(VplType structType) => FindNonEquatableField(structType, []);

    static bool SupportsEquality(VplType type, HashSet<VplType> visiting) =>
        type.IsNullableValue ? SupportsEquality(type.NonNull, visiting)
        : type.IsFixedArray ? SupportsEquality(type.ElementType!, visiting)
        : !type.IsStructOrEnum ? type != VplType.Void && !type.IsTypeParameter && !type.IsFunction && !type.IsNullableReference
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
            : !type.IsTypeParameter && !type.IsReference && !type.IsFunction && !type.IsNullableReference
              && (!type.IsStruct || FindNonDefaultableField(type, visiting) is null));

    static FieldSymbol? FindNonDefaultableField(VplType structType, HashSet<VplType> visiting)
    {
        if (!visiting.Add(structType))
            return null;
        var map = TypeMap.For(structType);
        return structType.Symbol!.Fields.FirstOrDefault(f =>
            f.Declaration.Initializer is null && !IsDefaultable(map.Apply(f.Type), visiting));
    }
}
