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
                    Error(method.Declaration!.NameLocation, $"'{method.Signature}' is already declared in interface '{inherited.Owner}'");
            }
            return;
        }

        if (s.Kind == TypeKind.Class)
        {
            foreach (var field in s.Fields)
            {
                if (self.BaseType is { } baseType && LookupField(baseType, field.Name) is { } inherited)
                    Error(field.Declaration.NameLocation, $"field '{field.Name}' is already defined in base class '{inherited.Field.Owner.Name}'");
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
                            Error(s.Declaration.NameLocation, $"'{s.Name}' must implement abstract method '{t.Symbol.Name}.{method.Name}({ParameterList(new MethodReference(method, t, []))})'");
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
                var description = $"'{iface}.{method.Name}({ParameterList(required)})'";
                var implementation = FindImplementation(self, MethodKey(method, TypeMap.For(iface)));
                if (implementation is null)
                {
                    if (!s.IsAbstract)
                        Error(s.Declaration.NameLocation, $"'{s.Name}' does not implement {description}");
                    continue;
                }
                var location = implementation.Owner!.Symbol == s ? implementation.Symbol.Declaration!.NameLocation : s.Declaration.NameLocation;
                if (implementation.Symbol.IsStatic)
                    Error(location, $"'{implementation.Owner!.Symbol!.Name}.{method.Name}' is static and cannot implement {description}");
                else if (implementation.Symbol.IsPrivate)
                    Error(location, $"'{implementation.Owner!.Symbol!.Name}.{method.Name}' implements {description} and cannot be private");
                else if (!HasSameTypes(implementation, required))
                    Error(location, $"'{implementation.Owner!.Symbol!.Name}.{method.Name}' must have the same parameter and return types as {description} to implement it{EscapingDifference(implementation, required, description)}");
                // A class method can't change the reference it is called through, so only a struct's must promise not to change 'this'.
                else if (method.IsReadOnly && s.Kind == TypeKind.Struct && !implementation.Symbol.IsReadOnly)
                    Error(location, $"'{implementation.Owner!.Symbol!.Name}.{method.Name}' cannot be 'mut', since {description} isn't");
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
            .Select(i => (method.ParameterType(i).NonNull.IsEscaping ? "@Escaping " : "") + FunctionSymbol.ParameterText(method.ParameterRefKind(i), method.ParameterType(i))));

    void CheckOverride(TypeSymbol s, FunctionSymbol method)
    {
        var location = method.Declaration!.NameLocation;
        var key = MethodKey(method, TypeMap.Empty);
        MethodReference? inherited = null;
        for (var t = s.Type.BaseType; t is not null && inherited is null; t = t.BaseType)
            inherited = FindMethod(t, key);

        if (!method.IsOverride)
        {
            if (inherited is null || inherited.Symbol.IsPrivate)
                return;
            var name = $"'{inherited.Owner!.Symbol!.Name}.{inherited.Symbol.Name}({ParameterList(inherited)})'";
            Error(location, inherited.Symbol.IsOverridable && !method.IsStatic
                ? $"'{method.Signature}' must be marked 'override' to override {name}"
                : $"'{method.Signature}' hides inherited method {name}; only 'virtual' methods can be overridden");
            return;
        }

        if (inherited is null || inherited.Symbol.IsPrivate)
        {
            Error(location, $"'{method.Signature}' does not override anything; no base class has a method with these parameters");
            return;
        }
        method.Overridden.Add(inherited.Symbol);
        var overridden = $"'{inherited.Owner!.Symbol!.Name}.{inherited.Symbol.Name}'";
        if (inherited.Symbol.IsFinal)
            Error(location, $"cannot override {overridden} because it is final");
        else if (!inherited.Symbol.IsOverridable || inherited.Symbol.IsStatic)
            Error(location, $"cannot override {overridden} because it is not 'virtual'");
        else if (!HasSameTypes(new MethodReference(method, s.Type, []), inherited))
            Error(location, $"'{method.Name}' must have the same parameter and return types as {overridden} to override it{EscapingDifference(new MethodReference(method, s.Type, []), inherited, overridden)}");
    }

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
