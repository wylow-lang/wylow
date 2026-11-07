namespace Wyl.Compiler.Semantics;

/// <summary>Facts about default values: which types and fields have one, and what making one involves.</summary>
public static class TypeDefaults
{
    extension(WylType type)
    {
        /// <summary>
        /// Whether a type has a default value. Non-null references do not, and neither do type
        /// parameters, since they could stand for a non-null reference. An enum's default is its first
        /// variant, so it has one only if that variant carries no data.
        /// </summary>
        public bool IsDefaultable => IsDefaultable(type, []);

        /// <summary>The first field without an initializer whose type has no default value.</summary>
        public FieldSymbol? FindNonDefaultableField() => FindNonDefaultableField(type, []);

        /// <summary>
        /// Whether empty storage makes a value of the type, so a field with no initializer may start as
        /// that value. Empty storage makes none for a value that runs a destructor, nor for a struct or
        /// array containing one; a nullable value's is null.
        /// </summary>
        public bool HasInitialValue => HasInitialValue(type, []);

        /// <summary>
        /// Whether the type's default value is more than empty storage. A generic struct's always is,
        /// since its type arguments' may be.
        /// </summary>
        public bool HasDefaultCode =>
            type is FixedArrayType array ? array.Element.HasDefaultCode
            : type.IsTypeParameter || (type.IsStruct && !type.Symbol!.IsExtern
                && (type.Symbol.IsGeneric || type.Symbol.Fields.Any(f => f.Initializer is not null || f.Type.HasDefaultCode) || !type.HasInitialValue));

        /// <summary>
        /// Whether making the type's default value may run code: a struct's field initializers can
        /// call functions, and a type parameter may be such a struct. A default with no code may still
        /// be more than zeroed storage (<c>HasDefaultCode</c>).
        /// </summary>
        public bool DefaultRunsCode =>
            type.IsTypeParameter
            || (type is FixedArrayType array && array.Element.DefaultRunsCode)
            || (type.IsStruct && !type.Symbol!.IsExtern && type.Symbol.Fields.Any(f =>
                f.Initializer is not (null or BoundLiteral or BoundNullLiteral) || (f.Initializer is null && TypeMap.For(type).Apply(f.Type).DefaultRunsCode)));
    }

    extension(FieldSymbol field)
    {
        /// <summary>
        /// Whether a construction may leave the field out: it has an initializer, or its type, with
        /// <paramref name="map"/> applied, has a default value that empty storage makes.
        /// </summary>
        public bool HasDefault(TypeMap map) => MayBeLeftOut(field, map, []);
    }

    static bool IsDefaultable(WylType type, HashSet<WylType> visiting) =>
        type.IsNullable || type.IsPointer
        || (type is FixedArrayType array ? IsDefaultable(array.Element, visiting)
            : type.IsEnum ? type.Symbol!.Variants.FirstOrDefault()?.Fields.Count == 0
            : !type.IsTypeParameter && !type.IsReference && !type.IsFunction && !type.IsNullableReference
              && (!type.IsStruct || FindNonDefaultableField(type, visiting) is null));

    static FieldSymbol? FindNonDefaultableField(WylType structType, HashSet<WylType> visiting)
    {
        // A cycle is already a reported error; don't recurse forever on it.
        if (!visiting.Add(structType))
            return null;
        var map = TypeMap.For(structType);
        return structType.Symbol!.Fields.FirstOrDefault(f => !HasDefaultValue(f, map, visiting));
    }

    // The declared initializer, since binding may not have reached the bound one.
    static bool HasDefaultValue(FieldSymbol field, TypeMap map, HashSet<WylType> visiting) =>
        field.Declaration.Initializer is not null || IsDefaultable(map.Apply(field.Type), visiting);

    static bool MayBeLeftOut(FieldSymbol field, TypeMap map, HashSet<WylType> visiting)
    {
        var type = map.Apply(field.Type);
        return field.Declaration.Initializer is not null || (IsDefaultable(type, visiting) && type.HasInitialValue);
    }

    static bool HasInitialValue(WylType type, HashSet<WylType> visiting)
    {
        if (type.IsNullableValue)
            return true;
        if (type is FixedArrayType array)
            return HasInitialValue(array.Element, visiting);
        if (!type.IsStructOrEnum || type.Symbol!.IsExtern || !visiting.Add(type))
            return true;
        if (type.Symbol.Destructor is not null)
            return false;
        var map = TypeMap.For(type);
        var fields = type.IsEnum ? type.Symbol.Variants.FirstOrDefault()?.Fields ?? [] : type.Symbol.Fields;
        return fields.All(field => HasInitialValue(map.Apply(field.Type), visiting));
    }
}
