using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Semantics;

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
        || (from is FunctionType { IsEscaping: true } function && to == function.NonEscaping)
        || IsNumericWidening(from, to)
        || (from == VplType.Null && (to.IsNullable || to.IsPointer || to.IsNullableReference))
        || (from is NullableReferenceType reference && to is NullableReferenceType { Kind: RefKind.In } readOnly && reference.Referenced == readOnly.Referenced)
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
        if (type is TypeParameterType parameter)
            return parameter.Parameter.Bounds;
        return type.BaseType is { } baseType ? [baseType, .. type.Interfaces] : type.Interfaces;
    }

    public static bool IsExactlyRepresentable(Int128 value, VplType floatingType) => floatingType == VplType.Float
        ? (Int128)(float)value == value
        : (Int128)(double)value == value;
}
