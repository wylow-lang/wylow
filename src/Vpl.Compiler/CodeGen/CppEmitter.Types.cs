using System.Globalization;
using System.Text;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.CodeGen;

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

        void VisitType(VplType type)
        {
            if (type.IsReference || type.IsPointer)
                return;
            if (type.IsNullableValue)
            {
                VisitType(type.NonNull);
                return;
            }
            if (type.IsFixedArray)
            {
                VisitType(type.ElementType!);
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

    /// <summary>
    /// Whether default construction must run code rather than just zero-initialize. Generic structs
    /// always get a <c>make_default</c>, since their type arguments may need one.
    /// </summary>
    bool NeedsDefaultFunction(TypeSymbol s)
    {
        if (!needsDefaultFunction.TryGetValue(s, out bool needs))
        {
            needsDefaultFunction[s] = false;
            needs = !s.IsExtern && (s.IsGeneric || s.Fields.Any(f => f.Initializer is not null || NeedsDefaultCode(f.Type)));
            needsDefaultFunction[s] = needs;
        }
        return needs;
    }

    bool NeedsDefaultCode(VplType type) =>
        type.IsFixedArray ? NeedsDefaultCode(type.ElementType!)
        : type.IsTypeParameter || (type.IsStruct && NeedsDefaultFunction(type.Symbol!));

    string DefaultValue(VplType type)
    {
        if (type.IsTypeParameter || (type.IsFixedArray && NeedsDefaultCode(type)))
            return $"vpl::default_value<{CppType(type)}>()";
        return type.IsStruct && NeedsDefaultFunction(type.Symbol!) ? $"{CppType(type)}::make_default()" : $"{CppType(type)}{{}}";
    }

    void EmitStructDefinition(TypeSymbol s)
    {
        var name = TypeName(s);
        if (s.IsGeneric)
            Line(TemplatePrefix(s.TypeParameters).TrimEnd());
        Line($"struct {name}");
        Line("{");
        indent++;
        foreach (var field in s.Fields)
            Line($"{CppType(field.Type)} {FieldName(field)}{{}};");
        if (NeedsDefaultFunction(s))
            Line($"static {name} make_default();");
        DeclareCollectorMembers(s);
        foreach (var member in s.Members.Where(m => !m.IsExtern))
            Line(TemplatePrefix(member.TypeParameters) + MemberDeclaration(member) + ";");
        // In a template, a defaulted == whose members lack == is deleted rather than an error.
        if (s.IsGeneric || Conversions.SupportsEquality(s.Type))
            Line($"bool operator==(const {name}&) const = default;");
        indent--;
        Line("};");
    }

    static string PayloadName(VariantSymbol variant) => "p_" + variant.Name;
    static string UnionMember(VariantSymbol variant) => "v_" + variant.Name;
    static string VariantFactory(VariantSymbol variant) => "make_" + variant.Name;

    /// <summary>The tag's C++ type: the underlying type if there is one, else the smallest that numbers the variants.</summary>
    static string TagType(TypeSymbol s) =>
        s.UnderlyingType == VplType.Long ? "int64_t"
        : s.UnderlyingType == VplType.Int ? "int32_t"
        : s.Variants.Count <= 256 ? "uint8_t"
        : s.Variants.Count <= 65536 ? "uint16_t"
        : "uint32_t";

    static string TagValue(VariantSymbol variant) =>
        variant.Owner.UnderlyingType == VplType.Long ? Literal(new BoundLiteral(variant.Declaration.Location, VplType.Long, variant.Value))
        : variant.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A tag and a union of one payload struct per variant with data. When a payload may hold an
    /// object reference, the union needs copy, move, and destruction written out; otherwise every
    /// member is trivially copyable and so is the enum.
    /// </summary>
    void EmitEnumDefinition(TypeSymbol s)
    {
        var name = TypeName(s);
        var tag = TagType(s);
        var withData = s.Variants.Where(v => v.Fields.Count > 0).ToList();
        bool equatable = s.IsGeneric || Conversions.SupportsEquality(s.Type);
        if (s.IsGeneric)
            Line(TemplatePrefix(s.TypeParameters).TrimEnd());
        Line($"struct {name}");
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
        // first variant, which is the VPL default when that variant has no data.
        var first = s.Variants[0];
        Line(first.Fields.Count > 0
            ? $"{name}() : tag({TagValue(first)}), {UnionMember(first)}() {{}}"
            : $"{name}() : tag({TagValue(first)}) {{}}");
        Line($"explicit {name}(vpl::RawTag, {tag} value) : tag(value) {{}}");
        if (withData.Count > 0 && MayHoldReferences(s.Type))
            EmitEnumLifetime(name, withData);

        foreach (var variant in s.Variants)
            EmitVariantFactory(name, variant);
        if (!s.HasData)
            EmitFromValue(s, name, tag);
        DeclareCollectorMembers(s);
        foreach (var member in s.Members.Where(m => !m.IsExtern))
            Line(TemplatePrefix(member.TypeParameters) + MemberDeclaration(member) + ";");

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
        Line($"~{name}() {{ vpl_destroy(); }}");
        Line($"void operator=(const {name}& other)");
        Line("{");
        Line($"    {name} copy(other);");
        Line("    *this = std::move(copy);");
        Line("}");
        // Like vpl::Ref, the old value is released only after the new one is stored, since releasing
        // it may run a destructor that reads this enum. Nothing touches `this` afterwards.
        Line($"void operator=({name}&& other) noexcept");
        Line("{");
        Line("    if (this == &other) return;");
        Line($"    {name} old(std::move(*this));");
        Line("    vpl_destroy();");
        Line("    tag = other.tag;");
        EmitTagSwitch(withData, v => Construct(v, "std::move(other)"), "break;");
        Line("}");
        Line("void vpl_destroy()");
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
            Line($"static {name} {VariantFactory(variant)}() {{ return {name}(vpl::RawTag{{}}, {TagValue(variant)}); }}");
            return;
        }
        var values = string.Join(", ", parameters.Select(p => $"std::move({LocalName(p)})"));
        Line($"static {name} {VariantFactory(variant)}({parameterList})");
        Line("{");
        Line($"    {name} result(vpl::RawTag{{}}, {TagValue(variant)});");
        Line($"    ::new (static_cast<void*>(&result.{UnionMember(variant)})) {PayloadName(variant)}{{{values}}};");
        Line("    return result;");
        Line("}");
    }

    /// <summary>The checked conversion from an integer, for casts to an enum without data.</summary>
    void EmitFromValue(TypeSymbol s, string name, string tag)
    {
        var message = CppStringLiteral($"integer is not a value of enum '{s.Name}'");
        Line($"static {name} from_value(int64_t value, const char* location)");
        Line("{");
        Line("    switch (value)");
        Line("    {");
        foreach (var variant in s.Variants)
            Line($"    case {Literal(new BoundLiteral(variant.Declaration.Location, VplType.Long, variant.Value))}:");
        Line($"        return {name}(vpl::RawTag{{}}, static_cast<{tag}>(value));");
        Line($"    default: vpl::panic({message}, location);");
        Line("    }");
        Line("}");
    }

    /// <summary>
    /// Classes and interfaces. Interfaces inherit virtually so that a class has one copy of each,
    /// and so does <c>vpl::Object</c> in a class hierarchy implementing any. Elsewhere it is a plain
    /// base, which saves the object a pointer and each reference count a lookup of its offset.
    /// </summary>
    void EmitObjectDefinition(TypeSymbol s)
    {
        var bases = new List<string>();
        if (s.BaseType is { } baseType)
            bases.Add(ObjectType(baseType));
        else if (s.Kind == TypeKind.Interface || sharedObjectRoots.Contains(s))
            bases.Add("virtual vpl::Object");
        else
            bases.Add("vpl::Object");
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
                Line($"{TypeName(s)}();");
            // Declared here and defined after every type, where the field types are complete.
            if (s.Destructor is null || HasSeparateDestructor(s))
                Line($"~{TypeName(s)}() override;");
        }
        foreach (var member in s.Members.Where(m => !m.IsExtern))
        {
            if (member.IsAbstract)
                EmitAbstractStub(member);
            else
                Line(TemplatePrefix(member.TypeParameters) + MemberDeclaration(member) + ";");
        }
        foreach (var implementation in s.InheritedImplementations)
            Line($"{CppType(implementation.ReturnType)} {MethodName(implementation.Symbol)}({Parameters(implementation)}) override;");
        DeclareCollectorMembers(s);
        if (s.Kind == TypeKind.Class && stdlib.Borrowable is { } borrowable && Conversions.IsSubtype(s.Type, borrowable.Type)
            && !(s.Type.BaseType is { } inherited && Conversions.IsSubtype(inherited, borrowable.Type)))
        {
            var hooks = $"static_cast<{ObjectType(borrowable.Type)}*>(this)";
            Line($"void vpl_begin_borrow() override {{ {hooks}->m_beginBorrow(); }}");
            Line($"void vpl_end_borrow() override {{ {hooks}->m_endBorrow(); }}");
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
        var message = CppStringLiteral($"abstract method '{method.ContainingType!.Name}.{method.Name}' called during construction or destruction");
        Line($"virtual {CppType(method.ReturnType)} {MethodName(method)}({parameterTypes}) {{ vpl::panic({message}, {location}); }}");
    }

    void EmitDefaultFunction(TypeSymbol s)
    {
        var type = CppType(s.Type);
        context = Context.Free;
        if (s.IsGeneric)
            Line(TemplatePrefix(s.TypeParameters).TrimEnd());
        Line($"{type} {type}::make_default()");
        Line("{");
        indent++;
        Line($"{type} self{{}};");
        foreach (var field in s.Fields)
        {
            if (field.Initializer is { } initializer)
            {
                DeclarePatternLocals(initializer);
                Line($"self.{FieldName(field)} = {Expression(initializer)};");
            }
            else if (NeedsDefaultCode(field.Type))
                Line($"self.{FieldName(field)} = {DefaultValue(field.Type)};");
        }
        Line("return self;");
        indent--;
        Line("}");
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
