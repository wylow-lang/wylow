namespace Vpl.Compiler.Semantics;

/// <summary>Which types <c>==</c> is defined for.</summary>
public static class TypeEquality
{
    extension(VplType type)
    {
        /// <summary>
        /// Whether <c>==</c> is defined: numbers, bools, strings (by contents), references (identity),
        /// and structs and enums whose fields all support it.
        /// </summary>
        public bool SupportsEquality => SupportsEquality(type, []);

        /// <summary>The first field of a struct type, or variant field of an enum type, whose type does not support <c>==</c>.</summary>
        public FieldSymbol? FindNonEquatableField() => FindNonEquatableField(type, []);
    }

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
}
