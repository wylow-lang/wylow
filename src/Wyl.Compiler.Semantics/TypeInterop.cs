namespace Wyl.Compiler.Semantics;

public static class TypeInterop
{
    extension(WylType type)
    {
        /// <summary>
        /// Whether the type's memory representation is the same in Wylow and in C++ (spec
        /// <c>interop.blittable</c>). A <c>@Repr</c> enum is not, although an extern function may
        /// take one as a parameter.
        /// </summary>
        public bool IsBlittable => IsBlittableType(type, []);
    }

    static bool IsBlittableType(WylType type, HashSet<TypeSymbol> visiting) => type switch
    {
        _ when type == WylType.Error => true,
        _ when type == WylType.Bool || type == WylType.Char || type.IsNumeric => true,
        PointerType => true,
        FixedArrayType array => IsBlittableType(array.Element, visiting),
        { Symbol: { Kind: Syntax.TypeKind.Struct } s } when s.IsExtern => !s.IsCppClass && !s.Declaration.IsOpaque,
        { Symbol: { Kind: Syntax.TypeKind.Struct } s } => !s.IsUnique && !s.IsRefStruct && !s.IsGeneric && s.Destructor is null
            && HasBlittableFields(s, visiting),
        _ => false,
    };

    // A struct that contains itself is already an error, and is not blittable.
    static bool HasBlittableFields(TypeSymbol s, HashSet<TypeSymbol> visiting)
    {
        if (!visiting.Add(s))
            return false;
        bool blittable = s.Fields.All(f => IsBlittableType(f.Type, visiting));
        visiting.Remove(s);
        return blittable;
    }
}
