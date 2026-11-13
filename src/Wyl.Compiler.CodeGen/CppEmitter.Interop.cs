using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.CodeGen;

/// <summary>
/// The lines of generated C++ that come from one extern declaration of a block outside the standard
/// library, so that the build can report a C++ error on those lines at the declaration. The
/// <paramref name="Symbol"/> of a function or constant is the qualified C++ name that the program
/// links to, so that the build can report a link that does not find it.
/// </summary>
public sealed record InteropRegion(int FirstLine, int LastLine, Location Declaration, string What, string? Symbol = null);

/// <summary>A <c>static_assert</c> of the boundary: the diagnostic its failure is, and the index of the region it is in.</summary>
public sealed record InteropCheck(Diagnostic Diagnostic, int Region);

/// <summary>
/// What the build needs to report a header that does not match the program's extern blocks: the
/// diagnostic of each <c>static_assert</c>, whose message is <see cref="CheckMessage"/> of its index,
/// and the lines that each extern declaration produced.
/// </summary>
public sealed record CppInterop(IReadOnlyList<InteropCheck> Checks, IReadOnlyList<InteropRegion> Regions)
{
    public const string CheckPrefix = "wyl-interop-check ";

    public static string CheckMessage(int index) => CheckPrefix + index;

    public static readonly CppInterop None = new([], []);
}

public sealed partial class CppEmitter
{
    readonly List<InteropCheck> interopChecks = [];
    readonly List<InteropRegion> interopRegions = [];
    readonly Dictionary<FunctionSymbol, string> externWrappers = [];

    /// <summary>The checks and regions of the last <see cref="Emit"/>.</summary>
    public CppInterop Interop => new([.. interopChecks], [.. interopRegions]);

    /// <summary>
    /// The boundary with the C++ that a program's extern blocks declare (spec <c>interop</c>): a
    /// layout check for each struct and union with fields, and for each function and constant a
    /// signature check and the <c>noexcept</c> wrapper that every call goes through. The checks and
    /// the wrappers come after every type of the program is complete, since a wrapper may take one.
    /// </summary>
    void EmitInterop(BoundProgram program)
    {
        var types = program.Types.Where(s => s.Declaration.Block is { IsStd: false } && s.Fields.Count > 0).ToList();
        var externs = program.FreeFunctions.Concat(program.ExternConstants).Concat(program.Types.SelectMany(s => s.Members))
            .Where(f => f.IsProgramExtern).ToList();
        if (types.Count == 0 && externs.Count == 0)
            return;
        foreach (var s in types)
        {
            InRegion(s.Declaration.NameLocation, $"{KindOf(s)} '{s.Name}'", () =>
            {
                EmitLayoutCheck(s);
                if (s.Type.SupportsEquality)
                    EmitExternEquality(s);
            });
        }
        foreach (var function in externs)
        {
            var what = function.IsExternConstant ? $"constant '{function.Name}'" : $"extern function '{function.Name}'";
            InRegion(function.Declaration!.NameLocation, what, () =>
            {
                if (function.IsExternConstant)
                    EmitExternConstant(function);
                else
                    EmitExternFunction(function);
            }, symbol: QualifiedCppName(function));
        }
    }

    void InRegion(Location declaration, string what, Action emit, bool separate = true, string? symbol = null)
    {
        if (separate)
            Line();
        int first = lineCount + 1;
        emit();
        interopRegions.Add(new InteropRegion(first, lineCount, declaration, what, symbol));
    }

    static string KindOf(TypeSymbol s) => s.IsUnion ? "union" : "struct";

    /// <summary>A <c>static_assert</c> whose failure the build reports as <paramref name="diagnostic"/>.</summary>
    void StaticAssert(string condition, Diagnostic diagnostic)
    {
        Line($"static_assert({condition}, \"{CppInterop.CheckMessage(interopChecks.Count)}\");");
        interopChecks.Add(new InteropCheck(diagnostic, interopRegions.Count));
    }

    /// <summary>
    /// Compares a struct's or union's C++ type with one that has the declared fields in their order:
    /// the size and alignment, and each field's offset and size (spec <c>interop.blittable.layout</c>).
    /// </summary>
    void EmitLayoutCheck(TypeSymbol s)
    {
        var cppName = s.CppName!;
        var kind = KindOf(s);
        var declared = $"wyl_layout_{interopRegions.Count}";
        Line($"{kind} {declared} {{ {string.Concat(s.Fields.Select((f, i) => $"{CppType(f.Type)} f{i}; "))}}};");
        StaticAssert($"sizeof({cppName}) == sizeof({declared}) && alignof({cppName}) == alignof({declared})",
            Catalog.LayoutMismatch(s.Declaration.NameLocation, "the size or alignment", kind, s.Name, cppName));
        for (int i = 0; i < s.Fields.Count; i++)
        {
            var field = s.Fields[i];
            StaticAssert($"offsetof({cppName}, {field.CppName}) == offsetof({declared}, f{i}) && sizeof({cppName}::{field.CppName}) == sizeof({CppType(field.Type)})",
                Catalog.LayoutMismatch(field.Declaration.NameLocation, $"field '{field.Name}'", kind, s.Name, cppName));
        }
    }

    /// <summary>
    /// <c>==</c> on a struct of an extern block, which compares its fields as <c>==</c> on any struct
    /// does (spec <c>structs.equality</c>). The operator is in the C++ type's namespace, where C++ finds
    /// it from any comparison of two values of the type.
    /// </summary>
    void EmitExternEquality(TypeSymbol s)
    {
        var cppName = s.CppName!;
        int separator = cppName.LastIndexOf("::", StringComparison.Ordinal);
        var space = separator < 0 ? null : cppName[..separator].TrimStart(':');
        var compared = s.Fields.Select(f => f.Type.IsFixedArray
            ? $"std::equal(std::begin(a.{f.CppName}), std::end(a.{f.CppName}), std::begin(b.{f.CppName}))"
            : $"a.{f.CppName} == b.{f.CppName}");
        var function = $"inline bool operator==(const {cppName}& a, const {cppName}& b) {{ return {string.Join(" && ", compared)}; }}";
        Line(string.IsNullOrEmpty(space) ? function : $"namespace {space} {{ {function} }}");
    }

    /// <summary>
    /// The signature check and the wrapper of an extern function. C++ decides whether the function's
    /// name has one declaration (<c>wyl::interop::Address</c>). If it has, the check compares the
    /// signature, and the wrapper converts each argument to the C++ parameter's type. If the name has
    /// several, the check passes, and the wrapper passes the arguments unchanged, so the C++ compiler
    /// picks the declaration that their types select (spec <c>interop.signatures</c>).
    /// </summary>
    void EmitExternFunction(FunctionSymbol function)
    {
        int index = interopRegions.Count;
        var one = $"wyl_one_{index}";
        var check = $"wyl_check_{index}";
        var wrapper = externWrappers[function] = $"wyl_extern_{index}";
        bool instance = function.IsInstanceMethod;
        var cppName = function.BuiltinCppName!;
        var owner = function.ContainingType?.CppName;
        var address = instance ? $"wyl::interop::MemberAddress<D>(&{owner}::{cppName})" : $"wyl::interop::Address<D>({cppName})";
        var returned = function.ReturnedInteger ?? function.ReturnType;
        var result = returned == WylType.Void ? "void" : CppType(returned);
        var declared = function.Parameters.Select(p => BoundaryCppType(BoundaryType(p.Type))).Prepend(returned == WylType.Void ? "void" : BoundaryCppType(returned));

        // A using-declaration names every declaration of a free function, so a name that C++ does not
        // declare is an error here with every compiler, whether or not anything calls it.
        if (!instance)
            Line($"namespace wyl_name_{index} {{ using {(cppName.Contains("::") ? "" : "::")}{cppName}; }}");
        Line($"template <class D> concept {one} = requires {{ {address}; }};");
        Line($"template <class D = void> constexpr bool {check}(int part)");
        Line("{");
        Line($"    if constexpr ({one}<D>)");
        Line($"        return wyl::interop::SignaturePart<decltype({address}), {string.Join(", ", declared)}>(part);");
        Line("    else");
        Line("        return true;");
        Line("}");
        var at = function.Declaration!.NameLocation;
        StaticAssert($"{check}(wyl::interop::ARITY)", Catalog.SignatureMismatch(at, function.Name, QualifiedCppName(function), "the number of parameters"));
        StaticAssert($"{check}(wyl::interop::RESULT)", Catalog.SignatureMismatch(at, function.Name, QualifiedCppName(function), $"return type '{function.ReturnType}'"));
        for (int i = 0; i < function.Parameters.Count; i++)
        {
            var parameter = function.Parameters[i];
            var location = function.Declaration.Parameters.Count > i ? function.Declaration.Parameters[i].Location : at;
            StaticAssert($"{check}({i})", Catalog.SignatureMismatch(location, function.Name, QualifiedCppName(function), $"type '{parameter.Type}' of parameter '{parameter.Name}'"));
        }

        var parameters = function.Parameters.Select((p, i) => $"{CppType(p.Type)} a{i}");
        if (instance)
            parameters = parameters.Prepend($"{(function.IsReadOnly ? "const " : "")}{owner}& self");
        var target = instance ? $"self.{cppName}" : cppName;
        var values = function.Parameters.Select((p, i) => p.Type.Symbol is { HasRepr: true } ? $"a{i}.tag" : $"a{i}").ToList();
        Line($"template <class D = void>");
        Line($"{result} {wrapper}({string.Join(", ", parameters)}) noexcept");
        Line("{");
        Line($"    if constexpr ({one}<D>)");
        Line("    {");
        if (values.Count > 0)
            Line($"        using F = decltype({address});");
        Line($"        {Returned(result, $"{target}({string.Join(", ", values.Select((v, i) => ToCpp(function.Parameters[i].Type, $"wyl::interop::Parameter<F, {i}>", v)))})")}");
        Line("    }");
        Line("    else");
        Line("    {");
        Line($"        {Returned(result, $"{target}({string.Join(", ", values.Select(v => $"wyl::interop::Pass<D>({v})"))})")}");
        Line("    }");
        Line("}");
    }

    /// <summary>
    /// An argument converted to the C++ parameter's type. An <c>extern fn</c> goes through
    /// <c>wyl::interop::ToC</c>, given the address of each <c>@CCallback</c> function of its type, so
    /// that C++ receives a function of its own type.
    /// </summary>
    string ToCpp(WylType type, string cppType, string value)
    {
        if (type.NonNull is not FunctionType { IsExtern: true } cFunction)
            return $"wyl::interop::Convert<{cppType}>({value})";
        var callbacks = callbackNames.Where(c => c.Key.ReturnType.FunctionReturning([.. c.Key.Parameters.Select(p => new FunctionParameterType(p.RefKind, p.Type))]).Extern == cFunction);
        return $"wyl::interop::ToC<{string.Join(", ", callbacks.Select(c => "&" + c.Value).Prepend(cppType))}>({value})";
    }

    /// <summary>A constant's type check and the wrapper that reads it (spec <c>interop.extern.constants</c>).</summary>
    void EmitExternConstant(FunctionSymbol constant)
    {
        var type = CppType(constant.ReturnType);
        var cppName = constant.BuiltinCppName!;
        var wrapper = externWrappers[constant] = $"wyl_extern_{interopRegions.Count}";
        StaticAssert($"wyl::interop::Matches<{BoundaryCppType(constant.ReturnType)}, decltype({cppName})>()",
            Catalog.ConstantMismatch(constant.Declaration!.NameLocation, constant.Name, constant.ReturnType.ToString(), cppName));
        // A pattern that names the constant matches the value that its declaration gives.
        if (constant.ConstantValue is { } value)
        {
            var written = value.Value switch { bool b => b ? "true" : "false", System.Text.Rune r => $"'{r}'", var other => $"{other}" };
            StaticAssert($"static_cast<{type}>({cppName}) == {Literal(value)}",
                Catalog.ConstantValueMismatch(constant.Declaration!.NameLocation, constant.Name, written, cppName));
        }
        Line($"inline {type} {wrapper}() noexcept {{ return wyl::interop::Convert<{type}>({cppName}); }}");
    }

    /// <summary>
    /// The C++ type that C code sees for a Wylow type: a C function pointer for an <c>extern fn</c>,
    /// nullable or not, also behind pointers, and otherwise the type's own C++ type.
    /// </summary>
    static string BoundaryCppType(WylType type) => type switch
    {
        _ when type.NonNull is FunctionType { IsExtern: true } cFunction => $"wyl::CPointer<{CSignature(cFunction)}>",
        PointerType pointer => BoundaryCppType(pointer.Element) + "*",
        _ => CppType(type),
    };

    /// <summary>The C function type of an <c>extern fn</c>, in the trailing form, which any return type can take.</summary>
    static string CSignature(FunctionType type) =>
        $"auto({string.Join(", ", type.Parameters.Select(p => BoundaryCppType(p.Type)))}) -> {(type.ReturnType == WylType.Void ? "void" : BoundaryCppType(type.ReturnType))}";

    /// <summary>The C function that each <c>@CCallback</c> function's address is.</summary>
    readonly Dictionary<FunctionSymbol, string> callbackNames = [];

    /// <summary>
    /// For each <c>@CCallback</c> function, a <c>noexcept</c> function with C's types, which converts
    /// its arguments and result and calls the Wylow function (spec <c>interop.callbacks</c>). The
    /// Wylow function keeps the C++ signature the emitter gives every function.
    /// </summary>
    void EmitCallbacks(BoundProgram program)
    {
        var callbacks = program.Functions.Select(f => f.Symbol).Concat(program.Types.SelectMany(s => s.Members))
            .Where(f => f.IsCCallback && functions.Contains(f)).Distinct().ToList();
        foreach (var callback in callbacks)
        {
            var name = callbackNames[callback] = $"wyl_callback_{callbackNames.Count}";
            var parameters = callback.Parameters.Select((p, i) => $"{BoundaryCppType(p.Type)} a{i}");
            IEnumerable<string> arguments = callback.Parameters.Select((p, i) => $"wyl::interop::Convert<{CppType(p.Type)}>(a{i})");
            if (callback.PassesLocation)
                arguments = arguments.Append(SourceLocation(callback.Declaration!.NameLocation));
            var callee = callback.ContainingType is not { } owner ? FunctionName(callback)
                : callback.IsExternStructBody ? ExternStructFunctionName(callback)
                : $"{ObjectType(owner.Type)}::{MethodName(callback)}";
            var call = $"{callee}({string.Join(", ", arguments)})";
            var result = callback.ReturnType == WylType.Void ? "void" : BoundaryCppType(callback.ReturnType);
            Line();
            Line($"{result} {name}({string.Join(", ", parameters)}) noexcept");
            Line("{");
            Line(callback.ReturnType == WylType.Void ? $"    {call};" : $"    return wyl::interop::Convert<{result}>({call});");
            Line("}");
        }
    }

    /// <summary>The type that crosses the boundary for a value of a type: the integer type of a <c>@Repr</c> enum, and otherwise the type itself.</summary>
    static WylType BoundaryType(WylType type) => type.Symbol is { HasRepr: true, UnderlyingType: { } underlying } ? underlying : type;

    static string Returned(string result, string call) => $"return wyl::interop::Result<{result}>([&]() -> decltype(auto) {{ return {call}; }});";

    /// <summary>The C++ function an extern function names, with its type for a member function.</summary>
    static string QualifiedCppName(FunctionSymbol function) =>
        function.IsInstanceMethod ? $"{function.ContainingType!.CppName}::{function.BuiltinCppName}" : function.BuiltinCppName!;

    /// <summary>The wrapper that a call of an extern function of the program goes through.</summary>
    string ExternWrapperName(FunctionSymbol function) => externWrappers[function];
}
