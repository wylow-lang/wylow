using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    void CheckStaticField(FieldSymbol field, TypeSymbol s)
    {
        var name = field.Declaration.NameLocation;
        if (s.IsGeneric)
            Error(Catalog.StaticInGenericType(name, s.Name));
        if (field.IsMutable)
            Error(Catalog.MutStaticField(name));
        if (field.Type != WylType.Error)
            CheckSendableAndCopyable(field.Type, (property, detail, at) => Catalog.StaticTypeNotAllowed(name, field.Name, property, detail, at));
    }

    void CheckSharedField(FieldSymbol field, TypeSymbol s)
    {
        if (!s.IsShared || field.Type == WylType.Error)
            return;
        var name = field.Declaration.NameLocation;
        if (field.IsMutable)
            Error(Catalog.MutSharedClassField(name, field.Name, s.Name));
        CheckSendableAndCopyable(field.Type, (property, detail, at) => Catalog.SharedClassFieldTypeNotAllowed(name, field.Name, s.Name, property, detail, at));
    }

    /// <summary>Reports a type that other threads would share but that is not sendable, or not copyable.</summary>
    void CheckSendableAndCopyable(WylType type, Func<string, string, Location?, Diagnostic> error)
    {
        if (!type.IsSendableIn(null))
        {
            var (detail, at) = SendableProblem(type);
            Error(error("sendable", detail, at));
        }
        if (!type.IsCopyableIn(null))
        {
            var (detail, at) = CopyableProblem(type);
            Error(error("copyable", detail, at));
        }
    }

    static (string Detail, Location? At) CopyableProblem(WylType type)
    {
        var unique = type.InlineTypes().FirstOrDefault(t => t.Symbol is { IsUnique: true });
        return unique is null
            ? ($"'{type}' has no copies", null)
            : ($"'{unique}' is unique", unique.Symbol?.Declaration.NameLocation);
    }

    static (string Detail, Location? At) SendableProblem(WylType type)
    {
        var value = type.NonNull;
        if (value.IsStructOrEnum && !value.Symbol!.IsCppClass)
        {
            var map = TypeMap.For(value);
            foreach (var field in value.Symbol.ValueFields)
            {
                var fieldType = map.Apply(field.Type);
                if (!fieldType.IsSendableIn(null))
                    return ($"field '{field.Name}' has type '{fieldType}', which is not sendable", field.Declaration.NameLocation);
            }
        }
        if (value is FixedArrayType array && !array.Element.IsSendableIn(null))
            return ($"element type '{array.Element}' is not sendable", null);
        return ($"'{type}' is not sendable", null);
    }

    /// <summary>
    /// A value type that holds a unique value inline must be unique itself, since a copy of it
    /// would copy that value (spec <c>structs.unique</c>, <c>enums.unique</c>). A class's object is
    /// shared by its copies, so it may hold one. A type that nests without end is already reported,
    /// and looking through its fields would never finish.
    /// </summary>
    void CheckUniqueFields(TypeSymbol s, HashSet<TypeSymbol> cyclic)
    {
        if (s.IsUnique || s.IsCppClass || cyclic.Contains(s) || s.Kind is not (TypeKind.Struct or TypeKind.Enum))
            return;
        var what = s.Kind == TypeKind.Enum ? "variant field" : "field";
        foreach (var field in s.ValueFields)
        {
            if (field.Type.IsUniqueType)
                Error(Catalog.UniqueField(field.Declaration.NameLocation, SyntaxFacts.KindName(s.Kind), s.Name, what, field.Type.ToString()));
        }
    }

    /// <summary>
    /// A struct or enum may not contain itself by value, directly or through type arguments
    /// (<c>struct Rec { Wrap&lt;Rec&gt; w; }</c>), and its field types may not grow forever. Returns the
    /// types reported.
    /// </summary>
    HashSet<TypeSymbol> CheckStructCycles(List<TypeSymbol> all)
    {
        const int maxDepth = 32;
        var reported = new HashSet<TypeSymbol>();
        var acyclic = new HashSet<WylType>();

        (List<WylType> Types, bool Nests)? FindCycle(WylType type, List<WylType> path)
        {
            if (acyclic.Contains(type))
                return null;
            int index = path.IndexOf(type);
            if (index >= 0)
            {
                var cycle = path.Skip(index).Append(type).ToList();
                foreach (var member in cycle)
                    reported.Add(member.Symbol!);
                return (cycle, false);
            }
            if (path.Count >= maxDepth)
            {
                foreach (var member in path)
                    reported.Add(member.Symbol!);
                return (path, true);
            }

            path.Add(type);
            var map = TypeMap.For(type);
            foreach (var field in type.Symbol!.ValueFields)
            {
                var fieldType = map.Apply(field.Type);
                // A nullable value holds its value inline too.
                while (fieldType.IsFixedArray || fieldType.IsNullableValue)
                    fieldType = fieldType is FixedArrayType array ? array.Element : fieldType.NonNull;
                if (fieldType.IsStructOrEnum && FindCycle(fieldType, path) is { } problem)
                    return problem;
            }
            path.RemoveAt(path.Count - 1);
            acyclic.Add(type);
            return null;
        }

        foreach (var s in all)
        {
            if (!reported.Contains(s) && FindCycle(s.Type, []) is { } problem)
                Error(problem.Nests ? Catalog.UnboundedNesting(s.Declaration.NameLocation, SyntaxFacts.KindName(s.Kind), s.Name) : Catalog.StructCycle(s.Declaration.NameLocation, SyntaxFacts.KindName(s.Kind), s.Name, problem.Types.Select(t => t.ToString())));
        }
        return reported;
    }

    void BindFieldInitializers(TypeSymbol s)
    {
        foreach (var field in s.Fields)
        {
            if (field.Declaration.Initializer is not { } initializer)
                continue;
            currentType = s;
            typeParameters = s.TypeParameters;
            noThisContext = NoThisContext.FieldInitializer;
            scope = new Scope(null);
            field.Initializer = Convert(BindExpression(initializer, field.Type), field.Type);
            currentType = null;
            typeParameters = [];
            noThisContext = NoThisContext.Static;
            scope = null;
        }
        foreach (var field in s.StaticFields)
        {
            if (field.Declaration.Initializer is not { } initializer)
            {
                if (field.Type != WylType.Error && !field.Type.IsDefaultable)
                    Error(Catalog.StaticWithoutDefault(field.Declaration.NameLocation, field.Name, field.Type.ToString()));
                continue;
            }
            currentType = s;
            typeParameters = s.TypeParameters;
            noThisContext = NoThisContext.FieldInitializer;
            hasThis = false;
            scope = new Scope(null);
            field.Initializer = Convert(BindExpression(initializer, field.Type), field.Type);
            currentType = null;
            typeParameters = [];
            noThisContext = NoThisContext.Static;
            scope = null;
        }
    }
}
