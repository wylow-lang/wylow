namespace Wyl.Compiler.Semantics;

public static class TypeSendable
{
    extension(WylType type)
    {
        public bool IsSendableIn(FunctionSymbol? function) =>
            type.Symbol is { IsIsolated: true } || type.InlineTypes().All(t => t.IsSendablePartIn(function));

        bool IsSendablePartIn(FunctionSymbol? function) => type switch
        {
            TypeParameterType { Parameter: var parameter } => parameter.IsSendableIn(function),
            _ when type == WylType.Error => true,
            _ when type == WylType.String => true,
            _ when type.IsWeakOfSharedClass => true,
            _ when type.Symbol is { IsSendableHandle: true } => true,
            _ when type.IsReference => type.Symbol is { IsShared: true },
            _ when type.IsFunction => type is FunctionType { IsEscaping: true, IsShared: true },
            _ when type.IsPointer => false,
            _ when type.Symbol is { IsExtern: true } => false,
            _ => true,
        };

        internal bool IsWeakOfSharedClass =>
            type.Symbol is { IsWeak: true }
            && type.TypeArguments is [{ IsReference: true } target]
            && target.Symbol is { IsShared: true };

        /// <summary>The first field, or variant field, whose type is not sendable.</summary>
        public FieldSymbol? FindNonSendableField(FunctionSymbol? function) => FindNonSendableField(type, function, []);
    }

    static FieldSymbol? FindNonSendableField(WylType type, FunctionSymbol? function, HashSet<WylType> visiting)
    {
        if (type.Symbol is { IsIsolated: true } || !type.IsStructOrEnum || !visiting.Add(type))
            return null;
        var map = TypeMap.For(type);
        foreach (var field in type.Symbol!.ValueFields)
        {
            var fieldType = map.Apply(field.Type);
            if (!fieldType.IsSendableIn(function))
                return field;
            if (FindNonSendableField(fieldType, function, visiting) is { } nested)
                return nested;
        }
        return null;
    }
}
