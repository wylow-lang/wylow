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
        WithHiddenParameters(function, DeclaredParameters(function));

    /// <summary>A function's parameters, with a by-value receiver or an extern struct's <c>self</c> first, as its call passes it.</summary>
    IEnumerable<string> DeclaredParameters(FunctionSymbol function)
    {
        var parameters = function.Parameters.Select(p => ParameterDeclaration(function, p, p.Type));
        if (function.ReceiverParameter is { } receiver)
            return [ParameterDeclaration(function, receiver, receiver.Type), .. parameters];
        if (function.IsExternStructBody && function.IsInstanceMethod)
            return [$"{ParameterType(stdlib.ThisType(function.ContainingType!), function.IsReadOnly ? RefKind.In : RefKind.Ref)} {SelfName}", .. parameters];
        return parameters;
    }

    /// <summary>The parameter that <c>this</c> is in a method with a body of an extern struct.</summary>
    const string SelfName = "self";

    /// <summary>The C++ function a method with a body of an extern struct is, named for its Wylow type so that no two collide.</summary>
    string ExternStructFunctionName(FunctionSymbol method) => $"wyl_{QualifiedName(method.ContainingType!)}_{MethodName(method)}";

    /// <summary>
    /// The C++ function a memberwise constructor of a struct of an extern block is in one form, since
    /// the compiler writes no C++ type that it could be a member of.
    /// </summary>
    static string ExternMemberwiseName(TypeSymbol s, IReadOnlyList<bool> form) => $"wyl_{QualifiedName(s)}_{MemberwiseName(form)}";

    /// <summary>
    /// Declares the reached methods with bodies of the extern structs, after every type is complete
    /// and before any function uses one. Then gives <c>wyl::Builtin</c>, through which a call on a
    /// bound reaches a primitive or <c>String</c>, a <c>wyl::MethodsOf</c> that forwards each
    /// interface method such a body implements. Two built-in types may be one C++ type, as <c>nint</c>
    /// and <c>long</c> are on a 64-bit target, so a type whose C++ identity is another type's gets a
    /// constrained partial specialization, which C++ uses only where the two types differ.
    /// </summary>
    void DeclareExternStructMethods(BoundProgram program)
    {
        var reached = program.Functions.Select(f => f.Symbol).Where(f => f.IsExternStructBody && functions.Contains(f)).ToList();
        var memberwise = program.Types.Where(s => s.IsExtern).SelectMany(s => s.Members).Where(m => m.IsMemberwise).ToList();
        if (reached.Count == 0 && memberwise.Count == 0)
            return;
        Line();
        foreach (var method in reached)
            Line($"{ReturnType(method)} {ExternStructFunctionName(method)}({Parameters(method)});");
        foreach (var constructor in memberwise)
        {
            foreach (var form in functions.MemberwiseForms(constructor))
                Line($"{CppType(constructor.ContainingType!.Type)} {ExternMemberwiseName(constructor.ContainingType, form.Builds!)}({MemberwiseParameters(constructor, form.Builds!)});");
        }
        foreach (var owner in reached.Where(m => m.Overridden.Any(o => o.ContainingType is { Kind: TypeKind.Interface })).GroupBy(m => m.ContainingType!))
        {
            var type = stdlib.ThisType(owner.Key);
            Line();
            if (type == type.CppIdentity)
            {
                Line("template <>");
                Line($"struct wyl::MethodsOf<{CppType(type)}>");
            }
            else
            {
                Line("template <class T>");
                Line($"    requires std::is_same_v<T, {CppType(type)}>");
                Line("struct wyl::MethodsOf<T>");
            }
            Line("{");
            foreach (var method in owner)
            {
                foreach (var name in method.Overridden.Where(o => o.ContainingType is { Kind: TypeKind.Interface }).Select(MethodName).Distinct())
                {
                    Line("    template <class... A>");
                    Line($"    static decltype(auto) {name}(A&&... arguments) {{ return {ExternStructFunctionName(method)}(std::forward<A>(arguments)...); }}");
                }
            }
            Line("};");
        }
    }

    string Parameters(MethodReference method) =>
        WithHiddenParameters(method.Symbol, method.Symbol.Parameters.Select((p, i) => ParameterDeclaration(method.Symbol, p, method.ParameterType(i))));

    /// <summary>The parameters with the caller's location, if the function takes it, and the holder its returned reference's hold goes in.</summary>
    static string WithHiddenParameters(FunctionSymbol function, IEnumerable<string> parameters)
    {
        if (function.PassesLocation)
            parameters = parameters.Append($"const char* {CallerLocationParameter}");
        if (function.ReturnsHold)
            parameters = parameters.Append($"wyl::Holder& {HolderParameter}");
        return string.Join(", ", parameters);
    }

    const string HolderParameter = "wyl_holder";

    string MemberDeclaration(FunctionSymbol member)
    {
        var owner = member.ContainingType!;
        if (member.IsConstructor)
        {
            return owner.Kind == TypeKind.Struct
                ? $"static {OwnName(owner)} make({Parameters(member)})"
                : $"{OwnName(owner)}({Parameters(member)})";
        }
        if (member.IsDestructor)
            return owner.Kind == TypeKind.Struct
                ? $"~{OwnName(owner)}()"
                : owner.HasSeparateDestructor ? "void wyl_destructor()" : $"~{OwnName(owner)}() override";
        var signature = $"{ReturnType(member)} {MethodName(member)}({Parameters(member)})";
        if (member.IsStatic || member.HasByValueReceiver)
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
            && Conversions.Supertypes(member.ContainingType!.Type, function: null).Any(t => t.Symbol == iface));

    string DefinitionSignature(FunctionSymbol function)
    {
        if (function.ContainingType is not { } s)
            return $"{CppType(function.ReturnType)} {FunctionName(function)}({Parameters(function)})";
        var owner = ObjectType(s.Type);
        if (function.IsConstructor)
        {
            return s.Kind == TypeKind.Struct
                ? $"{owner} {owner}::make({Parameters(function)})"
                : $"{owner}::{OwnName(s)}({Parameters(function)})";
        }
        if (function.IsDestructor)
            return s.HasSeparateDestructor ? $"void {owner}::wyl_destructor()" : $"{owner}::~{OwnName(s)}()";
        if (function.IsExternStructBody)
            return $"{ReturnType(function)} {ExternStructFunctionName(function)}({Parameters(function)})";
        var signature = $"{ReturnType(function)} {owner}::{MethodName(function)}({Parameters(function)})";
        return function.IsReadOnly && !function.HasByValueReceiver ? signature + " const" : signature;
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
