namespace Wyl.Compiler.Semantics;

/// <summary>
/// Facts about copying a value: which types have copies, and which values are unique types
/// (spec <c>types.copyable</c>, <c>structs.unique</c>).
/// </summary>
public static class TypeCopying
{
    extension(WylType type)
    {
        /// <summary>
        /// Whether a value of the type is copied where a value is stored, passed by value, or
        /// returned; any other value is moved (spec <c>types.copyable</c>). A unique struct or enum
        /// is not copyable, and neither is anything holding one inline. A type parameter is not
        /// copyable until a <c>copyable</c> bound can say so (spec <c>generics.checking</c>): the
        /// declaration's bounds decide, never its type arguments.
        /// </summary>
        public bool IsCopyable =>
            !type.InlineTypes().Any(t => t.IsTypeParameter || t.Symbol is { IsUnique: true });

        /// <summary>
        /// Whether the type is unique: a unique struct or enum, or a fixed array, nullable value, or
        /// struct or enum holding one inline. A type parameter is not, since a declaration cannot
        /// tell what its value would be for every type argument (spec <c>structs.unique</c>).
        /// </summary>
        public bool IsUniqueType => type.InlineTypes().Any(t => t.Symbol is { IsUnique: true });
    }
}
