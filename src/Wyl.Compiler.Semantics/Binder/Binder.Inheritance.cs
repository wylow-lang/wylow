using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    /// <summary>Checks overriding and hiding against base classes, and that every abstract and interface method is implemented.</summary>
    void CheckMembers(TypeSymbol s)
    {
        var self = s.Type;
        var methods = s.Methods.Values.SelectMany(m => m).ToList();

        if (s.Kind == TypeKind.Interface)
        {
            foreach (var method in methods)
            {
                var key = MethodKey(method, TypeMap.Empty);
                var inherited = Conversions.Supertypes(self).Select(t => FindMethod(t, key)).FirstOrDefault(m => m is not null);
                if (inherited is not null)
                    Error(Catalog.RedeclaredInterfaceMethod(method.Declaration!.NameLocation, method.Signature, inherited.Owner!.ToString()));
            }
            return;
        }

        if (s.Kind == TypeKind.Class)
        {
            foreach (var field in s.Fields)
            {
                if (self.BaseType is { } baseType && LookupField(baseType, field.Name) is { } inherited)
                    Error(Catalog.FieldInBase(field.Declaration.NameLocation, field.Name, inherited.Field.Owner.Name));
            }

            foreach (var method in methods)
                CheckOverride(s, method);

            if (!s.IsAbstract)
            {
                var seen = new HashSet<string>();
                for (var t = self; t is not null; t = t.BaseType)
                {
                    foreach (var method in t.Symbol!.Methods.Values.SelectMany(m => m))
                    {
                        // An abstract method of the class itself is already reported as needing an abstract class.
                        if (seen.Add(MethodKey(method, TypeMap.For(t))) && method.IsAbstract && t.Symbol != s)
                            Error(Catalog.MissingAbstractImplementation(s.Declaration.NameLocation, s.Name, $"{t.Symbol.Name}.{method.Name}({ParameterList(new MethodReference(method, t, []))})"));
                    }
                }
            }
        }

        var inheritedInterfaces = self.BaseType is { } b ? Conversions.Supertypes(b).ToHashSet() : [];
        foreach (var iface in Conversions.Supertypes(self).Where(t => t.IsInterface))
        {
            foreach (var method in iface.Symbol!.Methods.Values.SelectMany(m => m))
            {
                var required = new MethodReference(method, iface, []);
                var description = $"{iface}.{method.Name}({ParameterList(required)})";
                var implementation = FindImplementation(self, MethodKey(method, TypeMap.For(iface)));
                if (implementation is null)
                {
                    if (!s.IsAbstract)
                        Error(Catalog.MissingInterfaceImplementation(s.Declaration.NameLocation, s.Name, description));
                    continue;
                }
                var location = implementation.Owner!.Symbol == s ? implementation.Symbol.Declaration!.NameLocation : s.Declaration.NameLocation;
                var implementing = $"{implementation.Owner!.Symbol!.Name}.{method.Name}";
                if (implementation.Symbol.IsStatic)
                    Error(Catalog.StaticImplementation(location, implementing, description));
                else if (implementation.Symbol.Declaration!.Visibility != iface.Symbol.Declaration.Visibility)
                    Error(Catalog.ImplementationVisibility(location, implementing, description, iface.Symbol.Declaration.Visibility, iface.Symbol.Name));
                else if (!HasSameTypes(implementation, required))
                    Error(Catalog.ImplementationSignature(location, implementing, description, FindEscapingDifference(implementation, required)));
                // A class method can't change the reference it is called through, so only a struct's must promise not to change 'this'.
                else if (method.IsReadOnly && s.Kind == TypeKind.Struct && !implementation.Symbol.IsReadOnly)
                    Error(Catalog.MutImplementation(location, implementing, description));
                else if (LosesUnsafe(implementation.Symbol, method))
                    Error(Catalog.UnsafeImplementation(location, implementing, description));
                else
                {
                    if (!implementation.Symbol.Overridden.Contains(method))
                        implementation.Symbol.Overridden.Add(method);
                    if (s.Kind == TypeKind.Class)
                    {
                        implementation.Symbol.ImplementsInterface = true;
                        if (implementation.Owner!.Symbol != s
                            && !inheritedInterfaces.Contains(iface) && !Conversions.IsSubtype(implementation.Owner, iface)
                            && !s.InheritedImplementations.Any(m => m.Symbol == implementation.Symbol))
                            s.InheritedImplementations.Add(implementation);
                    }
                }
            }
        }
    }

    static string ParameterList(MethodReference method) =>
        string.Join(", ", Enumerable.Range(0, method.Symbol.Parameters.Count)
            .Select(i => (method.ParameterType(i).NonNull.IsEscaping ? "escaping " : "") + FunctionSymbol.ParameterText(method.ParameterRefKind(i), method.ParameterType(i))));

    void CheckOverride(TypeSymbol s, FunctionSymbol method)
    {
        var location = method.Declaration!.NameLocation;
        var key = MethodKey(method, TypeMap.Empty);
        MethodReference? inherited = null;
        for (var t = s.Type.BaseType; t is not null && inherited is null; t = t.BaseType)
            inherited = FindMethod(t, key);

        if (!method.IsOverride)
        {
            if (inherited is null)
                return;
            if (!inherited.Symbol.Reach.Includes(location.Source))
            {
                method.Shadows = inherited.Symbol;
                return;
            }
            var name = $"{inherited.Owner!.Symbol!.Name}.{inherited.Symbol.Name}({ParameterList(inherited)})";
            Error(inherited.Symbol.IsOverridable && !method.IsStatic
                ? Catalog.MissingOverride(location, method.Signature, name)
                : Catalog.HidesInherited(location, method.Signature, name));
            return;
        }

        if (inherited is null || !inherited.Symbol.Reach.Includes(location.Source))
        {
            Error(Catalog.OverridesNothing(location, method.Signature));
            return;
        }
        method.Overridden.Add(inherited.Symbol);
        var overridden = $"{inherited.Owner!.Symbol!.Name}.{inherited.Symbol.Name}";
        if (inherited.Symbol.IsFinal)
            Error(Catalog.OverrideFinal(location, overridden));
        else if (!inherited.Symbol.IsOverridable || inherited.Symbol.IsStatic)
            Error(Catalog.OverrideNonVirtual(location, overridden));
        else if (!HasSameTypes(new MethodReference(method, s.Type, []), inherited))
            Error(Catalog.OverrideSignature(location, method.Name, overridden, FindEscapingDifference(new MethodReference(method, s.Type, []), inherited)));
        // So that a method reaches as far in every subclass as in its base (spec classes.overriding).
        else if (method.Declaration.Visibility != inherited.Symbol.Declaration!.Visibility)
            Error(Catalog.OverrideVisibility(location, method.Name, overridden, inherited.Symbol.Declaration.Visibility));
        else if (LosesUnsafe(method, inherited.Symbol))
            Error(Catalog.UnsafeOverride(location, method.Name, overridden));
    }

    /// <summary>
    /// A call through a method that isn't <c>unsafe</c> needs no unsafe code, so nothing that call
    /// may dispatch to can be <c>unsafe</c> (spec unsafe.blocks). The reverse is allowed.
    /// </summary>
    static bool LosesUnsafe(FunctionSymbol implementation, FunctionSymbol declared) => implementation.IsUnsafe && !declared.IsUnsafe;

    /// <summary>A method of <paramref name="type"/> itself (not inherited) with the given <see cref="MethodKey"/>.</summary>
    static MethodReference? FindMethod(WylType type, string key)
    {
        var map = TypeMap.For(type);
        var name = key[..key.IndexOf('|')];
        if (type.Symbol is null || !type.Symbol.Methods.TryGetValue(name, out var methods))
            return null;
        return methods.Where(m => MethodKey(m, map) == key).Select(m => new MethodReference(m, type, [])).FirstOrDefault();
    }

    /// <summary>The most derived non-abstract method with the given key in a class or struct and its base classes.</summary>
    static MethodReference? FindImplementation(WylType type, string key)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (FindMethod(t, key) is { } method)
                return method.Symbol.IsAbstract ? null : method;
        }
        return null;
    }
}
