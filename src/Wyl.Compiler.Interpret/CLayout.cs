using Wyl.Compiler.Semantics;

namespace Wyl.Compiler.Interpret;

/// <summary>
/// The size and alignment that C++ gives a blittable type on the 64-bit targets the interpreter
/// models (spec <c>interop.blittable</c>). A struct's fields are laid out in order, each at the next
/// offset its alignment allows, and the struct's size is rounded up to its own alignment.
/// </summary>
readonly record struct CLayout(long Size, long Alignment)
{
    public static CLayout Of(WylType type)
    {
        if (!type.IsBlittable)
            throw new UnsupportedFeature($"the size of '{type}', which is not blittable");
        return type switch
        {
            _ when type == WylType.Bool || type == WylType.SByte || type == WylType.Byte => new(1, 1),
            _ when type == WylType.Short || type == WylType.UShort => new(2, 2),
            _ when type == WylType.Int || type == WylType.UInt || type == WylType.Float || type == WylType.Char => new(4, 4),
            _ when type == WylType.Long || type == WylType.ULong || type == WylType.Double
                || type == WylType.NInt || type == WylType.NUInt || type.IsPointer => new(8, 8),
            FixedArrayType array => Of(array.Element) with { Size = Of(array.Element).Size * array.Length },
            { Symbol: { } symbol } => OfFields([.. symbol.Fields.Select(f => Of(f.Type))], symbol.IsUnion),
            _ => throw new InterpreterException($"no layout for the blittable type '{type}'"),
        };
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
