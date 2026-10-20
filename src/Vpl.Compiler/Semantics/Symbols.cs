using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Semantics;

public sealed class LocalSymbol(string name, VplType type, bool isParameter)
{
    public string Name { get; } = name;
    public VplType Type { get; } = type;
    public bool IsParameter { get; } = isParameter;

    public override string ToString() => Name;
}

public sealed class FunctionSymbol(string name, IReadOnlyList<LocalSymbol> parameters, VplType returnType)
{
    public string Name { get; } = name;
    public IReadOnlyList<LocalSymbol> Parameters { get; } = parameters;
    public VplType ReturnType { get; } = returnType;

    public FunctionDecl? Declaration { get; init; }
    public string? BuiltinCppName { get; init; }
    public bool IsNoReturn { get; init; }

    /// <summary>The C++ call gets the call site's source location appended, for panic messages.</summary>
    public bool PassesLocation { get; init; }

    public bool IsBuiltin => BuiltinCppName is not null;

    public string Signature => $"{Name}({string.Join(", ", Parameters.Select(p => p.Type))})";

    public override string ToString() => Signature;
}

public static class Builtins
{
    public static IEnumerable<FunctionSymbol> All
    {
        get
        {
            VplType[] printable = [VplType.Int, VplType.Long, VplType.Float, VplType.Double, VplType.Bool, VplType.String];
            foreach (var type in printable)
            {
                yield return new FunctionSymbol("print", [new LocalSymbol("value", type, isParameter: true)], VplType.Void)
                {
                    BuiltinCppName = "vpl::print",
                };
            }

            yield return new FunctionSymbol("panic", [new LocalSymbol("message", VplType.String, isParameter: true)], VplType.Void)
            {
                BuiltinCppName = "vpl::panic",
                IsNoReturn = true,
                PassesLocation = true,
            };
        }
    }
}
