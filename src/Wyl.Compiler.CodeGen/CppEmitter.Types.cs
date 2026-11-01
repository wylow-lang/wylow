using System.Globalization;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.CodeGen;

public sealed partial class CppEmitter
{
    /// <summary>
    /// Orders definitions so each comes after its base types and every struct its fields hold by
    /// value, including in type arguments. Objects are held by reference and need no ordering.
    /// </summary>
    static List<TypeSymbol> DependencyOrder(IReadOnlyList<TypeSymbol> types)
    {
        var ordered = new List<TypeSymbol>();
        var visited = new HashSet<TypeSymbol>();

        void VisitType(WylType type)
        {
            if (type.IsReference || type.IsPointer)
                return;
            if (type.IsNullableValue)
            {
                VisitType(type.NonNull);
                return;
            }
            if (type is FixedArrayType array)
            {
                VisitType(array.Element);
                return;
            }
            if (type.Symbol is { } s)
                Visit(s);
            foreach (var argument in type.TypeArguments)
                VisitType(argument);
        }

        void Visit(TypeSymbol s)
        {
            if (!visited.Add(s))
                return;
            // An extern struct is defined by the runtime.
            if (s.IsExtern)
                return;
            if (s.BaseType is { } baseType)
                Visit(baseType.Symbol!);
            foreach (var iface in s.Kind is TypeKind.Struct or TypeKind.Enum ? [] : s.Interfaces)
                Visit(iface.Symbol!);
            foreach (var field in s.ValueFields)
                VisitType(field.Type);
            ordered.Add(s);
        }

        foreach (var s in types)
            Visit(s);
        return ordered;
    }

    string DefaultValue(WylType type)
    {
        if (type.IsTypeParameter || (type.IsFixedArray && type.HasDefaultCode))
            return $"wyl::default_value<{CppType(type)}>()";
        return type.IsStruct && type.HasDefaultCode ? $"{CppType(type)}::make_default()" : $"{CppType(type)}{{}}";
    }

    /// <summary>
    /// How a type's own definition names it: C++'s injected class name, unqualified, since a type
    /// defined by a qualified name (<c>wyl::ReadOnlySpan</c>) can't name itself so without type arguments.
    /// </summary>
    static string OwnName(TypeSymbol s) => TypeName(s).Split("::")[^1];

    void EmitStructDefinition(TypeSymbol s)
    {
        var name = OwnName(s);
        if (s.IsGeneric)
            Line(TemplatePrefix(s.TypeParameters).TrimEnd());
        Line($"struct {TypeName(s)}");
        Line("{");
        indent++;
        foreach (var field in s.Fields)
            Line($"{CppType(field.Type)} {FieldName(field)}{{}};");
        if (s.Type.HasDefaultCode)
            Line($"static {name} make_default();");
        DeclareCollectorMembers(s);
        foreach (var member in s.Members.Where(m => !m.IsExtern && !m.IsMemberwise))
            Line(FunctionTemplatePrefix(member) + MemberDeclaration(member) + ";");
        foreach (var member in s.Members.Where(m => m.IsMemberwise))
        {
            foreach (var form in functions.MemberwiseForms(member))
                Line($"static {name} {MemberwiseName(form.Builds!)}({MemberwiseParameters(member, form.Builds!)});");
        }
        // In a template, a defaulted == whose members lack == is deleted rather than an error.
        if (s.IsGeneric || s.Type.SupportsEquality)
            Line($"bool operator==(const {name}&) const = default;");
        indent--;
        Line("};");
    }

    static string PayloadName(VariantSymbol variant) => "p_" + variant.Name;
    static string UnionMember(VariantSymbol variant) => "v_" + variant.Name;
    static string VariantFactory(VariantSymbol variant) => "make_" + variant.Name;

    /// <summary>The tag's C++ type: the underlying type if there is one, else the smallest that numbers the variants.</summary>
    static string TagType(TypeSymbol s)
    {
        if (s.UnderlyingType is { } underlying)
            return CppType(underlying);
        if (s.Variants.Count <= 256)
            return "uint8_t";
        return s.Variants.Count <= 65536 ? "uint16_t" : "uint32_t";
    }

    static string TagValue(VariantSymbol variant) =>
        variant.Owner.UnderlyingType is { } underlying ? Literal(BoundLiteral.Number(variant.Declaration.Location, underlying, variant.Value))
        : variant.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A tag and a union of one payload struct per variant with data. When a payload may hold an
    /// object reference or a string, the union needs copy, move, and destruction written out; otherwise every
    /// member is trivially copyable and so is the enum.
    /// </summary>
    void EmitEnumDefinition(TypeSymbol s)
    {
        var name = OwnName(s);
        var tag = TagType(s);
        var withData = s.Variants.Where(v => v.Fields.Count > 0).ToList();
        bool equatable = s.IsGeneric || s.Type.SupportsEquality;
        if (s.IsGeneric)
            Line(TemplatePrefix(s.TypeParameters).TrimEnd());
        Line($"struct {TypeName(s)}");
        Line("{");
        indent++;
        foreach (var variant in withData)
        {
            Line($"struct {PayloadName(variant)}");
            Line("{");
            indent++;
            foreach (var field in variant.Fields)
                Line($"{CppType(field.Type)} {FieldName(field)};");
            if (equatable)
                Line($"bool operator==(const {PayloadName(variant)}&) const = default;");
            indent--;
            Line("};");
        }
        Line($"{tag} tag;");
        if (withData.Count > 0)
        {
            Line("union");
            Line("{");
            indent++;
            foreach (var variant in withData)
                Line($"{PayloadName(variant)} {UnionMember(variant)};");
            indent--;
            Line("};");
        }

        // C++ needs a default constructor for placeholders such as unassigned locals. It makes the
        // first variant, which is the Wylow default when that variant has no data.
        var first = s.Variants[0];
        Line(first.Fields.Count > 0
            ? $"{name}() : tag({TagValue(first)}), {UnionMember(first)}() {{}}"
            : $"{name}() : tag({TagValue(first)}) {{}}");
        Line($"explicit {name}(wyl::RawTag, {tag} value) : tag(value) {{}}");
        if (withData.Count > 0 && s.Type.IsCounted)
            EmitEnumLifetime(name, withData);

        foreach (var variant in s.Variants)
            EmitVariantFactory(name, variant);
        if (!s.HasData)
            EmitFromValue(s, name, tag);
        DeclareCollectorMembers(s);
        foreach (var member in s.Members.Where(m => !m.IsExtern))
            Line(FunctionTemplatePrefix(member) + MemberDeclaration(member) + ";");

        if (equatable && withData.Count == 0)
        {
            Line($"bool operator==(const {name}&) const = default;");
        }
        else if (equatable)
        {
            Line($"bool operator==(const {name}& other) const");
            Line("{");
            Line("    if (tag != other.tag) return false;");
            EmitTagSwitch(withData, v => $"return {UnionMember(v)} == other.{UnionMember(v)};", "return true;");
            Line("}");
        }
        indent--;
        Line("};");
    }

    /// <summary>A switch over the tag with a case per variant with data; the variants without data go to the default.</summary>
    void EmitTagSwitch(List<VariantSymbol> variants, Func<VariantSymbol, string> body, string otherwise)
    {
        Line("    switch (tag)");
        Line("    {");
        foreach (var variant in variants)
            Line($"    case {TagValue(variant)}: {body(variant)}");
        Line($"    default: {otherwise}");
        Line("    }");
    }

    void EmitEnumLifetime(string name, List<VariantSymbol> withData)
    {
        string Construct(VariantSymbol v, string source) =>
            $"::new (static_cast<void*>(&{UnionMember(v)})) {PayloadName(v)}({source}.{UnionMember(v)}); break;";

        Line($"{name}(const {name}& other) : tag(other.tag)");
        Line("{");
        EmitTagSwitch(withData, v => Construct(v, "other"), "break;");
        Line("}");
        Line($"{name}({name}&& other) noexcept : tag(other.tag)");
        Line("{");
        EmitTagSwitch(withData, v => Construct(v, "std::move(other)"), "break;");
        Line("}");
        Line($"~{name}() {{ wyl_destroy(); }}");
        Line($"void operator=(const {name}& other)");
        Line("{");
        Line($"    {name} copy(other);");
        Line("    *this = std::move(copy);");
        Line("}");
        // Like wyl::Ref, the old value is released only after the new one is stored, since releasing
        // it may run a destructor that reads this enum. Nothing touches `this` afterwards.
        Line($"void operator=({name}&& other) noexcept");
        Line("{");
        Line("    if (this == &other) return;");
        Line($"    {name} old(std::move(*this));");
        Line("    wyl_destroy();");
        Line("    tag = other.tag;");
        EmitTagSwitch(withData, v => Construct(v, "std::move(other)"), "break;");
        Line("}");
        Line("void wyl_destroy()");
        Line("{");
        EmitTagSwitch(withData, v => $"{UnionMember(v)}.~{PayloadName(v)}(); break;", "break;");
        Line("}");
    }

    void EmitVariantFactory(string name, VariantSymbol variant)
    {
        var parameters = variant.Constructor.Parameters;
        var parameterList = string.Join(", ", parameters.Select(p => $"{CppType(p.Type)} {LocalName(p)}"));
        if (parameters.Count == 0)
        {
            Line($"static {name} {VariantFactory(variant)}() {{ return {name}(wyl::RawTag{{}}, {TagValue(variant)}); }}");
            return;
        }
        var values = string.Join(", ", parameters.Select(p => $"std::move({LocalName(p)})"));
        Line($"static {name} {VariantFactory(variant)}({parameterList})");
        Line("{");
        Line($"    {name} result(wyl::RawTag{{}}, {TagValue(variant)});");
        Line($"    ::new (static_cast<void*>(&result.{UnionMember(variant)})) {PayloadName(variant)}{{{values}}};");
        Line("    return result;");
        Line("}");
    }

    /// <summary>
    /// The checked conversion from an integer of any type, for casts to an enum without data. The
    /// value is compared in a type that holds every variant, once it is known to fit in it.
    /// </summary>
    void EmitFromValue(TypeSymbol s, string name, string tag)
    {
        var message = CppStringLiteral($"integer is not a value of enum '{s.DisplayName}'");
        var compared = s.UnderlyingType == WylType.ULong ? WylType.ULong : WylType.Long;
        Line("template <class T>");
        Line($"static {name} from_value(T value, const char* location)");
        Line("{");
        Line($"    if (std::in_range<{CppType(compared)}>(value))");
        Line("    {");
        Line($"        switch (static_cast<{CppType(compared)}>(value))");
        Line("        {");
        foreach (var variant in s.Variants)
            Line($"        case {Literal(BoundLiteral.Number(variant.Declaration.Location, compared, variant.Value))}:");
        Line($"            return {name}(wyl::RawTag{{}}, static_cast<{tag}>(value));");
        Line("        default: break;");
        Line("        }");
        Line("    }");
        Line($"    wyl::raise_panic({message}, location);");
        Line("}");
    }

    /// <summary>
    /// Classes and interfaces. Interfaces inherit virtually so that a class has one copy of each,
    /// and so does <c>wyl::Object</c> in a class hierarchy implementing any. Elsewhere it is a plain
    /// base, which saves the object a pointer and each reference count a lookup of its offset.
    /// </summary>
    void EmitObjectDefinition(TypeSymbol s)
    {
        var bases = new List<string>();
        if (s.BaseType is { } baseType)
            bases.Add(ObjectType(baseType));
        else if (s.Kind == TypeKind.Interface || sharedObjectRoots.Contains(s))
            bases.Add("virtual wyl::Object");
        else
            bases.Add("wyl::Object");
        bases.AddRange(s.Interfaces.Select(i => "virtual " + ObjectType(i)));

        if (s.IsGeneric)
            Line(TemplatePrefix(s.TypeParameters).TrimEnd());
        Line($"struct {TypeName(s)} : {string.Join(", ", bases)}");
        Line("{");
        indent++;
        foreach (var field in s.Fields)
            Line($"{CppType(field.Type)} {FieldName(field)}{{}};");
        if (s.Kind == TypeKind.Class)
        {
            if (s.Constructors.Count == 0)
                Line($"{OwnName(s)}();");
            // Declared here and defined after every type, where the field types are complete.
            if (s.Destructor is null || s.HasSeparateDestructor)
                Line($"~{OwnName(s)}() override;");
        }
        foreach (var member in s.Members.Where(m => !m.IsExtern))
        {
            if (member.IsAbstract)
                EmitAbstractStub(member);
            else
                Line(FunctionTemplatePrefix(member) + MemberDeclaration(member) + ";");
        }
        foreach (var (constructor, index) in s.Constructors.Select((c, i) => (c, i)))
        {
            var built = functions.Single(constructor)!;
            if (NeedsArgumentsConstructor(built))
                EmitArgumentsConstructor(s, index, built);
        }
        foreach (var implementation in s.InheritedImplementations)
            Line($"{CppType(implementation.ReturnType)} {MethodName(implementation.Symbol)}({Parameters(implementation)}) override;");
        DeclareCollectorMembers(s);
        // The runtime borrows any object through these hooks; the interface's methods dispatch to the
        // class that implements them, so every Borrowable gets them from the interface.
        if (s == stdlib.Borrowable)
        {
            Line("void wyl_begin_borrow() override { m_beginBorrow(); }");
            Line("void wyl_end_borrow() override { m_endBorrow(); }");
        }
        indent--;
        Line("};");
    }

    static TypeSymbol RootClass(TypeSymbol s) => s.BaseType?.Symbol is { } baseClass ? RootClass(baseClass) : s;

    /// <summary>
    /// Abstract methods get a body so that no call is ever a C++ pure virtual call: during a base
    /// class constructor or destructor the object is still the base class, and calling an abstract
    /// method then panics.
    /// </summary>
    void EmitAbstractStub(FunctionSymbol method)
    {
        var parameterTypes = string.Join(", ", method.Parameters.Select(p => ParameterType(p.Type, p.RefKind)));
        var location = SourceLocation(method.Declaration!.NameLocation);
        var message = CppStringLiteral(method.AbstractCallMessage);
        var signature = $"{CppType(method.ReturnType)} {MethodName(method)}({parameterTypes})";
        Line($"{(OverridesInCpp(method) ? signature + " override" : "virtual " + signature)} {{ wyl::raise_panic({message}, {location}); }}");
    }

    void EmitDefaultFunction(TypeSymbol s)
    {
        var type = CppType(s.Type);
        if (s.IsGeneric)
            Line(TemplatePrefix(s.TypeParameters).TrimEnd());
        Line($"{type} {type}::make_default()");
        EmitIrFunction(functions.Default(s)!);
    }

    /// <summary><c>make</c> when a call passes every field, and otherwise named for those it passes.</summary>
    static string MemberwiseName(IReadOnlyList<bool> form) =>
        form.All(given => given) ? "make" : "make_" + string.Concat(form.Select(given => given ? '1' : '0'));

    string MemberwiseParameters(FunctionSymbol constructor, IReadOnlyList<bool> form) =>
        string.Join(", ", constructor.Parameters.Where((_, i) => form[i]).Select(p => ParameterDeclaration(constructor, p, p.Type)));

    /// <summary>
    /// A memberwise constructor as calls with these fields use it: it sets each field, in order, to
    /// its argument, or for one left out, to its initializer or its type's default value.
    /// </summary>
    void EmitMemberwiseConstructor(BoundFunction function, IrFunction built)
    {
        var symbol = function.Symbol;
        var s = symbol.ContainingType!;
        var form = built.Builds!;
        var type = CppType(s.Type);
        EmitOwnerTemplatePrefix(s);
        Line($"{type} {type}::{MemberwiseName(form)}({MemberwiseParameters(symbol, form)})");
        EmitIrFunction(built);
    }

    /// <summary>Connects an interface method to the base class method that implements it.</summary>
    void EmitForwarder(TypeSymbol s, MethodReference implementation)
    {
        EmitOwnerTemplatePrefix(s);
        var method = implementation.Symbol;
        Line($"{CppType(implementation.ReturnType)} {ObjectType(s.Type)}::{MethodName(method)}({Parameters(implementation)})");
        Line("{");
        var arguments = string.Join(", ", method.Parameters.Select(LocalName));
        Line($"    return this->{ObjectType(implementation.Owner!)}::{MethodName(method)}({arguments});");
        Line("}");
    }

    void EmitOwnerTemplatePrefix(TypeSymbol s)
    {
        if (s.IsGeneric)
            Line(TemplatePrefix(s.TypeParameters).TrimEnd());
    }
}
