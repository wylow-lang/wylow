namespace Wyl.Compiler.Semantics;

public static class TypeSendable
{
    extension(WylType type)
    {
        public bool IsSendable => type.IsSendableIn(null);

        public bool IsSendableIn(FunctionSymbol? function) =>
            type.InlineTypes().All(t => t.IsSendablePartIn(function));

        bool IsSendablePartIn(FunctionSymbol? function) => type switch
        {
            TypeParameterType { Parameter: var parameter } =>
                parameter.IsSendableBound || function?.SendableParameters.Contains(parameter) == true,
            _ when type == WylType.Error => true,
            _ when type == WylType.String => true,
            _ when type.IsReference || type.IsFunction || type.IsPointer => false,
            _ when type.Symbol is { IsExtern: true } => false,
            _ => true,
        };
    }
}
