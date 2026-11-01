using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.CodeGen;

/// <summary>
/// What the cycle collector needs from each type: whether a class's objects may be cyclic, a
/// <c>wyl_visit</c> that visits every strong reference a value holds, and for classes with
/// destructors, a way to run them without releasing fields. See <c>wyl::CycleCollector</c>.
/// </summary>
public sealed partial class CppEmitter
{
    /// <summary>
    /// Generic classes are cyclic per instantiation. <c>wyl::is_cyclic</c> is true unless specialized
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
            Line($"template <> inline constexpr const char* class_name<{ObjectType(type)}> = {CppStringLiteral(type.Name)};");
            if (!cycles.IsCyclic(type))
                Line($"template <> inline constexpr bool is_cyclic<{ObjectType(type)}> = false;");
        }
        Line("}");
    }

    string CyclicFlag(TypeSymbol s) =>
        s.IsGeneric ? $"wyl::is_cyclic<{ObjectType(s.Type)}>" : cycles.IsCyclic(s.Type) ? "true" : "false";

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
            Line($"void wyl_visit(wyl::Visitor& visitor){(isClass ? " override" : "")};");
        if (s.HasSeparateDestructor)
            Line("void wyl_finalize() override;");
        if (isClass)
            Line($"const char* wyl_class_name() const override {{ return {ClassName(s)}; }}");
    }

    string ClassName(TypeSymbol s)
    {
        if (!s.IsGeneric)
            return CppStringLiteral(s.Name);
        var name = $"wyl::class_name<{ObjectType(s.Type)}>";
        return $"{name} != nullptr ? {name} : {CppStringLiteral(s.Name)}";
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
                    Line($"{ObjectType(baseType)}::wyl_visit(visitor);");
                foreach (var field in s.Fields.Where(f => NeedsVisit(f.Type)))
                    Line($"wyl::visit(visitor, this->{FieldName(field)});");
                if (OwnContainerElement(s) is not null)
                {
                    var span = stdlib.ReadOnlySpan!;
                    var data = FieldName(span.FindField("data")!);
                    var count = FieldName(span.FindField("count")!);
                    Line("auto span = this->m_asReadOnlySpan();");
                    Line($"for (int32_t i = 0; i < span.{count}; ++i) wyl::visit(visitor, span.{data}[i]);");
                }
            });
        }
        else if (s.Kind == TypeKind.Struct && NeedsValueVisit(s))
        {
            EmitVisit(s, () =>
            {
                foreach (var field in s.Fields.Where(f => NeedsVisit(f.Type)))
                    Line($"wyl::visit(visitor, {FieldName(field)});");
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
                    var visits = variant.Fields.Where(f => NeedsVisit(f.Type)).Select(f => $"wyl::visit(visitor, {UnionMember(variant)}.{FieldName(f)}); ");
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
            Line("    if (!this->wyl_finalized()) this->wyl_destructor();");
            Line("}");
            Line();
            EmitOwnerTemplatePrefix(s);
            Line($"void {owner}::wyl_finalize()");
            Line("{");
            Line("    this->wyl_destructor();");
            if (s.Type.BaseType is { } baseType)
                Line($"    {ObjectType(baseType)}::wyl_finalize();");
            Line("}");
        }
    }

    void EmitVisit(TypeSymbol s, Action body)
    {
        Line();
        EmitOwnerTemplatePrefix(s);
        Line($"void {ObjectType(s.Type)}::wyl_visit([[maybe_unused]] wyl::Visitor& visitor)");
        Line("{");
        indent++;
        body();
        indent--;
        Line("}");
    }
}
