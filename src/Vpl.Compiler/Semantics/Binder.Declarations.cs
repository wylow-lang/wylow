using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

public sealed partial class Binder
{
    void AddFunction(FunctionSymbol function)
    {
        if (!functions.TryGetValue(function.Name, out var overloads))
            functions[function.Name] = overloads = [];
        overloads.Add(function);
    }

    List<TypeParameterSymbol> DeclareTypeParameters(IReadOnlyList<TypeParameterSyntax> syntax, IReadOnlyList<TypeParameterSymbol> outer)
    {
        var result = new List<TypeParameterSymbol>();
        foreach (var parameter in syntax)
        {
            if (result.Any(p => p.Name == parameter.Name))
                Error(parameter.Location, $"duplicate type parameter '{parameter.Name}'");
            else if (outer.Any(p => p.Name == parameter.Name))
                Error(parameter.Location, $"type parameter '{parameter.Name}' hides the type parameter of the same name of its type");
            result.Add(new TypeParameterSymbol(parameter.Name));
        }
        return result;
    }

    /// <summary>Binds bounds with <see cref="typeParameters"/> already in scope, so a bound may mention them.</summary>
    void BindBounds(IReadOnlyList<TypeParameterSyntax> syntax, IReadOnlyList<TypeParameterSymbol> symbols)
    {
        for (int i = 0; i < syntax.Count; i++)
        {
            foreach (var boundSyntax in syntax[i].Bounds)
            {
                var bound = BindType(boundSyntax, allowVoid: false);
                if (bound == VplType.Error)
                    continue;
                if (!bound.IsInterface || bound.IsNullable)
                    Error(boundSyntax.Location, $"'{bound}' cannot be a bound; type parameters can only be bounded by interfaces");
                else
                    symbols[i].Bounds.Add(bound);
            }
        }
    }

    TypeSymbol? DeclareType(TypeDecl decl)
    {
        if (types.ContainsKey(decl.Name) || BuiltinType(decl.Name) is not null)
        {
            Error(decl.NameLocation, $"'{decl.Name}' is already defined");
            return null;
        }
        var symbol = new TypeSymbol(decl, DeclareTypeParameters(decl.TypeParameters, []));
        types[decl.Name] = symbol;
        return symbol;
    }

    void BindSupertypes(TypeSymbol s)
    {
        var decl = s.Declaration;
        if (decl.BaseType is { } baseSyntax && BindType(baseSyntax, allowVoid: false) is var baseType && baseType != VplType.Error)
        {
            if (!baseType.IsClass)
                Error(baseSyntax.Location, baseType.IsInterface
                    ? $"a class cannot extend interface '{baseType}'; use 'implements'"
                    : $"'{baseType}' is not a class and cannot be extended");
            else if (baseType.IsNullable)
                Error(baseSyntax.Location, "a base class cannot be nullable");
            else if (baseType.Symbol!.IsFinal)
                Error(baseSyntax.Location, $"cannot extend final class '{baseType}'");
            else
                s.BaseType = baseType;
        }

        foreach (var interfaceSyntax in decl.Interfaces)
        {
            var type = BindType(interfaceSyntax, allowVoid: false);
            if (type == VplType.Error)
                continue;
            if (!type.IsInterface || type.IsNullable)
                Error(interfaceSyntax.Location, $"'{type}' is not an interface");
            else if (s.Interfaces.Any(i => i.Symbol == type.Symbol))
                Error(interfaceSyntax.Location, $"'{type.Symbol!.Name}' is listed more than once");
            else
                s.Interfaces.Add(type);
        }
    }

    void CheckInheritanceCycles(List<TypeSymbol> all)
    {
        static IEnumerable<TypeSymbol> Direct(TypeSymbol s) =>
            (s.BaseType is { } b ? [b, .. s.Interfaces] : s.Interfaces).Select(t => t.Symbol!);

        foreach (var s in all)
        {
            var seen = new HashSet<TypeSymbol>();
            var pending = new Stack<TypeSymbol>(Direct(s));
            while (pending.TryPop(out var next))
            {
                if (next == s)
                {
                    Error(s.Declaration.NameLocation, $"'{s.Name}' inherits from itself");
                    s.BaseType = null;
                    s.Interfaces.Clear();
                    break;
                }
                if (seen.Add(next))
                {
                    foreach (var super in Direct(next))
                        pending.Push(super);
                }
            }
        }
    }

    FunctionSymbol DeclareFunction(FunctionDecl decl)
    {
        var ownTypeParameters = DeclareTypeParameters(decl.TypeParameters, []);
        typeParameters = ownTypeParameters;
        BindBounds(decl.TypeParameters, ownTypeParameters);
        var function = new FunctionSymbol(decl.Name, BindParameters(decl.Parameters), BindType(decl.ReturnType!, allowVoid: true))
        {
            Declaration = decl,
            Body = decl.Body,
            TypeParameters = ownTypeParameters,
        };
        typeParameters = [];

        if (types.TryGetValue(decl.Name, out var type))
        {
            Error(decl.NameLocation, $"'{decl.Name}' is already defined as a {KindName(type.Kind)}");
            return function;
        }

        var existing = FindSameSignature(functions.GetValueOrDefault(decl.Name), function);
        if (existing is null)
            AddFunction(function);
        else if (existing.IsBuiltin)
            Error(decl.NameLocation, $"'{function.Signature}' conflicts with a built-in function");
        else
            Error(decl.NameLocation, $"function '{function.Signature}' is already defined");
        return function;
    }

    /// <summary>
    /// Identifies a method signature as seen from a type whose type arguments <paramref name="map"/>
    /// applies. A function's own type parameters compare by position. Nullability is ignored, since
    /// <c>T</c> and <c>T?</c> are the same type at runtime and can't be told apart by overloading.
    /// </summary>
    static string MethodKey(FunctionSymbol function, TypeMap map)
    {
        string Key(VplType type) =>
            type.IsNullable ? Key(type.NonNull)
            : type.TypeParameter is { } p && function.TypeParameters.Contains(p) ? $"#{function.TypeParameters.ToList().IndexOf(p)}"
            : type.Symbol is { } s && type.TypeArguments.Count > 0 ? $"{s.Name}<{string.Join(",", type.TypeArguments.Select(Key))}>"
            : type.Name;
        var parameters = string.Join(",", function.Parameters.Select(p => p.RefKind + Key(map.Apply(p.Type))));
        return $"{function.Name}|{function.TypeParameters.Count}|{parameters}";
    }

    static FunctionSymbol? FindSameSignature(IEnumerable<FunctionSymbol>? candidates, FunctionSymbol function) =>
        candidates?.FirstOrDefault(f => MethodKey(f, TypeMap.Empty) == MethodKey(function, TypeMap.Empty));

    /// <summary>Whether two methods have the same parameter and return types, including nullability.</summary>
    static bool HasSameTypes(MethodReference a, MethodReference b) =>
        a.ReturnType == b.ReturnType
        && Enumerable.Range(0, a.Symbol.Parameters.Count).All(i => a.ParameterType(i) == b.ParameterType(i));

    List<LocalSymbol> BindParameters(IReadOnlyList<ParameterSyntax> parameters)
    {
        var result = new List<LocalSymbol>();
        foreach (var parameter in parameters)
        {
            if (result.Any(p => p.Name == parameter.Name))
                Error(parameter.Location, $"duplicate parameter '{parameter.Name}'");
            var type = BindType(parameter.Type, allowVoid: false);
            if (parameter.RefKind != RefKind.None && type.IsReference)
            {
                Error(parameter.Location,
                    $"'{FunctionSymbol.RefPrefix(parameter.RefKind).Trim()}' cannot be used on a parameter of type '{type}'; class objects are already passed by reference");
            }
            result.Add(new LocalSymbol(parameter.Name, type, isParameter: true, parameter.RefKind));
        }
        return result;
    }

    void DeclareMembers(TypeSymbol s)
    {
        typeParameters = s.TypeParameters;
        if (s.Kind == TypeKind.Enum)
            DeclareVariants(s);
        foreach (var decl in s.Declaration.Fields)
        {
            var field = new FieldSymbol(decl.Name, BindType(decl.Type, allowVoid: false), s, decl);
            if (field.IsWeak && field.Type != VplType.Error && !(field.Type.IsReference && field.Type.IsNullable))
                Error(decl.Type.Location, $"a 'weak' field must have a nullable class or interface type, such as '{field.Type}?'");
            if (s.FindField(decl.Name) is not null)
                Error(decl.NameLocation, $"field '{decl.Name}' is already defined");
            else
                s.Fields.Add(field);
        }

        foreach (var decl in s.Declaration.Methods)
        {
            var ownTypeParameters = DeclareTypeParameters(decl.TypeParameters, s.TypeParameters);
            typeParameters = [.. s.TypeParameters, .. ownTypeParameters];
            BindBounds(decl.TypeParameters, ownTypeParameters);
            var parameters = BindParameters(decl.Parameters);
            var modifiers = decl.Modifiers;
            bool isPrivate = (modifiers & Modifiers.Private) != 0;
            if (decl.IsConstructor)
            {
                if (parameters.Count == 0 && s.Kind == TypeKind.Struct)
                {
                    Error(decl.NameLocation, "structs cannot declare a parameterless constructor; use field initializers instead");
                    continue;
                }
                var constructor = new FunctionSymbol(s.Name, parameters, VplType.Void)
                {
                    Declaration = decl,
                    Body = decl.Body,
                    ContainingType = s,
                    IsConstructor = true,
                    IsPrivate = isPrivate,
                };
                if (FindSameSignature(s.Constructors, constructor) is not null)
                {
                    Error(decl.NameLocation, $"constructor '{constructor.Signature}' is already defined");
                    continue;
                }
                s.Constructors.Add(constructor);
                s.Members.Add(constructor);
                continue;
            }

            bool isInterface = s.Kind == TypeKind.Interface;
            var method = new FunctionSymbol(decl.Name, parameters, BindType(decl.ReturnType!, allowVoid: true))
            {
                Declaration = decl,
                Body = decl.Body,
                TypeParameters = ownTypeParameters,
                ContainingType = s,
                IsStatic = (modifiers & Modifiers.Static) != 0,
                // An enum value cannot be modified, so its methods never modify `this`.
                IsReadOnly = (modifiers & Modifiers.ReadOnly) != 0 || (s.Kind == TypeKind.Enum && (modifiers & Modifiers.Static) == 0),
                IsPrivate = isPrivate,
                IsVirtual = (modifiers & Modifiers.Virtual) != 0,
                IsOverride = (modifiers & Modifiers.Override) != 0,
                IsAbstract = isInterface || (modifiers & Modifiers.Abstract) != 0,
                IsFinal = (modifiers & Modifiers.Final) != 0,
            };
            // C++ templates cannot be virtual.
            if (method.IsGeneric && isInterface)
                Error(decl.NameLocation, "interface methods cannot have type parameters");
            else if (method.IsGeneric && (method.IsVirtual || method.IsOverride || method.IsAbstract))
                Error(decl.NameLocation, "generic methods cannot be 'virtual', 'override', or 'abstract'");
            if ((modifiers & Modifiers.Abstract) != 0 && !s.IsAbstract)
                Error(decl.NameLocation, $"'{s.Name}' must be declared 'abstract' because it has abstract method '{decl.Name}'");
            if (s.FindField(decl.Name) is not null || s.FindVariant(decl.Name) is not null)
            {
                Error(decl.NameLocation, $"'{decl.Name}' is already defined as a {(s.Kind == TypeKind.Enum ? "variant" : "field")}");
                continue;
            }
            if (!s.Methods.TryGetValue(decl.Name, out var overloads))
                s.Methods[decl.Name] = overloads = [];
            if (FindSameSignature(overloads, method) is not null)
            {
                Error(decl.NameLocation, $"method '{method.Signature}' is already defined");
                continue;
            }
            overloads.Add(method);
            s.Members.Add(method);
        }

        if (s.Declaration.Destructor is { } destructor)
        {
            s.Destructor = new FunctionSymbol("~" + s.Name, [], VplType.Void)
            {
                Body = destructor.Body,
                ContainingType = s,
                IsDestructor = true,
            };
            s.Members.Add(s.Destructor);
        }
        typeParameters = [];
    }

    void DeclareVariants(TypeSymbol s)
    {
        foreach (var decl in s.Declaration.Variants)
        {
            var fields = new List<FieldSymbol>();
            foreach (var parameter in decl.Fields ?? [])
            {
                if (fields.Any(f => f.Name == parameter.Name))
                    Error(parameter.Location, $"duplicate field '{parameter.Name}'");
                var fieldDecl = new FieldDecl(parameter.Location, Modifiers.None, parameter.Type, parameter.Name, parameter.Location, null);
                fields.Add(new FieldSymbol(parameter.Name, BindType(parameter.Type, allowVoid: false), s, fieldDecl));
            }
            if (s.FindVariant(decl.Name) is not null)
                Error(decl.NameLocation, $"variant '{decl.Name}' is already defined");
            else
                s.Variants.Add(new VariantSymbol(decl.Name, s, s.Variants.Count, decl, fields));
        }

        if (s.Declaration.UnderlyingType is { } underlyingSyntax)
        {
            var underlying = BindType(underlyingSyntax, allowVoid: false);
            if (s.HasData)
                Error(underlyingSyntax.Location, "an enum whose variants carry data cannot have an underlying type");
            else if (underlying != VplType.Error && !underlying.IsInteger)
                Error(underlyingSyntax.Location, $"an enum's underlying type must be an integer type, not '{underlying}'");
            else
                s.UnderlyingType = underlying;
        }

        long next = 0;
        var used = new Dictionary<long, VariantSymbol>();
        foreach (var variant in s.Variants)
        {
            if (variant.Declaration.Value is { } valueSyntax)
            {
                if (s.HasData)
                {
                    Error(valueSyntax.Location, "only enums whose variants carry no data can give variants values");
                    continue;
                }
                s.UnderlyingType ??= VplType.Int;
                if (VariantValue(valueSyntax, s.UnderlyingType) is not { } value)
                    continue;
                next = value;
            }
            variant.Value = next;
            if (used.TryGetValue(next, out var other))
                Error(variant.Declaration.NameLocation, $"'{variant.Name}' has the same value as '{other.Name}'");
            used.TryAdd(next, variant);
            next = next == long.MaxValue ? next : next + 1;
        }
        if (s.UnderlyingType == VplType.Int && s.Variants.FirstOrDefault(v => v.Value is > int.MaxValue or < int.MinValue) is { } tooLarge)
            Error(tooLarge.Declaration.NameLocation, $"the value of '{tooLarge.Name}' does not fit in the underlying type 'int'");
    }

    long? VariantValue(Expr syntax, VplType underlying)
    {
        var (literal, negated) = syntax switch
        {
            IntLiteralExpr l => (l, false),
            UnaryExpr { Operator: TokenKind.Minus, Operand: IntLiteralExpr l } => (l, true),
            _ => ((IntLiteralExpr?)null, false),
        };
        if (literal is null)
        {
            Error(syntax.Location, "a variant's value must be an integer literal");
            return null;
        }
        ulong limit = (underlying == VplType.Long ? (ulong)long.MaxValue : int.MaxValue) + (negated ? 1UL : 0UL);
        if (literal.Value > limit)
        {
            Error(syntax.Location, $"the value does not fit in the underlying type '{underlying}'");
            return null;
        }
        return negated ? unchecked((long)(0UL - literal.Value)) : (long)literal.Value;
    }

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
                    Error(location, $"'{implementation.Owner!.Symbol!.Name}.{method.Name}' must have the same parameter and return types as {description} to implement it");
                else if (s.Kind == TypeKind.Class && implementation.Owner!.Symbol != s
                         && !inheritedInterfaces.Contains(iface) && !Conversions.IsSubtype(implementation.Owner, iface)
                         && !s.InheritedImplementations.Any(m => m.Symbol == implementation.Symbol))
                    s.InheritedImplementations.Add(implementation);
            }
        }
    }

    static string ParameterList(MethodReference method) =>
        string.Join(", ", Enumerable.Range(0, method.Symbol.Parameters.Count)
            .Select(i => FunctionSymbol.RefPrefix(method.ParameterRefKind(i)) + method.ParameterType(i)));

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
        var overridden = $"'{inherited.Owner!.Symbol!.Name}.{inherited.Symbol.Name}'";
        if (inherited.Symbol.IsFinal)
            Error(location, $"cannot override {overridden} because it is final");
        else if (!inherited.Symbol.IsOverridable || inherited.Symbol.IsStatic)
            Error(location, $"cannot override {overridden} because it is not 'virtual'");
        else if (!HasSameTypes(new MethodReference(method, s.Type, []), inherited))
            Error(location, $"'{method.Name}' must have the same parameter and return types as {overridden} to override it");
    }

    /// <summary>A method of <paramref name="type"/> itself (not inherited) with the given <see cref="MethodKey"/>.</summary>
    static MethodReference? FindMethod(VplType type, string key)
    {
        var map = TypeMap.For(type);
        var name = key[..key.IndexOf('|')];
        if (type.Symbol is null || !type.Symbol.Methods.TryGetValue(name, out var methods))
            return null;
        return methods.Where(m => MethodKey(m, map) == key).Select(m => new MethodReference(m, type, [])).FirstOrDefault();
    }

    /// <summary>The most derived non-abstract method with the given key in a class or struct and its base classes.</summary>
    static MethodReference? FindImplementation(VplType type, string key)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (FindMethod(t, key) is { } method)
                return method.Symbol.IsAbstract ? null : method;
        }
        return null;
    }

    /// <summary>
    /// A struct or enum may not contain itself by value, directly or through type arguments
    /// (<c>struct Rec { Wrap&lt;Rec&gt; w; }</c>), and its field types may not grow forever.
    /// </summary>
    void CheckStructCycles(List<TypeSymbol> all)
    {
        const int maxDepth = 32;
        var reported = new HashSet<TypeSymbol>();
        var acyclic = new HashSet<VplType>();

        string? FindCycle(VplType type, List<VplType> path)
        {
            if (acyclic.Contains(type))
                return null;
            int index = path.IndexOf(type);
            if (index >= 0)
            {
                var cycle = path.Skip(index).Append(type).ToList();
                foreach (var member in cycle)
                    reported.Add(member.Symbol!);
                return $"contains itself ({string.Join(" -> ", cycle)}); a struct or enum cannot hold itself by value";
            }
            if (path.Count >= maxDepth)
            {
                foreach (var member in path)
                    reported.Add(member.Symbol!);
                return "has field types that nest without end";
            }

            path.Add(type);
            var map = TypeMap.For(type);
            foreach (var field in type.Symbol!.ValueFields)
            {
                var fieldType = map.Apply(field.Type);
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
                Error(s.Declaration.NameLocation, $"{KindName(s.Kind)} '{s.Name}' {problem}");
        }
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
    }

    FunctionSymbol? FindMain(SourceText source)
    {
        if (!functions.TryGetValue("main", out var candidates))
        {
            Error(new Location(source, new TextSpan(0, 0)), "program has no 'main' function");
            return null;
        }

        FunctionSymbol? main = null;
        foreach (var candidate in candidates)
        {
            if (candidate.Parameters.Count != 0 || candidate.IsGeneric)
                Error(candidate.Declaration!.NameLocation, "'main' must not take parameters or type parameters");
            else if (candidate.ReturnType != VplType.Void && candidate.ReturnType != VplType.Int)
                Error(candidate.Declaration!.NameLocation, "'main' must return 'void' or 'int'");
            else
                main = candidate;
        }
        return main;
    }
}
