using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.Interpret;

/// <summary>
/// The size and alignment that C++ gives a type on the 64-bit targets the interpreter models: a
/// blittable type (spec <c>interop.blittable</c>), a nullable value, a fixed array, or a struct of
/// the program whose fields have a layout here. A struct's fields are laid out in order, each at
/// the next offset its alignment allows, and the struct's size is rounded up to its own alignment.
/// If a struct has a destructor, the emitter adds a one-byte flags field after its fields.
/// </summary>
readonly record struct CLayout(long Size, long Alignment)
{
    static readonly CLayout Flags = new(1, 1);

    public static CLayout Of(WylType type)
    {
        if (type.Symbol is { Kind: TypeKind.Struct, IsExtern: false, IsGeneric: false, IsRefStruct: false } program && !type.IsNullable)
            return OfFields([.. program.Fields.Select(f => Of(f.Type)), .. program.Destructor is null ? Array.Empty<CLayout>() : [Flags]], union: false);
        if (type.IsNullableValue && !type.NonNull.IsTypeParameter)
            return OfOptional(type.NonNull);
        if (type is FixedArrayType array)
        {
            var element = Of(array.Element);
            return element with { Size = element.Size * array.Length };
        }
        if (!type.IsBlittable)
            throw new UnsupportedFeature($"the size of '{type}', which is not blittable");
        return type switch
        {
            _ when type == WylType.Bool || type == WylType.SByte || type == WylType.Byte => new(1, 1),
            _ when type == WylType.Short || type == WylType.UShort => new(2, 2),
            _ when type == WylType.Int || type == WylType.UInt || type == WylType.Float || type == WylType.Char => new(4, 4),
            _ when type == WylType.Long || type == WylType.ULong || type == WylType.Double
                || type == WylType.NInt || type == WylType.NUInt || type.IsPointer => new(8, 8),
            { Symbol: { } symbol } => OfFields([.. symbol.Fields.Select(f => Of(f.Type))], symbol.IsUnion),
            _ => throw new InterpreterException($"no layout for the blittable type '{type}'"),
        };
    }

    /// <summary>
    /// <c>wyl::Optional&lt;T&gt;</c>: a union of the value and an empty byte, then a one-byte state,
    /// rounded up to the value's alignment. The runtime specializes <c>Optional&lt;CFunction&lt;F&gt;&gt;</c>
    /// to the C function pointer, so a nullable extern function has a pointer's layout.
    /// </summary>
    static CLayout OfOptional(WylType value)
    {
        if (value.IsExternFunction)
            return new(8, 8);
        var held = Of(value);
        return new(RoundUp(Math.Max(held.Size, 1) + 1, held.Alignment), held.Alignment);
    }

    static CLayout OfFields(IReadOnlyList<CLayout> fields, bool union)
    {
        long size = 0, alignment = 1;
        foreach (var field in fields)
        {
            alignment = Math.Max(alignment, field.Alignment);
            size = union ? Math.Max(size, field.Size) : RoundUp(size, field.Alignment) + field.Size;
        }
        // C++ gives an empty struct one byte, so that two objects have distinct addresses.
        return new(Math.Max(RoundUp(size, alignment), 1), alignment);
    }

    static long RoundUp(long size, long alignment) => (size + alignment - 1) / alignment * alignment;
}
