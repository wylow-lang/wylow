using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.CodeGen;

/// <summary>
/// What the cycle collector needs from each type: whether a class's objects may be cyclic, a
/// <c>vpl_visit</c> that visits every strong reference a value holds, and for classes with
/// destructors, a way to run them without releasing fields. See <c>vpl::CycleCollector</c>.
/// </summary>
public sealed partial class CppEmitter
{
    /// <summary>
    /// Generic classes are cyclic per instantiation. <c>vpl::is_cyclic</c> is true unless specialized
    /// here, for the instantiations that only C++ sees. Known instantiations also get their VPL name.
    /// </summary>
    void EmitInstantiationTraits()
    {
        // What C++ doesn't tell apart is erased already (`VplType.CppIdentity`): `T` and `T?` are one C++ type.
        var instances = cycles.Classes.Where(c => c.Symbol!.IsGeneric).ToList();
        if (instances.Count == 0)
            return;
        Line();
        Line("namespace vpl {");
        foreach (var type in instances)
        {
            Line($"template <> inline constexpr const char* class_name<{ObjectType(type)}> = {CppStringLiteral(type.Name)};");
            if (!cycles.IsCyclic(type))
                Line($"template <> inline constexpr bool is_cyclic<{ObjectType(type)}> = false;");
        }
        Line("}");
    }

    string CyclicFlag(TypeSymbol s) =>
        s.IsGeneric ? $"vpl::is_cyclic<{ObjectType(s.Type)}>" : cycles.IsCyclic(s.Type) ? "true" : "false";

    static bool NeedsVisit(VplType fieldType) => fieldType.MayOwnObjects;

    /// <summary>A class's own fields and container elements to visit; a base class visits its own.</summary>
    bool HasOwnReferences(TypeSymbol s) =>
        s.Fields.Any(f => NeedsVisit(f.Type)) || OwnContainerElement(s) is not null;

    VplType? OwnContainerElement(TypeSymbol s) =>
        stdlib.ContainerElement(s.Type) is { } element && (s.Type.BaseType is not { } baseType || stdlib.ContainerElement(baseType) is null)
            ? element
            : null;

    /// <summary>
    /// The collector runs the destructors of a cycle's objects before releasing any of their fields,
    /// so a destructor's body is a method of its own. The standard library's destructors only
    /// release storage, so they run with the fields, after every destructor of the cycle.
    /// </summary>
    static bool HasSeparateDestructor(TypeSymbol s) =>
        s.Kind == TypeKind.Class && s.Destructor is not null && !Stdlib.Contains(s.Declaration.Location.Source);

    void DeclareCollectorMembers(TypeSymbol s)
    {
        if (s.Kind == TypeKind.Interface)
            return;
        bool isClass = s.Kind == TypeKind.Class;
        if (isClass ? HasOwnReferences(s) : NeedsValueVisit(s))
            Line($"void vpl_visit(vpl::Visitor& visitor){(isClass ? " override" : "")};");
        if (HasSeparateDestructor(s))
            Line("void vpl_finalize() override;");
        if (isClass)
            Line($"const char* vpl_class_name() const override {{ return {ClassName(s)}; }}");
    }

    string ClassName(TypeSymbol s)
    {
        if (!s.IsGeneric)
            return CppStringLiteral(s.Name);
        var name = $"vpl::class_name<{ObjectType(s.Type)}>";
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
                    Line($"{ObjectType(baseType)}::vpl_visit(visitor);");
                foreach (var field in s.Fields.Where(f => NeedsVisit(f.Type)))
                    Line($"vpl::visit(visitor, this->{FieldName(field)});");
                if (OwnContainerElement(s) is not null)
                {
                    var span = stdlib.ReadOnlySpan!;
                    var data = FieldName(span.FindField("data")!);
                    var count = FieldName(span.FindField("count")!);
                    Line("auto span = this->m_asReadOnlySpan();");
                    Line($"for (int32_t i = 0; i < span.{count}; ++i) vpl::visit(visitor, span.{data}[i]);");
                }
            });
        }
        else if (s.Kind == TypeKind.Struct && NeedsValueVisit(s))
        {
            EmitVisit(s, () =>
            {
                foreach (var field in s.Fields.Where(f => NeedsVisit(f.Type)))
                    Line($"vpl::visit(visitor, {FieldName(field)});");
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
                    var visits = variant.Fields.Where(f => NeedsVisit(f.Type)).Select(f => $"vpl::visit(visitor, {UnionMember(variant)}.{FieldName(f)}); ");
                    Line($"case {TagValue(variant)}: {string.Concat(visits)}break;");
                }
                Line("default: break;");
                Line("}");
            });
        }

        if (HasSeparateDestructor(s))
        {
            Line();
            EmitOwnerTemplatePrefix(s);
            Line($"{owner}::~{TypeName(s)}()");
            Line("{");
            Line("    if (!this->vpl_finalized()) this->vpl_destructor();");
            Line("}");
            Line();
            EmitOwnerTemplatePrefix(s);
            Line($"void {owner}::vpl_finalize()");
            Line("{");
            Line("    this->vpl_destructor();");
            if (s.Type.BaseType is { } baseType)
                Line($"    {ObjectType(baseType)}::vpl_finalize();");
            Line("}");
        }
    }

    void EmitVisit(TypeSymbol s, Action body)
    {
        Line();
        EmitOwnerTemplatePrefix(s);
        Line($"void {ObjectType(s.Type)}::vpl_visit([[maybe_unused]] vpl::Visitor& visitor)");
        Line("{");
        indent++;
        body();
        indent--;
        Line("}");
    }
}
