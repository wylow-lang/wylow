using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.CodeGen;

/// <summary>
/// What the cycle collector needs from each type: whether a class's objects may be cyclic, a
/// <c>Visit</c> that visits every strong reference a value holds, and for classes with
/// destructors, a way to run them without releasing fields. See <c>wyl::CycleCollector</c>.
/// </summary>
public sealed partial class CppEmitter
{
    /// <summary>
    /// Generic classes are cyclic per instantiation. <c>wyl::IS_CYCLIC</c> is true unless specialized
    /// here, for the instantiations that only C++ sees. Known instantiations also get their Wylow name.
    /// </summary>
    void EmitInstantiationTraits()
    {
        // What C++ doesn't tell apart is erased already (`WylType.CppIdentity`): `T` and `T?` are one C++ type.
        var instances = cycles.Classes.Where(c => c.Symbol!.IsGeneric).ToList();
        if (instances.Count == 0)
            return;
        Line();
        Line("namespace wyl {");
        foreach (var type in instances)
        {
            Line($"template <> inline constexpr const char* CLASS_NAME<{ObjectType(type)}> = {CppStringLiteral(type.Name)};");
            if (!cycles.IsCyclic(type))
                Line($"template <> inline constexpr bool IS_CYCLIC<{ObjectType(type)}> = false;");
        }
        Line("}");
    }

    string CyclicFlag(TypeSymbol s) =>
        s.IsGeneric ? $"wyl::IS_CYCLIC<{ObjectType(s.Type)}>" : cycles.IsCyclic(s.Type) ? "true" : "false";

    static bool NeedsVisit(WylType fieldType) => fieldType.MayOwnObjects;

    /// <summary>A class's own fields and container elements to visit; a base class visits its own.</summary>
    bool HasOwnReferences(TypeSymbol s) =>
        s.Fields.Any(f => NeedsVisit(f.Type)) || OwnContainerElement(s) is not null;

    WylType? OwnContainerElement(TypeSymbol s) =>
        stdlib.ContainerElement(s.Type) is { } element && (s.Type.BaseType is not { } baseType || stdlib.ContainerElement(baseType) is null)
            ? element
            : null;

    void DeclareCollectorMembers(TypeSymbol s)
    {
        if (s.Kind == TypeKind.Interface)
            return;
        bool isClass = s.Kind == TypeKind.Class;
        if (isClass ? HasOwnReferences(s) : NeedsValueVisit(s))
            Line($"void Visit(wyl::Visitor& visitor){(isClass ? " override" : "")};");
        if (s.HasSeparateDestructor)
            Line("void Finalize() override;");
        if (isClass)
            Line($"const char* ClassName() const override {{ return {ClassName(s)}; }}");
    }

    string ClassName(TypeSymbol s)
    {
        if (!s.IsGeneric)
            return CppStringLiteral(s.DisplayName);
        var name = $"wyl::CLASS_NAME<{ObjectType(s.Type)}>";
        return $"{name} != nullptr ? {name} : {CppStringLiteral(s.DisplayName)}";
    }

    bool NeedsValueVisit(TypeSymbol s) => !s.IsRefStruct && (s.IsGeneric || s.Type.MayOwnObjects);

    void EmitCollectorMembers(TypeSymbol s)
    {
        var owner = ObjectType(s.Type);
        if (s.Kind == TypeKind.Class && HasOwnReferences(s))
        {
            EmitVisit(s, () =>
            {
                if (s.Type.BaseType is { } baseType)
                    Line($"{ObjectType(baseType)}::Visit(visitor);");
                foreach (var field in s.Fields.Where(f => NeedsVisit(f.Type)))
                    Line($"wyl::Visit(visitor, this->{FieldName(field)});");
                if (OwnContainerElement(s) is not null)
                {
                    var span = stdlib.ReadOnlySpan!;
                    var data = FieldName(span.FindField("data")!);
                    var count = FieldName(span.FindField("count")!);
                    Line("auto span = this->m_asReadOnlySpan();");
                    Line($"for (int32_t i = 0; i < span.{count}; ++i) wyl::Visit(visitor, span.{data}[i]);");
                }
            });
        }
        else if (s.Kind == TypeKind.Struct && NeedsValueVisit(s))
        {
            EmitVisit(s, () =>
            {
                foreach (var field in s.Fields.Where(f => NeedsVisit(f.Type)))
                    Line($"wyl::Visit(visitor, {FieldName(field)});");
            });
        }
        else if (s.Kind == TypeKind.Enum && NeedsValueVisit(s))
        {
            var withReferences = s.Variants.Where(v => v.Fields.Any(f => NeedsVisit(f.Type))).ToList();
            EmitVisit(s, () =>
            {
                Line("switch (tag)");
                Line("{");
                foreach (var variant in withReferences)
                {
                    var visits = variant.Fields.Where(f => NeedsVisit(f.Type)).Select(f => $"wyl::Visit(visitor, {UnionMember(variant)}.{FieldName(f)}); ");
                    Line($"case {TagValue(variant)}: {string.Concat(visits)}break;");
                }
                Line("default: break;");
                Line("}");
            });
        }

        if (s.HasSeparateDestructor)
        {
            Line();
            EmitOwnerTemplatePrefix(s);
            Line($"{owner}::~{OwnName(s)}()");
            Line("{");
            Line("    if (!this->Finalized()) this->wyl_destructor();");
            Line("}");
            Line();
            EmitOwnerTemplatePrefix(s);
            Line($"void {owner}::Finalize()");
            Line("{");
            Line("    this->wyl_destructor();");
            if (s.Type.BaseType is { } baseType)
                Line($"    {ObjectType(baseType)}::Finalize();");
            Line("}");
        }
    }

    void EmitVisit(TypeSymbol s, Action body)
    {
        Line();
        EmitOwnerTemplatePrefix(s);
        Line($"void {ObjectType(s.Type)}::Visit([[maybe_unused]] wyl::Visitor& visitor)");
        Line("{");
        indent++;
        body();
        indent--;
        Line("}");
    }
}
