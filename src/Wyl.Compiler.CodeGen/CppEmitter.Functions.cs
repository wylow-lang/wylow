using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.CodeGen;

public sealed partial class CppEmitter
{
    static string ParameterType(WylType type, RefKind refKind) => refKind switch
    {
        _ when type.IsNullableReference => CppType(type),
        RefKind.Ref => CppType(type) + "&",
        RefKind.In => ConstReferenceType(type),
        _ => CppType(type),
    };

    static string ReturnType(FunctionSymbol function) => ParameterType(function.ReturnType, function.ReturnRefKind);

    string Parameters(FunctionSymbol function) =>
        WithLocationParameter(function, function.Parameters.Select(p => ParameterDeclaration(function, p, p.Type)));

    string Parameters(MethodReference method) =>
        WithLocationParameter(method.Symbol, method.Symbol.Parameters.Select((p, i) => ParameterDeclaration(method.Symbol, p, method.ParameterType(i))));

    static string WithLocationParameter(FunctionSymbol function, IEnumerable<string> parameters) =>
        string.Join(", ", function.PassesLocation ? parameters.Append($"const char* {CallerLocationParameter}") : parameters);

    string MemberDeclaration(FunctionSymbol member)
    {
        var owner = member.ContainingType!;
        if (member.IsConstructor)
        {
            return owner.Kind == TypeKind.Struct
                ? $"static {TypeName(owner)} make({Parameters(member)})"
                : $"{TypeName(owner)}({Parameters(member)})";
        }
        if (member.IsDestructor)
            return owner.HasSeparateDestructor ? "void wyl_destructor()" : $"~{TypeName(owner)}() override";
        var signature = $"{ReturnType(member)} {MethodName(member)}({Parameters(member)})";
        if (member.IsStatic)
            return "static " + signature;
        if (OverridesInCpp(member))
            return signature + (member.IsFinal ? " override final" : " override");
        if (member.IsVirtual && !unoverridden.Contains(member))
            return "virtual " + signature;
        return signature + (member.IsReadOnly ? " const" : "");
    }

    /// <summary>
    /// Whether the C++ method overrides a base's: an interface method's implementation does when its
    /// own class implements that interface, while one inherited for it is reached through a forwarder.
    /// </summary>
    static bool OverridesInCpp(FunctionSymbol member) =>
        member.IsOverride
        || member.ImplementsInterface && member.Overridden.Any(o => o.ContainingType is { Kind: TypeKind.Interface } iface
            && Conversions.Supertypes(member.ContainingType!.Type).Any(t => t.Symbol == iface));

    string DefinitionSignature(FunctionSymbol function)
    {
        if (function.ContainingType is not { } s)
            return $"{CppType(function.ReturnType)} {FunctionName(function)}({Parameters(function)})";
        var owner = ObjectType(s.Type);
        if (function.IsConstructor)
        {
            return s.Kind == TypeKind.Struct
                ? $"{owner} {owner}::make({Parameters(function)})"
                : $"{owner}::{TypeName(s)}({Parameters(function)})";
        }
        if (function.IsDestructor)
            return s.HasSeparateDestructor ? $"void {owner}::wyl_destructor()" : $"{owner}::~{TypeName(s)}()";
        var signature = $"{ReturnType(function)} {owner}::{MethodName(function)}({Parameters(function)})";
        return function.IsReadOnly ? signature + " const" : signature;
    }

    void EmitFunction(BoundFunction function)
    {
        var symbol = function.Symbol;
        if (symbol.ContainingType is { IsGeneric: true } owner)
            Line(TemplatePrefix(owner.TypeParameters).TrimEnd());
        if (FunctionTemplatePrefix(symbol) is { Length: > 0 } prefix)
            Line(prefix.TrimEnd());
        Line(DefinitionSignature(symbol));
        EmitIrFunction(functions.Single(symbol)!);
    }
}
