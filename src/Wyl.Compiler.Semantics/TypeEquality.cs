namespace Wyl.Compiler.Semantics;

/// <summary>Which types <c>==</c> is defined for.</summary>
public static class TypeEquality
{
    extension(WylType type)
    {
        /// <summary>
        /// Whether <c>==</c> is defined: numbers, bools, strings (by contents), references (identity),
        /// and structs and enums whose fields all support it.
        /// </summary>
        public bool SupportsEquality => SupportsEquality(type, []);

        /// <summary>The first field of a struct type, or variant field of an enum type, whose type does not support <c>==</c>.</summary>
        public FieldSymbol? FindNonEquatableField() => FindNonEquatableField(type, []);
    }

    static bool SupportsEquality(WylType type, HashSet<WylType> visiting) =>
        type.IsNullableValue ? SupportsEquality(type.NonNull, visiting)
        : type is FixedArrayType array ? SupportsEquality(array.Element, visiting)
        : !type.IsStructOrEnum ? type != WylType.Void && !type.IsTypeParameter && !type.IsFunction && !type.IsNullableReference
        : !type.Symbol!.IsCppClass && !type.Symbol.IsUnion && FindNonEquatableField(type, visiting) is null;

    static FieldSymbol? FindNonEquatableField(WylType structType, HashSet<WylType> visiting)
    {
        // A cycle is already a reported error; don't recurse forever on it.
        if (!visiting.Add(structType))
            return null;
        var map = TypeMap.For(structType);
        return structType.Symbol!.ValueFields.FirstOrDefault(f => !SupportsEquality(map.Apply(f.Type), visiting));
    }
}
