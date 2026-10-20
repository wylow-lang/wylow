using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

public sealed class Binder(DiagnosticBag diagnostics)
{
    readonly Dictionary<string, List<FunctionSymbol>> functions = new();
    readonly Dictionary<string, TypeSymbol> types = new();

    // Bounds may mention types whose own bounds are not bound yet, so satisfaction is checked last.
    readonly List<(VplType Argument, TypeParameterSymbol Parameter, TypeMap Map, Location Location)> boundChecks = [];

    /// <summary>The prelude's <c>Result</c>, which <c>try</c> and the unused-result check know about.</summary>
    TypeSymbol? resultSymbol;

    FunctionSymbol? currentFunction;
    TypeSymbol? currentType;
    IReadOnlyList<TypeParameterSymbol> typeParameters = [];
    bool hasThis;
    NoThisContext noThisContext;
    Scope? scope;
    int loopDepth;

    sealed class Scope(Scope? parent)
    {
        readonly Dictionary<string, LocalSymbol> locals = new();

        public Scope? Parent { get; } = parent;

        public LocalSymbol? Lookup(string name)
        {
            for (var s = this; s is not null; s = s.Parent)
            {
                if (s.locals.TryGetValue(name, out var local))
                    return local;
            }
            return null;
        }

        public void Add(LocalSymbol local) => locals[local.Name] = local;
    }

    sealed record Argument(BoundExpression Expression, bool IsRef, Location Location);

    /// <summary>A method or constructor that may be called, as a member of <see cref="Owner"/>.</summary>
    sealed record Candidate(FunctionSymbol Symbol, VplType? Owner);

    enum PlaceKind { Temporary, Mutable, ReadOnly }

    enum NoThisContext { Static, FieldInitializer, SuperArguments }

    public BoundProgram BindProgram(CompilationUnit unit)
    {
        foreach (var builtin in Builtins.All)
            AddFunction(builtin);

        var typeList = new List<TypeSymbol>();
        foreach (var decl in unit.Declarations.OfType<TypeDecl>())
        {
            if (DeclareType(decl) is not { } symbol)
                continue;
            typeList.Add(symbol);
            if (decl.Location.Source == Prelude.Source && decl.Name == "Result")
                resultSymbol = symbol;
        }
        foreach (var s in typeList)
        {
            typeParameters = s.TypeParameters;
            BindBounds(s.Declaration.TypeParameters, s.TypeParameters);
            BindSupertypes(s);
            typeParameters = [];
        }
        CheckInheritanceCycles(typeList);

        var functionList = unit.Declarations.OfType<FunctionDecl>().Select(DeclareFunction).ToList();
        foreach (var s in typeList)
            DeclareMembers(s);
        foreach (var s in typeList)
            CheckMembers(s);
        CheckStructCycles([.. typeList.Where(s => s.Kind is TypeKind.Struct or TypeKind.Enum)]);
        foreach (var s in typeList)
            BindFieldInitializers(s);

        var bodies = new List<BoundFunction>();
        foreach (var s in typeList)
        {
            foreach (var member in s.Members.Where(m => m.Body is not null))
                bodies.Add(new BoundFunction(member, BindBody(member)));
            if (s.Kind == TypeKind.Class && s.Constructors.Count == 0)
                bodies.Add(BindImplicitConstructor(s));
        }
        foreach (var function in functionList)
            bodies.Add(new BoundFunction(function, BindBody(function)));

        CheckBounds();
        return new BoundProgram(unit.Source, typeList, bodies, FindMain(unit.Source));
    }

    void Error(Location location, string message) => diagnostics.Error(location, message);

    static string TextOf(Location location) => location.Source.Text.Substring(location.Span.Start, location.Span.Length);

    static string KindName(TypeKind kind) => kind switch
    {
        TypeKind.Struct => "struct",
        TypeKind.Class => "class",
        TypeKind.Enum => "enum",
        _ => "interface",
    };

    // ---- Declarations ----

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

    // ---- Types ----

    static VplType? BuiltinType(string name) => name switch
    {
        "void" => VplType.Void,
        "bool" => VplType.Bool,
        "int" => VplType.Int,
        "long" => VplType.Long,
        "float" => VplType.Float,
        "double" => VplType.Double,
        "String" => VplType.String,
        _ => null,
    };

    TypeParameterSymbol? LookupTypeParameter(string name) => typeParameters.LastOrDefault(p => p.Name == name);

    VplType BindType(TypeSyntax syntax, bool allowVoid)
    {
        var type = BindTypeName(syntax.Name, syntax.TypeArguments, syntax.Location);
        if (type == VplType.Void && !allowVoid)
        {
            Error(syntax.Location, "'void' cannot be used here");
            return VplType.Error;
        }
        if (!syntax.IsNullable || type == VplType.Error)
            return type;
        if (type.IsReference)
            return type.Nullable;
        Error(syntax.Location, type.IsTypeParameter
            ? $"type parameter '{type}' cannot be nullable, since it may stand for a struct"
            : $"'{type}?' is not supported: only class and interface types can be nullable for now");
        return VplType.Error;
    }

    VplType BindTypeName(string name, IReadOnlyList<TypeSyntax> typeArgumentSyntax, Location location)
    {
        var arguments = typeArgumentSyntax.Select(a => BindType(a, allowVoid: false)).ToList();
        if (arguments.Contains(VplType.Error))
            return VplType.Error;

        if (LookupTypeParameter(name) is { } parameter)
            return arguments.Count == 0 ? parameter.Type : NotGeneric(name, location);
        if (BuiltinType(name) is { } builtin)
            return arguments.Count == 0 ? builtin : NotGeneric(name, location);
        if (types.TryGetValue(name, out var s))
            return Construct(s, arguments, location);

        Error(location, $"unknown type '{name}'");
        return VplType.Error;
    }

    VplType NotGeneric(string name, Location location)
    {
        Error(location, $"'{name}' is not generic");
        return VplType.Error;
    }

    VplType Construct(TypeSymbol s, IReadOnlyList<VplType> arguments, Location location)
    {
        if (arguments.Count == s.TypeParameters.Count)
        {
            QueueBoundChecks(s.TypeParameters, arguments, location);
            return s.Construct(arguments);
        }
        if (!s.IsGeneric)
            return NotGeneric(s.Name, location);
        int count = s.TypeParameters.Count;
        Error(location, $"'{s.Name}' requires {count} type argument{(count == 1 ? "" : "s")}");
        return VplType.Error;
    }

    void QueueBoundChecks(IReadOnlyList<TypeParameterSymbol> parameters, IReadOnlyList<VplType> arguments, Location location)
    {
        var map = TypeMap.Empty.With(parameters, arguments);
        foreach (var (parameter, argument) in parameters.Zip(arguments))
            boundChecks.Add((argument, parameter, map, location));
    }

    void CheckBounds()
    {
        foreach (var (argument, parameter, map, location) in boundChecks)
        {
            foreach (var bound in parameter.Bounds.Select(map.Apply))
            {
                if (argument != VplType.Error && !Conversions.IsSubtype(argument, bound))
                    Error(location, $"'{argument}' does not implement '{bound}', which type parameter '{parameter.Name}' requires");
            }
        }
    }

    IReadOnlyList<VplType>? BindTypeArguments(IReadOnlyList<TypeSyntax> syntax) =>
        syntax.Count == 0 ? null : [.. syntax.Select(a => BindType(a, allowVoid: false))];

    // ---- Member lookup ----

    /// <summary>A field of the type or, for classes, of a base class, with the type it was found in.</summary>
    static (FieldSymbol Field, VplType Owner)? LookupField(VplType type, string name)
    {
        for (var t = type.NonNull; t?.Symbol is not null; t = t.BaseType)
        {
            if (t.Symbol.FindField(name) is { } field)
                return (field, t);
        }
        return null;
    }

    /// <summary>
    /// Methods callable on a value of the type: its own, then inherited ones that it does not
    /// override. A type parameter has the methods of its bounds.
    /// </summary>
    static List<Candidate> LookupMethods(VplType type, string name)
    {
        var result = new List<Candidate>();
        var seen = new HashSet<string>();
        IEnumerable<VplType> searched = type.IsTypeParameter ? Conversions.Supertypes(type) : [type, .. Conversions.Supertypes(type)];
        foreach (var t in searched)
        {
            if (t.Symbol is null || !t.Symbol.Methods.TryGetValue(name, out var methods))
                continue;
            var map = TypeMap.For(t);
            foreach (var method in methods)
            {
                if (seen.Add(MethodKey(method, map)))
                    result.Add(new Candidate(method, t));
            }
        }
        return result;
    }

    static string DisplayName(VplType type) => type.Symbol?.Name ?? type.Name;

    // ---- Bodies and statements ----

    BoundBlock BindBody(FunctionSymbol function)
    {
        currentFunction = function;
        currentType = function.ContainingType;
        typeParameters = [.. currentType?.TypeParameters ?? [], .. function.TypeParameters];
        hasThis = currentType is not null && !function.IsStatic;
        scope = new Scope(null);
        foreach (var parameter in function.Parameters)
            scope.Add(parameter);

        var body = function.IsConstructor && currentType!.Kind == TypeKind.Class
            ? BindClassConstructorBody(function)
            : BindBlock(function.Body!);

        currentFunction = null;
        currentType = null;
        typeParameters = [];
        hasThis = false;
        scope = null;
        return body;
    }

    /// <summary>Binds a class constructor, whose first statement is always the call to the base class constructor.</summary>
    BoundBlock BindClassConstructorBody(FunctionSymbol constructor)
    {
        var block = constructor.Body!;
        var statements = new List<BoundStatement>();
        var rest = block.Statements;
        if (block.Statements.FirstOrDefault() is ExprStmt { Expression: CallExpr { Callee: SuperExpr } call })
        {
            if (BindSuperCall(call) is { } super)
                statements.Add(super);
            rest = [.. block.Statements.Skip(1)];
        }
        else if (ImplicitSuperCall(currentType!, constructor.Declaration!.NameLocation) is { } super)
        {
            statements.Add(super);
        }

        scope = new Scope(scope);
        statements.AddRange(rest.Select(BindStatement));
        scope = scope.Parent;
        return new BoundBlock(block.Location, statements);
    }

    BoundSuperCall? BindSuperCall(CallExpr call)
    {
        hasThis = false;
        noThisContext = NoThisContext.SuperArguments;
        var arguments = BindArguments(call.Arguments);
        hasThis = true;
        noThisContext = NoThisContext.Static;

        if (currentType!.Type.BaseType is not { } baseType)
        {
            Error(call.Callee.Location, $"'{currentType.Name}' has no base class, so it cannot call 'super(...)'");
            return null;
        }
        if (arguments.Any(a => a.Expression.Type == VplType.Error))
            return null;
        var constructor = Resolve(call.Location, $"constructor of '{baseType}'", ConstructorsOf(baseType), null, arguments);
        if (constructor is null)
            return null;
        CheckAccessible(call.Location, baseType.Symbol!, constructor.Symbol.IsPrivate, $"constructor '{constructor}'");
        return new BoundSuperCall(call.Location, constructor, ConvertArguments(constructor, arguments));
    }

    /// <summary>The base constructor call of a constructor without <c>super(...)</c>: the base's parameterless constructor.</summary>
    BoundSuperCall? ImplicitSuperCall(TypeSymbol s, Location location)
    {
        if (s.Type.BaseType is not { } baseType)
            return null;
        var constructor = ConstructorsOf(baseType).FirstOrDefault(c => c.Symbol.Parameters.Count == 0);
        if (constructor is null)
        {
            Error(location, $"'{baseType}' has no parameterless constructor, so this constructor must start with 'super(...)'");
            return null;
        }
        var reference = new MethodReference(constructor.Symbol, baseType, []);
        CheckAccessible(location, baseType.Symbol!, reference.Symbol.IsPrivate, $"constructor '{reference}'");
        return new BoundSuperCall(location, reference, []);
    }

    static List<Candidate> ConstructorsOf(VplType type)
    {
        var s = type.Symbol!;
        if (s.Kind == TypeKind.Struct)
            return [.. s.Constructors.Append(s.DefaultConstructor).Select(c => new Candidate(c, type))];
        return [.. (s.Constructors.Count > 0 ? s.Constructors : [s.DefaultConstructor]).Select(c => new Candidate(c, type))];
    }

    BoundFunction BindImplicitConstructor(TypeSymbol s)
    {
        var location = s.Declaration.NameLocation;
        foreach (var field in s.Fields.Where(f => f.RequiresAssignment))
        {
            Error(field.Declaration.NameLocation,
                $"field '{field.Name}' of type '{field.Type}' has no default value; give it an initializer or assign it in a constructor");
        }
        currentType = s;
        var super = ImplicitSuperCall(s, location);
        currentType = null;
        return new BoundFunction(s.DefaultConstructor, new BoundBlock(location, super is null ? [] : [super]));
    }

    void Declare(LocalSymbol local, Location location)
    {
        // Like Java, a local may not shadow another local or parameter (it may shadow a field).
        if (scope!.Lookup(local.Name) is not null)
            Error(location, $"'{local.Name}' is already defined");
        else
            scope.Add(local);
    }

    BoundStatement BindStatement(Stmt statement) => statement switch
    {
        BlockStmt s => BindBlock(s),
        VarDeclStmt s => BindVariableDeclaration(s),
        IfStmt s => BindIf(s),
        WhileStmt s => BindWhile(s),
        ForStmt s => BindFor(s),
        ReturnStmt s => BindReturn(s),
        BreakStmt s => BindJump(s.Location, "break", new BoundBreak(s.Location)),
        ContinueStmt s => BindJump(s.Location, "continue", new BoundContinue(s.Location)),
        ExprStmt s => BindExpressionStatement(s),
        AssignStmt s => BindAssignment(s),
        IncDecStmt s => BindIncDec(s),
        DiscardStmt s => BindDiscard(s),
        SwitchStmt s => BindSwitch(s),
        _ => throw new InvalidOperationException($"unexpected statement {statement.GetType().Name}"),
    };

    static BoundStatement ErrorStatement(Location location) =>
        new BoundExpressionStatement(location, new BoundErrorExpression(location));

    BoundBlock BindBlock(BlockStmt block)
    {
        scope = new Scope(scope);
        var statements = block.Statements.Select(BindStatement).ToList();
        scope = scope.Parent;
        return new BoundBlock(block.Location, statements);
    }

    // Names bound by `is` patterns in a condition are in scope for the whole statement.
    BoundStatement BindIf(IfStmt s)
    {
        scope = new Scope(scope);
        var result = new BoundIf(s.Location, BindCondition(s.Condition), BindBlock(s.Then), s.Else is null ? null : BindStatement(s.Else));
        scope = scope.Parent;
        return result;
    }

    BoundStatement BindVariableDeclaration(VarDeclStmt s)
    {
        var declared = s.Type is null ? null : BindType(s.Type, allowVoid: false);
        var initializer = s.Initializer is null ? null : BindValue(s.Initializer, declared);
        VplType type;
        if (declared is not null)
        {
            type = declared;
            if (initializer is not null)
                initializer = Convert(initializer, type);
        }
        else if (initializer is null)
        {
            Error(s.Location, "'var' declarations require an initializer");
            type = VplType.Error;
        }
        else if (initializer.Type == VplType.Void)
        {
            Error(initializer.Location, "cannot assign a 'void' value to a variable");
            type = VplType.Error;
        }
        else if (initializer.Type == VplType.Null)
        {
            Error(initializer.Location, "cannot infer a type from 'null'; declare the variable with a nullable type instead of 'var'");
            type = VplType.Error;
        }
        else
        {
            type = initializer.Type;
        }

        var local = new LocalSymbol(s.Name, type, isParameter: false);
        Declare(local, s.NameLocation);
        return new BoundVariableDeclaration(s.Location, local, initializer);
    }

    BoundStatement BindWhile(WhileStmt s)
    {
        scope = new Scope(scope);
        var condition = BindCondition(s.Condition);
        loopDepth++;
        var body = BindBlock(s.Body);
        loopDepth--;
        scope = scope.Parent;
        return new BoundWhile(s.Location, condition, body);
    }

    BoundStatement BindFor(ForStmt s)
    {
        scope = new Scope(scope);
        var initializer = s.Initializer is null ? null : BindStatement(s.Initializer);
        var condition = s.Condition is null ? null : BindCondition(s.Condition);
        loopDepth++;
        var body = BindBlock(s.Body);
        loopDepth--;
        var update = s.Update is null ? null : BindStatement(s.Update);
        // The update runs inside the C++ `for` header, where an early return can't be placed.
        var updateValue = update switch
        {
            BoundAssignment assignment => assignment.Value,
            BoundExpressionStatement statement => statement.Expression,
            _ => null,
        };
        if (updateValue is not null && IsTry(updateValue))
            Error(s.Update!.Location, "'try' cannot be used in a 'for' update");
        scope = scope.Parent;
        return new BoundFor(s.Location, initializer, condition, update, body);
    }

    BoundStatement BindReturn(ReturnStmt s)
    {
        var function = currentFunction!;
        if (s.Value is null)
        {
            if (function.ReturnType != VplType.Void && function.ReturnType != VplType.Error)
                Error(s.Location, $"'{function.Name}' must return a value of type '{function.ReturnType}'");
            return new BoundReturn(s.Location, null);
        }

        var value = BindValue(s.Value, function.IsConstructor || function.IsDestructor ? null : function.ReturnType);
        if (function.IsConstructor || function.IsDestructor)
        {
            Error(s.Value.Location, function.IsConstructor ? "constructors cannot return a value" : "destructors cannot return a value");
            return new BoundReturn(s.Location, null);
        }
        if (function.ReturnType == VplType.Void)
        {
            Error(s.Value.Location, $"'{function.Name}' returns 'void' and cannot return a value");
            return new BoundReturn(s.Location, null);
        }
        return new BoundReturn(s.Location, Convert(value, function.ReturnType));
    }

    BoundStatement BindJump(Location location, string keyword, BoundStatement jump)
    {
        if (loopDepth == 0)
            Error(location, $"'{keyword}' can only be used inside a loop");
        return jump;
    }

    BoundStatement BindExpressionStatement(ExprStmt s)
    {
        var expression = BindValue(s.Expression, null);
        if (expression is not (BoundCall or BoundNew or BoundTry or BoundConditionalAccess { WhenNotNull: BoundCall }) && expression.Type != VplType.Error)
            Error(s.Location, "only calls can be used as statements");
        else if (expression is BoundCall call && IsResult(call.Type))
            Error(s.Location, $"the 'Result' returned by '{call.Method.Symbol.Name}' is not used; handle it, or discard it with '_ = ...'");
        return new BoundExpressionStatement(s.Location, expression);
    }

    BoundStatement BindDiscard(DiscardStmt s)
    {
        var value = BindValue(s.Value, null);
        if (value.Type == VplType.Void)
            Error(s.Value.Location, "there is no value to discard; call it without '_ ='");
        return new BoundExpressionStatement(s.Location, value, IsDiscard: true);
    }

    BoundStatement BindAssignment(AssignStmt s)
    {
        var target = BindExpression(s.Target);
        var value = s.Operator == TokenKind.Assign ? BindValue(s.Value, target.Type) : BindExpression(s.Value);
        if (target.Type == VplType.Error || !CheckAssignable(target))
            return ErrorStatement(s.Location);

        if (s.Operator != TokenKind.Assign)
            value = BindBinaryOperation(s.OperatorLocation, target, SyntaxFacts.CompoundToBinary(s.Operator), value);
        return new BoundAssignment(s.Location, target, Convert(value, target.Type));
    }

    BoundStatement BindIncDec(IncDecStmt s)
    {
        var target = BindExpression(s.Target);
        if (target.Type == VplType.Error || !CheckAssignable(target))
            return ErrorStatement(s.Location);
        if (!target.Type.IsNumeric)
        {
            Error(s.Location, $"'{(s.IsIncrement ? "++" : "--")}' requires a numeric variable");
            return ErrorStatement(s.Location);
        }

        object one = target.Type == VplType.Int ? (object)1
            : target.Type == VplType.Long ? (object)1L
            : target.Type == VplType.Float ? (object)1f
            : (object)1d;
        var value = BindBinaryOperation(
            s.Location, target, s.IsIncrement ? TokenKind.Plus : TokenKind.Minus, new BoundLiteral(s.Location, target.Type, one));
        return new BoundAssignment(s.Location, target, value);
    }

    // ---- Places: what can be assigned or passed by ref ----

    (PlaceKind Kind, string Reason) ClassifyPlace(BoundExpression expression) => expression switch
    {
        BoundLocalAccess { Local.RefKind: RefKind.In } access => (PlaceKind.ReadOnly, $"'{access.Local.Name}' is an 'in' parameter"),
        BoundLocalAccess => (PlaceKind.Mutable, ""),
        BoundThis { Type.IsReference: true } => (PlaceKind.Temporary, ""),
        BoundThis when currentFunction is { IsReadOnly: true } => (PlaceKind.ReadOnly, "'this' is read-only in a readonly method"),
        BoundThis => (PlaceKind.Mutable, ""),
        // Fields of an object live on the heap, however the reference to it was obtained.
        BoundFieldAccess { Receiver.Type.IsReference: true } => (PlaceKind.Mutable, ""),
        BoundFieldAccess field => ClassifyPlace(field.Receiver),
        _ => (PlaceKind.Temporary, ""),
    };

    bool CheckAssignable(BoundExpression target)
    {
        var (kind, reason) = ClassifyPlace(target);
        switch (kind)
        {
            case PlaceKind.Temporary:
                Error(target.Location, target is BoundFieldAccess
                    ? "cannot assign to a field of a temporary value"
                    : "cannot assign to this expression");
                return false;
            case PlaceKind.ReadOnly:
                Error(target.Location, $"cannot assign to '{TextOf(target.Location)}' because {reason}");
                return false;
            default:
                return true;
        }
    }

    // ---- Expressions ----

    BoundExpression BindCondition(Expr condition) => Convert(BindExpression(condition), VplType.Bool);

    /// <summary>
    /// <paramref name="expected"/> is the type the value will be converted to, if known. It only lets
    /// enum variants be written without their enum or type arguments; the caller still converts.
    /// </summary>
    BoundExpression BindExpression(Expr expression, VplType? expected = null) => expression switch
    {
        IntLiteralExpr e => BindIntegerLiteral(e, e.Location, negated: false),
        FloatLiteralExpr e => e.IsDouble
            ? new BoundLiteral(e.Location, VplType.Double, e.Value)
            : new BoundLiteral(e.Location, VplType.Float, (float)e.Value),
        BoolLiteralExpr e => new BoundLiteral(e.Location, VplType.Bool, e.Value),
        StringLiteralExpr e => new BoundLiteral(e.Location, VplType.String, e.Value),
        NullLiteralExpr e => new BoundNullLiteral(e.Location),
        NameExpr e => BindName(e, expected),
        ThisExpr e => BindThis(e),
        SuperExpr e => ErrorExpression(e.Location, "'super' can only be used to call a base class constructor or method"),
        MemberAccessExpr e => BindMemberAccess(e, expected),
        ConditionalAccessExpr e => BindConditionalFieldAccess(e),
        UnaryExpr e => BindUnary(e),
        NonNullExpr e => BindNonNull(e),
        BinaryExpr e => BindBinary(e),
        CoalesceExpr e => BindCoalesce(e),
        IsExpr e => BindIs(e),
        CallExpr e => BindCall(e, expected),
        NewExpr e => BindNew(e),
        InitializerExpr e => BindInitializer(e),
        CastExpr e => BindCast(e),
        ConditionalExpr e => BindConditional(e, expected),
        SwitchExpr e => BindSwitchExpression(e, expected),
        TryExpr e => ErrorExpression(e.Location, "'try' must be the whole value of a declaration, assignment, return, or expression statement"),
        RefArgExpr e => ErrorExpression(e.Location, "'ref' can only be used on a call argument"),
        ErrorExpr e => new BoundErrorExpression(e.Location),
        _ => throw new InvalidOperationException($"unexpected expression {expression.GetType().Name}"),
    };

    BoundErrorExpression ErrorExpression(Location location, string message)
    {
        Error(location, message);
        return new BoundErrorExpression(location);
    }

    string NoInstanceReason => noThisContext switch
    {
        NoThisContext.FieldInitializer => "field initializers cannot use 'this', fields, or instance methods",
        NoThisContext.SuperArguments => "arguments to 'super(...)' cannot use 'this', fields, or instance methods",
        _ => "static methods cannot use 'this', fields, or instance methods",
    };

    BoundExpression BindIntegerLiteral(IntLiteralExpr e, Location location, bool negated)
    {
        // Negation is folded into the literal so that int.MinValue and long.MinValue can be written.
        ulong limit = (e.IsLong ? (ulong)long.MaxValue : int.MaxValue) + (negated ? 1UL : 0UL);
        if (e.Value > limit)
        {
            return ErrorExpression(location, e.IsLong
                ? "integer literal is too large for 'long'"
                : "integer literal is too large for 'int'; add an 'L' suffix to make it a 'long'");
        }

        if (e.IsLong)
            return new BoundLiteral(location, VplType.Long, negated ? unchecked((long)(0UL - e.Value)) : (long)e.Value);
        return new BoundLiteral(location, VplType.Int, negated ? unchecked((int)(0U - (uint)e.Value)) : (int)e.Value);
    }

    bool IsValueName(string name) =>
        scope?.Lookup(name) is not null || (currentType is not null && LookupField(currentType.Type, name) is not null);

    /// <summary>
    /// The type named by an expression like <c>Vec3</c> or <c>Pair&lt;int, float&gt;</c>, or null
    /// when the expression is not a type name. A local or field hides a type of the same name.
    /// </summary>
    VplType? AsTypeName(Expr expression)
    {
        if (expression is not NameExpr name || IsValueName(name.Name) || !types.TryGetValue(name.Name, out var s))
            return null;
        return BindTypeName(s.Name, name.TypeArguments, name.Location);
    }

    BoundExpression BindName(NameExpr e, VplType? expected)
    {
        if (e.TypeArguments.Count > 0)
            return ErrorExpression(e.Location, "type arguments can only be used in a call or construction");

        if (scope!.Lookup(e.Name) is { } local)
            return new BoundLocalAccess(e.Location, local);

        if (currentType is not null && LookupField(currentType.Type, e.Name) is var (field, owner))
        {
            if (!hasThis)
                return ErrorExpression(e.Location, $"'{e.Name}' is an instance field; {NoInstanceReason}");
            CheckAccessible(e.Location, field.Owner, field.IsPrivate, $"field '{field.Owner.Name}.{e.Name}'");
            return new BoundFieldAccess(e.Location, new BoundThis(e.Location, currentType.Type), field, TypeMap.For(owner).Apply(field.Type));
        }

        if (types.ContainsKey(e.Name) || LookupTypeParameter(e.Name) is not null)
            return ErrorExpression(e.Location, $"'{e.Name}' is a type, not a value");
        if (functions.ContainsKey(e.Name) || (currentType is not null && LookupMethods(currentType.Type, e.Name).Count > 0))
            return ErrorExpression(e.Location, $"'{e.Name}' is a function; call it with '{e.Name}(...)'");
        if (ExpectedVariant(e.Name, expected) is { } variant)
            return BindVariant(e.Location, variant.Owner, expected, variant, null);
        return ErrorExpression(e.Location, $"'{e.Name}' is not defined{VariantHint(e.Name)}");
    }

    BoundExpression BindThis(ThisExpr e)
    {
        if (!hasThis)
        {
            return ErrorExpression(e.Location, currentType is null
                ? "'this' can only be used inside a struct or class"
                : $"'this' cannot be used here; {NoInstanceReason}");
        }
        return new BoundThis(e.Location, currentType!.Type);
    }

    /// <summary>
    /// Members of a nullable value can only be reached through a local that flow analysis can prove
    /// non-null; other nullable values need <c>?.</c> or <c>!</c>.
    /// </summary>
    BoundExpression RequireNonNull(BoundExpression receiver)
    {
        if (!receiver.Type.IsNullable)
            return receiver;
        if (receiver is BoundLocalAccess { AssumesNonNull: false } local)
            return new BoundLocalAccess(local.Location, local.Local, AssumesNonNull: true);
        return ErrorExpression(receiver.Location, $"'{TextOf(receiver.Location)}' might be null; use '?.' or '!' to access its members");
    }

    BoundExpression BindMemberAccess(MemberAccessExpr e, VplType? expected)
    {
        if (e.TypeArguments.Count > 0)
            return ErrorExpression(e.NameLocation, "type arguments can only be used in a call");
        if (e.Target is SuperExpr)
            return ErrorExpression(e.NameLocation, $"'super' can only be used to call methods; use 'this.{e.Name}' for fields");
        if (AsEnumName(e.Target, expected) is var (enumSymbol, enumType) && enumSymbol.FindVariant(e.Name) is { } variant)
            return BindVariant(e.Location, enumSymbol, enumType, variant, null);

        if (AsTypeName(e.Target) is { } typeName)
        {
            if (typeName == VplType.Error)
                return new BoundErrorExpression(e.Location);
            return ErrorExpression(e.NameLocation, LookupMethods(typeName, e.Name).Count > 0
                ? $"'{typeName.Symbol!.Name}.{e.Name}' is a method; call it with '{e.Name}(...)'"
                : $"'{typeName.Symbol!.Name}' has no static member '{e.Name}'");
        }

        return BindFieldAccess(e.Location, BindExpression(e.Target), e.Name, e.NameLocation);
    }

    BoundExpression BindFieldAccess(Location location, BoundExpression target, string name, Location nameLocation)
    {
        if (target.Type == VplType.Error)
            return new BoundErrorExpression(location);
        target = RequireNonNull(target);
        if (target.Type == VplType.Error)
            return new BoundErrorExpression(location);

        if (!target.Type.IsStruct && !target.Type.IsClass)
        {
            return ErrorExpression(nameLocation, LookupMethods(target.Type, name).Count > 0
                ? $"'{DisplayName(target.Type)}.{name}' is a method; call it with '{name}(...)'"
                : target.Type.IsEnum && target.Type.Symbol!.ValueFields.Any(f => f.Name == name)
                ? $"a variant's fields can only be read with a pattern, as in 'if (x is Variant(var {name}))'"
                : $"'{target.Type}' has no member '{name}'");
        }
        if (LookupField(target.Type, name) is not var (field, owner))
        {
            return ErrorExpression(nameLocation, LookupMethods(target.Type, name).Count > 0
                ? $"'{DisplayName(target.Type)}.{name}' is a method; call it with '{name}(...)'"
                : $"'{DisplayName(target.Type)}' has no field '{name}'");
        }
        CheckAccessible(nameLocation, field.Owner, field.IsPrivate, $"field '{field.Owner.Name}.{name}'");
        return new BoundFieldAccess(location, target, field, TypeMap.For(owner).Apply(field.Type));
    }

    // Private members are visible only inside their declaring type. Protected and package access
    // need no checks yet: every program is a single file, which is a single package.
    void CheckAccessible(Location location, TypeSymbol owner, bool isPrivate, string description)
    {
        if (isPrivate && currentType != owner)
            Error(location, $"{description} is private");
    }

    BoundExpression BindUnary(UnaryExpr e)
    {
        if (e.Operator == TokenKind.Minus && e.Operand is IntLiteralExpr literal)
            return BindIntegerLiteral(literal, e.Location, negated: true);

        var operand = BindExpression(e.Operand);
        if (operand.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);

        var (op, valid) = e.Operator switch
        {
            TokenKind.Minus => (BoundUnaryOperator.Negate, operand.Type.IsNumeric),
            TokenKind.Bang => (BoundUnaryOperator.LogicalNot, operand.Type == VplType.Bool),
            TokenKind.Tilde => (BoundUnaryOperator.BitwiseNot, operand.Type.IsInteger),
            _ => throw new InvalidOperationException($"unexpected unary operator {e.Operator}"),
        };
        if (!valid)
            return ErrorExpression(e.Location, $"operator '{SyntaxFacts.GetText(e.Operator)}' cannot be applied to '{operand.Type}'");
        return new BoundUnary(e.Location, op, operand);
    }

    BoundExpression BindNonNull(NonNullExpr e)
    {
        var operand = BindExpression(e.Operand);
        if (operand.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);
        if (!operand.Type.IsNullable)
            return ErrorExpression(e.Location, $"'!' can only be applied to a nullable value, and '{operand.Type}' is not nullable");
        return new BoundNonNull(e.Location, operand);
    }

    BoundExpression BindCoalesce(CoalesceExpr e)
    {
        var left = BindExpression(e.Left);
        var right = BindExpression(e.Right);
        if (left.Type == VplType.Error || right.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);
        if (!left.Type.IsNullable)
            return ErrorExpression(e.Left.Location, $"the left side of '??' must be nullable, and '{left.Type}' is not");

        // Prefer a non-null result: `a ?? b` is non-null when `b` is.
        VplType[] candidates = [left.Type.NonNull, right.Type, left.Type];
        var type = candidates.FirstOrDefault(t =>
            t != VplType.Null && Conversions.IsImplicit(left.Type.NonNull, t) && (Conversions.IsImplicit(right.Type, t) || IsConvertibleConstant(right, t)));
        if (type is null)
            return ErrorExpression(e.OperatorLocation, $"operator '??' cannot be applied to '{left.Type}' and '{right.Type}'");
        return new BoundCoalesce(e.Location, left, Convert(right, type), type);
    }

    // ---- Patterns and switch ----

    BoundExpression BindIs(IsExpr e)
    {
        var operand = BindExpression(e.Operand);
        if (operand.Type == VplType.Error || operand.Type == VplType.Void)
        {
            DeclareErrorBindings(e.Pattern);
            return operand.Type == VplType.Void
                ? ErrorExpression(e.Operand.Location, "'is' cannot test a 'void' value")
                : new BoundErrorExpression(e.Location);
        }

        bool isTypeTest = e.Pattern is TypePatternSyntax || (e.Pattern is VariantPatternSyntax { Enum: null, Fields: null } && !operand.Type.IsEnum);
        if (e.Pattern is DiscardPatternSyntax or VarPatternSyntax)
            Error(e.Pattern.Location, "this pattern always matches, so 'is' would always be true");
        else if (isTypeTest && !operand.Type.IsReference)
        {
            DeclareErrorBindings(e.Pattern);
            return ErrorExpression(e.Operand.Location, $"'is' can only test the type of class or interface values, not '{operand.Type}'");
        }
        var pattern = BindPattern(e.Pattern, operand.Type, canBind: true);
        return new BoundIs(e.Location, operand, pattern);
    }

    /// <summary>Binds a pattern matching values of <paramref name="type"/> and declares its names in the current scope.</summary>
    BoundPattern BindPattern(PatternSyntax syntax, VplType type, bool canBind)
    {
        if (type == VplType.Error)
        {
            DeclareErrorBindings(syntax);
            return new BoundDiscardPattern(syntax.Location, type);
        }
        switch (syntax)
        {
            case DiscardPatternSyntax:
                return new BoundDiscardPattern(syntax.Location, type);
            case VarPatternSyntax v:
                return new BoundBindingPattern(v.Location, type, DeclareBinding(v.Name, type, v.NameLocation, canBind));
            case ConstantPatternSyntax c:
                return BindConstantPattern(c, type);
            case TypePatternSyntax t:
                return BindTypePattern(t.Location, t.Type, t.Name, t.NameLocation, type, canBind);
            // Only a variant if the value is an enum: otherwise a lone name is a type test, as in `animal is Dog`.
            case VariantPatternSyntax { Enum: null, Fields: null } v when !type.IsEnum:
                return BindTypePattern(v.Location, new TypeSyntax(v.NameLocation, v.Name, []), null, v.NameLocation, type, canBind);
            case VariantPatternSyntax v:
                return BindVariantPattern(v, type, canBind);
            default:
                throw new InvalidOperationException($"unexpected pattern {syntax.GetType().Name}");
        }
    }

    LocalSymbol DeclareBinding(string name, VplType type, Location location, bool canBind)
    {
        if (!canBind)
            Error(location, "a case with several patterns cannot bind names");
        var local = new LocalSymbol(name, type, isParameter: false);
        Declare(local, location);
        return local;
    }

    /// <summary>Declares a broken pattern's names, so that their uses don't report them as undefined.</summary>
    void DeclareErrorBindings(PatternSyntax syntax)
    {
        switch (syntax)
        {
            case VarPatternSyntax v:
                Declare(new LocalSymbol(v.Name, VplType.Error, isParameter: false), v.NameLocation);
                break;
            case TypePatternSyntax { Name: { } name } t:
                Declare(new LocalSymbol(name, VplType.Error, isParameter: false), t.NameLocation);
                break;
            case VariantPatternSyntax { Fields: { } fields }:
                foreach (var field in fields)
                    DeclareErrorBindings(field);
                break;
        }
    }

    BoundPattern ErrorPattern(PatternSyntax syntax, VplType type, string message)
    {
        Error(syntax.Location, message);
        DeclareErrorBindings(syntax);
        return new BoundDiscardPattern(syntax.Location, type);
    }

    BoundPattern BindConstantPattern(ConstantPatternSyntax c, VplType type)
    {
        var value = c.Value switch
        {
            IntLiteralExpr literal => BindIntegerLiteral(literal, c.Location, negated: false),
            UnaryExpr { Operand: IntLiteralExpr literal } => BindIntegerLiteral(literal, c.Location, negated: true),
            BoolLiteralExpr literal => new BoundLiteral(c.Location, VplType.Bool, literal.Value),
            NullLiteralExpr => ErrorExpression(c.Location, "'null' cannot be a pattern; compare with '== null', or use 'default'"),
            _ => ErrorExpression(c.Location, "only integer and 'bool' literals can be patterns"),
        };
        if (value is not BoundLiteral literalValue)
            return new BoundDiscardPattern(c.Location, type);
        if (literalValue.Type == type)
            return new BoundConstantPattern(c.Location, literalValue);
        if (type == VplType.Long && literalValue.Value is int small)
            return new BoundConstantPattern(c.Location, new BoundLiteral(c.Location, VplType.Long, (long)small));
        return ErrorPattern(c, type, $"a '{type}' can never be '{TextOf(c.Location)}'");
    }

    /// <summary>
    /// For a reference, a test of its runtime type. For a value type, which has no subtypes, the
    /// pattern only states the type, which must be exactly the value's.
    /// </summary>
    BoundPattern BindTypePattern(Location location, TypeSyntax typeSyntax, string? name, Location nameLocation, VplType type, bool canBind)
    {
        var target = BindType(typeSyntax, allowVoid: false);
        LocalSymbol? Binding(VplType bindingType) => name is null ? null : DeclareBinding(name, bindingType, nameLocation, canBind);
        if (target == VplType.Error)
        {
            Binding(VplType.Error);
            return new BoundDiscardPattern(location, type);
        }

        if (type.IsReference)
        {
            var from = type.NonNull;
            if (!target.IsReference || (from.IsClass && target.IsClass && !Conversions.IsSubtype(from, target) && !Conversions.IsSubtype(target, from)))
            {
                Binding(VplType.Error);
                Error(typeSyntax.Location, $"a '{from}' can never be a '{target}'");
                return new BoundDiscardPattern(location, type);
            }
            return new BoundTypePattern(location, type, target, Binding(target));
        }

        if (target != type)
        {
            Binding(VplType.Error);
            Error(typeSyntax.Location, $"a '{type}' can never be a '{target}'");
            return new BoundDiscardPattern(location, type);
        }
        return Binding(target) is { } local ? new BoundBindingPattern(location, type, local) : new BoundDiscardPattern(location, type);
    }

    BoundPattern BindVariantPattern(VariantPatternSyntax v, VplType type, bool canBind)
    {
        var written = v.Enum is null ? v.Name : $"{TextOf(v.Enum.Location)}.{v.Name}";
        if (!type.IsEnum)
            return ErrorPattern(v, type, $"a '{type}' can never be '{written}', since '{type}' is not an enum");

        var s = type.Symbol!;
        if (v.Enum is { } enumSyntax)
        {
            // `Result.Ok(...)` may leave out the type arguments, which the value determines.
            bool sameEnum = enumSyntax.TypeArguments.Count == 0 && LookupTypeParameter(enumSyntax.Name) is null
                ? types.GetValueOrDefault(enumSyntax.Name) == s
                : BindType(enumSyntax, allowVoid: false) is var named && (named == type || named == VplType.Error);
            if (!sameEnum)
                return ErrorPattern(v, type, $"a '{type}' can never be '{written}'");
        }
        if (s.FindVariant(v.Name) is not { } variant)
            return ErrorPattern(v, type, $"'{s.Name}' has no variant '{v.Name}'");

        var map = TypeMap.For(type);
        if (v.Fields is null)
            return new BoundVariantPattern(v.Location, type, variant, [.. variant.Fields.Select(f => new BoundDiscardPattern(v.Location, map.Apply(f.Type)))]);
        if (variant.Fields.Count == 0)
            return ErrorPattern(v, type, $"'{variant.Name}' carries no data; match it without parentheses");
        if (v.Fields.Count != variant.Fields.Count)
        {
            int count = variant.Fields.Count;
            return ErrorPattern(v, type, $"'{variant.Name}' has {count} field{(count == 1 ? "" : "s")}, but the pattern lists {v.Fields.Count}");
        }
        var fields = v.Fields.Zip(variant.Fields, (field, symbol) => BindPattern(field, map.Apply(symbol.Type), canBind)).ToList();
        return new BoundVariantPattern(v.Location, type, variant, fields);
    }

    /// <summary>Sets <paramref name="broken"/> if a pattern has an error, which would make coverage errors misleading.</summary>
    List<BoundPattern> BindCasePatterns(SwitchCase c, VplType type, ref bool broken)
    {
        int errors = diagnostics.Count;
        List<BoundPattern> patterns = [.. c.Patterns.Select(p => BindPattern(p, type, canBind: c.Patterns.Count == 1))];
        broken |= diagnostics.Count > errors;
        return patterns;
    }

    BoundExpression BindSwitchValue(Expr syntax)
    {
        var value = BindExpression(syntax);
        return value.Type == VplType.Void ? ErrorExpression(syntax.Location, "cannot switch on a 'void' value") : value;
    }

    BoundStatement BindSwitch(SwitchStmt s)
    {
        var value = BindSwitchValue(s.Value);
        var cases = new List<BoundSwitchCase<BoundStatement>>();
        bool broken = value.Type == VplType.Error;
        foreach (var c in s.Cases)
        {
            scope = new Scope(scope);
            var patterns = BindCasePatterns(c, value.Type, ref broken);
            cases.Add(new BoundSwitchCase<BoundStatement>(c.Location, patterns, BindStatement(c.Body!)));
            scope = scope.Parent;
        }
        CheckCases(s.Location, value.Type, s.Cases, [.. cases.Select(c => c.Patterns)], broken);
        return new BoundSwitch(s.Location, value, cases);
    }

    static bool IsNoReturn(BoundExpression expression) => expression is BoundCall { Method.Symbol.IsNoReturn: true };

    BoundExpression BindSwitchExpression(SwitchExpr e, VplType? expected)
    {
        var value = BindSwitchValue(e.Value);
        var bound = new List<(Location Location, List<BoundPattern> Patterns, BoundExpression Value)>();
        bool broken = value.Type == VplType.Error;
        foreach (var c in e.Cases)
        {
            scope = new Scope(scope);
            var patterns = BindCasePatterns(c, value.Type, ref broken);
            bound.Add((c.Location, patterns, BindExpression(c.Value!, expected)));
            scope = scope.Parent;
        }
        CheckCases(e.Location, value.Type, e.Cases, [.. bound.Select(b => b.Patterns)], broken);

        // A case that cannot complete, such as `default -> panic(...)`, needs no value.
        var producing = bound.Select(b => b.Value).Where(v => !IsNoReturn(v)).ToList();
        foreach (var caseValue in producing.Where(v => v.Type == VplType.Void))
            Error(caseValue.Location, "a switch expression's case must produce a value");
        if (value.Type == VplType.Error || producing.Any(v => v.Type == VplType.Error || v.Type == VplType.Void))
            return new BoundErrorExpression(e.Location);
        if (producing.Count == 0)
            return ErrorExpression(e.Location, "a switch expression needs a case that produces a value");

        var type = expected ?? CommonType(producing);
        if (type is null)
        {
            var found = string.Join(", ", producing.Select(v => $"'{v.Type}'").Distinct());
            return ErrorExpression(e.Location, $"the cases of the switch have incompatible types {found}");
        }
        var cases = bound.Select(b => new BoundSwitchCase<BoundExpression>(b.Location, b.Patterns, IsNoReturn(b.Value) ? b.Value : Convert(b.Value, type)));
        return new BoundSwitchExpression(e.Location, value, [.. cases], type);
    }

    /// <summary>The first case's type, or a later one, that every case converts to; references may become nullable for a <c>null</c> case.</summary>
    static VplType? CommonType(IReadOnlyList<BoundExpression> values)
    {
        var candidates = values
            .Select(v => v.Type)
            .Where(t => t != VplType.Null)
            .SelectMany(t => t.IsReference ? [t, t.Nullable] : new[] { t })
            .Distinct();
        return candidates.FirstOrDefault(t => values.All(v => CanConvert(v, t)));
    }

    /// <summary>Checks that <c>default</c> comes last, that every case can match something, and that the cases cover every value.</summary>
    void CheckCases(Location location, VplType type, IReadOnlyList<SwitchCase> syntax, IReadOnlyList<IReadOnlyList<BoundPattern>> patterns, bool broken)
    {
        for (int i = 0; i < syntax.Count - 1; i++)
        {
            if (syntax[i].IsDefault)
                Error(syntax[i].Location, "'default' must be the last case");
        }
        if (broken)
            return;

        var space = new MatchSpace(type);
        for (int i = 0; i < syntax.Count; i++)
        {
            if (syntax[i].IsDefault)
            {
                if (!space.Add(null))
                    Error(syntax[i].Location, "'default' can never be reached: the cases above match every value");
                continue;
            }
            foreach (var pattern in patterns[i])
            {
                if (!space.Add(pattern))
                    Error(pattern.Location, "this case can never be reached: the cases above match every value it matches");
            }
        }
        if (space.Missing() is { } missing)
        {
            Error(location, missing == "_"
                ? $"the switch does not handle every '{type}'; add a 'default' case"
                : $"the switch does not handle every '{type}': '{missing}' is not covered");
        }
    }

    // ---- Enum variants ----

    /// <summary>
    /// The enum named by the target of <c>Enum.Variant</c>. A generic enum written without type
    /// arguments takes them from <paramref name="expected"/> if that is the same enum, and otherwise
    /// has a null type, which is inferred from the variant's fields.
    /// </summary>
    (TypeSymbol Enum, VplType? Type)? AsEnumName(Expr target, VplType? expected)
    {
        if (target is not NameExpr name || IsValueName(name.Name) || !types.TryGetValue(name.Name, out var s) || s.Kind != TypeKind.Enum)
            return null;
        if (name.TypeArguments.Count == 0 && s.IsGeneric)
            return (s, expected?.Symbol == s ? expected : null);
        return (s, BindTypeName(s.Name, name.TypeArguments, name.Location));
    }

    /// <summary>A variant of the expected enum written without its enum, as in <c>return Ok(value);</c>.</summary>
    static VariantSymbol? ExpectedVariant(string name, VplType? expected) =>
        expected is { IsEnum: true } ? expected.Symbol!.FindVariant(name) : null;

    /// <summary>A hint for an undefined name that is the name of a variant.</summary>
    string VariantHint(string name)
    {
        if (types.Values.FirstOrDefault(t => t.Kind == TypeKind.Enum && t.FindVariant(name) is not null) is not { } owner)
            return "";
        return owner.IsGeneric
            ? $"; to create the '{owner.Name}' variant, write '{owner.Name}<...>.{name}', or use it where a '{owner.Name}' is expected"
            : $"; to create the '{owner.Name}' variant, write '{owner.Name}.{name}'";
    }

    /// <summary>
    /// Creates a variant. <paramref name="type"/> is null for a generic enum whose type arguments
    /// must be inferred; <paramref name="arguments"/> is null when no parentheses were written.
    /// </summary>
    BoundExpression BindVariant(Location location, TypeSymbol s, VplType? type, VariantSymbol variant, List<Argument>? arguments)
    {
        if (type == VplType.Error || arguments?.Any(a => a.Expression.Type == VplType.Error) == true)
            return new BoundErrorExpression(location);
        var name = $"{s.Name}.{variant.Name}";
        if (arguments is null && variant.Fields.Count > 0)
            return ErrorExpression(location, $"'{name}' carries data; create it with '{name}(...)'");
        if (arguments is not null && variant.Fields.Count == 0)
            return ErrorExpression(location, $"'{name}' carries no data; write it without parentheses");
        arguments ??= [];
        if (arguments.Count != variant.Fields.Count)
        {
            int count = variant.Fields.Count;
            return ErrorExpression(location, $"'{name}' has {count} field{(count == 1 ? "" : "s")}, but {arguments.Count} value{(arguments.Count == 1 ? " was" : "s were")} given");
        }

        MethodReference? constructor;
        if (type is not null)
        {
            constructor = Resolve(location, $"variant '{name}'", [new Candidate(variant.Constructor, type)], null, arguments);
        }
        else
        {
            var inferable = variant.Fields.SelectMany(f => TypeParametersIn(f.Type)).ToHashSet();
            if (s.TypeParameters.FirstOrDefault(p => !inferable.Contains(p)) is { } missing)
            {
                return ErrorExpression(location,
                    $"cannot infer type argument '{missing.Name}' of '{s.Name}' from '{variant.Name}'; use it where a '{s.Name}' is expected, or write '{s.Name}<...>.{variant.Name}'");
            }
            constructor = Resolve(location, $"variant '{name}'", [new Candidate(variant.InferringConstructor, null)], null, arguments);
            if (constructor is not null)
            {
                type = s.Construct(constructor.TypeArguments);
                QueueBoundChecks(s.TypeParameters, constructor.TypeArguments, location);
            }
        }
        if (constructor is null)
            return new BoundErrorExpression(location);
        return new BoundVariant(location, type!, variant, ConvertArguments(constructor, arguments));
    }

    static IEnumerable<TypeParameterSymbol> TypeParametersIn(VplType type) =>
        type.TypeParameter is { } parameter ? [parameter] : type.NonNull.TypeArguments.SelectMany(TypeParametersIn);

    // ---- Results ----

    bool IsResult(VplType? type) => type?.Symbol is { } s && s == resultSymbol;

    static bool IsTry(BoundExpression value) => value is BoundTry || (value is BoundConversion conversion && IsTry(conversion.Operand));

    /// <summary>The whole value of a statement, which unlike any other expression may be a <c>try</c>.</summary>
    BoundExpression BindValue(Expr expression, VplType? expected) =>
        expression is TryExpr t ? BindTry(t) : BindExpression(expression, expected == VplType.Error ? null : expected);

    BoundExpression BindTry(TryExpr e)
    {
        var operand = BindExpression(e.Operand);
        if (operand.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);
        if (!IsResult(operand.Type))
            return ErrorExpression(e.Operand.Location, $"'try' needs a 'Result', not '{operand.Type}'");
        var function = currentFunction;
        if (function is null || function.IsConstructor || function.IsDestructor || !IsResult(function.ReturnType))
            return ErrorExpression(e.Location, "'try' can only be used in a function that returns a 'Result'");
        var (error, expectedError) = (operand.Type.TypeArguments[1], function.ReturnType.TypeArguments[1]);
        if (error != expectedError)
        {
            return ErrorExpression(e.Location,
                $"'try' would return an error of type '{error}' from '{function.Name}', whose error type is '{expectedError}'; convert the error first");
        }
        return new BoundTry(e.Location, operand, operand.Type.TypeArguments[0]);
    }

    static BoundBinaryOperator BinaryOperatorFor(TokenKind kind) => kind switch
    {
        TokenKind.Plus => BoundBinaryOperator.Add,
        TokenKind.Minus => BoundBinaryOperator.Subtract,
        TokenKind.Star => BoundBinaryOperator.Multiply,
        TokenKind.Slash => BoundBinaryOperator.Divide,
        TokenKind.Percent => BoundBinaryOperator.Remainder,
        TokenKind.Ampersand => BoundBinaryOperator.BitwiseAnd,
        TokenKind.Pipe => BoundBinaryOperator.BitwiseOr,
        TokenKind.Caret => BoundBinaryOperator.BitwiseXor,
        TokenKind.LessLess => BoundBinaryOperator.ShiftLeft,
        TokenKind.GreaterGreater => BoundBinaryOperator.ShiftRight,
        TokenKind.AmpersandAmpersand => BoundBinaryOperator.LogicalAnd,
        TokenKind.PipePipe => BoundBinaryOperator.LogicalOr,
        TokenKind.EqualEqual => BoundBinaryOperator.Equal,
        TokenKind.BangEqual => BoundBinaryOperator.NotEqual,
        TokenKind.Less => BoundBinaryOperator.Less,
        TokenKind.LessEqual => BoundBinaryOperator.LessOrEqual,
        TokenKind.Greater => BoundBinaryOperator.Greater,
        TokenKind.GreaterEqual => BoundBinaryOperator.GreaterOrEqual,
        _ => throw new InvalidOperationException($"unexpected binary operator {kind}"),
    };

    /// <summary>An enum on the left of <c>==</c> or <c>!=</c> lets the right side name its variant alone, as in <c>this == Hearts</c>.</summary>
    BoundExpression BindBinary(BinaryExpr e)
    {
        var left = BindExpression(e.Left);
        var expected = e.Operator is TokenKind.EqualEqual or TokenKind.BangEqual && left.Type.IsEnum ? left.Type : null;
        return BindBinaryOperation(e.OperatorLocation, left, e.Operator, BindExpression(e.Right, expected));
    }

    static bool IsReferenceOrNull(VplType type) => type.IsReference || type == VplType.Null;

    BoundExpression BindBinaryOperation(Location location, BoundExpression left, TokenKind syntaxOperator, BoundExpression right)
    {
        if (left.Type == VplType.Error || right.Type == VplType.Error)
            return new BoundErrorExpression(location);

        var op = BinaryOperatorFor(syntaxOperator);
        var symbol = SyntaxFacts.GetText(syntaxOperator);
        string Mismatch() => $"operator '{symbol}' cannot be applied to '{left.Type}' and '{right.Type}'";

        if (IsReferenceOrNull(left.Type) || IsReferenceOrNull(right.Type))
        {
            if (op is not (BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual) || !IsReferenceOrNull(left.Type) || !IsReferenceOrNull(right.Type))
                return ErrorExpression(location, Mismatch());
            // References compare by identity, and only when one could be the other.
            var (a, b) = (left.Type.NonNull, right.Type.NonNull);
            bool related = a == VplType.Null || b == VplType.Null || a.IsInterface || b.IsInterface
                || Conversions.IsSubtype(a, b) || Conversions.IsSubtype(b, a);
            if (!related)
                return ErrorExpression(location, Mismatch());
            return new BoundBinary(location, left, op, right, VplType.Bool);
        }

        if (op is BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual && (left.Type.IsStructOrEnum || right.Type.IsStructOrEnum))
        {
            if (left.Type != right.Type)
                return ErrorExpression(location, Mismatch());
            if (Conversions.FindNonEquatableField(left.Type) is { } field)
            {
                return ErrorExpression(location,
                    $"operator '{symbol}' cannot be applied to '{left.Type}' because its field '{field.Name}' of type '{TypeMap.For(left.Type).Apply(field.Type)}' does not support '=='");
            }
            return new BoundBinary(location, left, op, right, VplType.Bool);
        }

        bool bothBool = left.Type == VplType.Bool && right.Type == VplType.Bool;
        bool bothNumeric = left.Type.IsNumeric && right.Type.IsNumeric;
        bool bothInteger = left.Type.IsInteger && right.Type.IsInteger;

        VplType? operandType = null;
        bool producesBool = false;
        switch (op)
        {
            case BoundBinaryOperator.ShiftLeft or BoundBinaryOperator.ShiftRight:
                if (bothInteger)
                    return new BoundBinary(location, left, op, right, left.Type);
                break;
            case BoundBinaryOperator.LogicalAnd or BoundBinaryOperator.LogicalOr:
                operandType = bothBool ? VplType.Bool : null;
                break;
            case BoundBinaryOperator.BitwiseAnd or BoundBinaryOperator.BitwiseOr or BoundBinaryOperator.BitwiseXor:
                operandType = bothBool ? VplType.Bool : bothInteger ? CommonNumericType(left, right) : null;
                break;
            case BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual:
                operandType = bothBool ? VplType.Bool : bothNumeric ? CommonNumericType(left, right) : null;
                producesBool = true;
                break;
            case BoundBinaryOperator.Less or BoundBinaryOperator.LessOrEqual
                or BoundBinaryOperator.Greater or BoundBinaryOperator.GreaterOrEqual:
                operandType = bothNumeric ? CommonNumericType(left, right) : null;
                producesBool = true;
                break;
            default:
                operandType = bothNumeric ? CommonNumericType(left, right) : null;
                break;
        }

        if (operandType is null)
        {
            var hint = bothNumeric ? "; use an explicit cast to convert one side" : "";
            return ErrorExpression(location, Mismatch() + hint);
        }
        var resultType = producesBool ? VplType.Bool : operandType;
        return new BoundBinary(location, Convert(left, operandType), op, Convert(right, operandType), resultType);
    }

    VplType? CommonNumericType(BoundExpression left, BoundExpression right)
    {
        if (left.Type == right.Type)
            return left.Type;
        if (CanConvert(left, right.Type))
            return right.Type;
        if (CanConvert(right, left.Type))
            return left.Type;
        return null;
    }

    static bool CanConvert(BoundExpression expression, VplType to) =>
        Conversions.IsImplicit(expression.Type, to) || IsConvertibleConstant(expression, to) || IsNarrowable(expression, to);

    /// <summary>Integer literals convert implicitly to float or double when no precision is lost.</summary>
    static bool IsConvertibleConstant(BoundExpression expression, VplType to) =>
        to.IsFloatingPoint
        && expression is BoundLiteral { Value: int or long } literal
        && Conversions.IsExactlyRepresentable(System.Convert.ToInt64(literal.Value), to);

    /// <summary>A nullable local converts to a non-null type if flow analysis proves it is not null there.</summary>
    static bool IsNarrowable(BoundExpression expression, VplType to) =>
        expression is BoundLocalAccess { AssumesNonNull: false, Type.IsNullable: true }
        && !to.IsNullable
        && Conversions.IsImplicit(expression.Type.NonNull, to);

    BoundExpression Convert(BoundExpression expression, VplType to)
    {
        if (expression.Type == to || expression.Type == VplType.Error || to == VplType.Error)
            return expression;

        if (IsConvertibleConstant(expression, to))
        {
            long value = System.Convert.ToInt64(((BoundLiteral)expression).Value);
            return new BoundLiteral(expression.Location, to, to == VplType.Float ? (object)(float)value : (object)(double)value);
        }

        if (IsNarrowable(expression, to))
        {
            expression = new BoundLocalAccess(expression.Location, ((BoundLocalAccess)expression).Local, AssumesNonNull: true);
            if (expression.Type == to)
                return expression;
        }

        if (Conversions.IsImplicit(expression.Type, to))
            return new BoundConversion(expression.Location, to, expression);

        var hint = Conversions.IsExplicit(expression.Type, to) ? "; use an explicit cast"
            : expression.Type.IsNullable && Conversions.IsImplicit(expression.Type.NonNull, to) ? "; check it for null first, or use '!'"
            : "";
        return ErrorExpression(expression.Location, $"cannot implicitly convert '{expression.Type}' to '{to}'{hint}");
    }

    // ---- Calls and construction ----

    List<Argument> BindArguments(IEnumerable<Expr> arguments) =>
    [
        .. arguments.Select(a => a is RefArgExpr r
            ? new Argument(BindExpression(r.Operand), true, r.Location)
            : new Argument(BindExpression(a), false, a.Location)),
    ];

    BoundExpression BindCall(CallExpr e, VplType? expected)
    {
        if (e.Callee is SuperExpr)
        {
            BindArguments(e.Arguments);
            return ErrorExpression(e.Callee.Location, currentFunction is { IsConstructor: true, ContainingType.Kind: TypeKind.Class }
                ? "'super(...)' must be the first statement of a constructor"
                : "'super(...)' can only be used in a class constructor");
        }

        var arguments = BindArguments(e.Arguments);
        return e.Callee switch
        {
            NameExpr name => BindNameCall(e, name, arguments, expected),
            MemberAccessExpr member => BindMemberCall(e, member, arguments, expected),
            ConditionalAccessExpr access => BindConditionalCall(e, access, arguments),
            ThisExpr => ErrorExpression(e.Callee.Location, "'this(...)' constructor chaining is not supported yet"),
            _ => ErrorExpression(e.Callee.Location, "this expression cannot be called"),
        };
    }

    BoundExpression BindNameCall(CallExpr e, NameExpr name, List<Argument> arguments, VplType? expected)
    {
        if (IsValueName(name.Name))
            return ErrorExpression(name.Location, $"'{name.Name}' is a variable, not a function");
        if (LookupTypeParameter(name.Name) is not null)
            return ErrorExpression(name.Location, $"'{name.Name}' is a type parameter and cannot be constructed");

        var typeArguments = BindTypeArguments(name.TypeArguments);
        if (arguments.Any(a => a.Expression.Type == VplType.Error) || typeArguments?.Contains(VplType.Error) == true)
            return new BoundErrorExpression(e.Location);

        if (currentType is { } s && LookupMethods(s.Type, name.Name) is { Count: > 0 } methods)
        {
            var method = Resolve(e.Location, $"overload of '{name.Name}'", methods, typeArguments, arguments);
            if (method is null)
                return new BoundErrorExpression(e.Location);
            CheckAccessible(name.Location, method.Symbol.ContainingType!, method.Symbol.IsPrivate, $"method '{method.Symbol.ContainingType!.Name}.{name.Name}'");
            if (method.Symbol.IsStatic)
                return MakeCall(e.Location, method, null, arguments);
            if (!hasThis)
                return ErrorExpression(name.Location, $"'{name.Name}' is an instance method; {NoInstanceReason}");
            return MakeCall(e.Location, method, new BoundThis(name.Location, s.Type), arguments);
        }

        if (types.TryGetValue(name.Name, out var type))
        {
            if (type.Kind != TypeKind.Struct)
            {
                return ErrorExpression(name.Location, type.Kind switch
                {
                    TypeKind.Class => $"classes are created with 'new', as in 'new {name.Name}(...)'",
                    TypeKind.Enum => $"enums are created from their variants, as in '{name.Name}.{type.Variants.FirstOrDefault()?.Name}'",
                    _ => $"cannot create an instance of interface '{name.Name}'",
                });
            }
            var constructed = Construct(type, typeArguments ?? [], name.Location);
            return constructed == VplType.Error
                ? new BoundErrorExpression(e.Location)
                : BindConstruction(e.Location, constructed, arguments, []);
        }

        if (!functions.TryGetValue(name.Name, out var overloads))
        {
            if (typeArguments is null && ExpectedVariant(name.Name, expected) is { } variant)
                return BindVariant(e.Location, variant.Owner, expected, variant, arguments);
            return ErrorExpression(name.Location, $"function '{name.Name}' is not defined{VariantHint(name.Name)}");
        }
        var function = Resolve(e.Location, $"overload of '{name.Name}'", [.. overloads.Select(f => new Candidate(f, null))], typeArguments, arguments);
        return function is null ? new BoundErrorExpression(e.Location) : MakeCall(e.Location, function, null, arguments);
    }

    BoundExpression BindMemberCall(CallExpr e, MemberAccessExpr member, List<Argument> arguments, VplType? expected)
    {
        var typeArguments = BindTypeArguments(member.TypeArguments);
        if (typeArguments?.Contains(VplType.Error) == true)
            return new BoundErrorExpression(e.Location);

        if (member.Target is SuperExpr super)
            return BindSuperMethodCall(e, super, member, typeArguments, arguments);

        if (AsEnumName(member.Target, expected) is var (enumSymbol, enumType) && enumSymbol.FindVariant(member.Name) is { } variant)
        {
            if (typeArguments is not null)
                return ErrorExpression(member.NameLocation, $"type arguments belong to the enum, as in '{enumSymbol.Name}<...>.{member.Name}(...)'");
            return BindVariant(e.Location, enumSymbol, enumType, variant, arguments);
        }

        if (AsTypeName(member.Target) is { } type)
        {
            if (type == VplType.Error || arguments.Any(a => a.Expression.Type == VplType.Error))
                return new BoundErrorExpression(e.Location);
            var s = type.Symbol!;
            var staticCandidates = LookupMethods(type, member.Name);
            if (staticCandidates.Count == 0)
                return ErrorExpression(member.NameLocation, $"'{s.Name}' has no method '{member.Name}'");
            var staticMethod = Resolve(e.Location, $"overload of '{s.Name}.{member.Name}'", staticCandidates, typeArguments, arguments);
            if (staticMethod is null)
                return new BoundErrorExpression(e.Location);
            if (!staticMethod.Symbol.IsStatic)
            {
                return ErrorExpression(member.NameLocation,
                    $"'{s.Name}.{member.Name}' is an instance method; call it on a value of type '{type}'");
            }
            var declaring = staticMethod.Symbol.ContainingType!;
            CheckAccessible(member.NameLocation, declaring, staticMethod.Symbol.IsPrivate, $"method '{declaring.Name}.{member.Name}'");
            return MakeCall(e.Location, staticMethod, null, arguments);
        }

        var receiver = BindExpression(member.Target);
        if (receiver.Type == VplType.Error || arguments.Any(a => a.Expression.Type == VplType.Error))
            return new BoundErrorExpression(e.Location);
        receiver = RequireNonNull(receiver);
        if (receiver.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);
        return BindMethodCall(e.Location, receiver, member.Name, member.NameLocation, typeArguments, arguments);
    }

    BoundExpression BindMethodCall(
        Location location,
        BoundExpression receiver,
        string name,
        Location nameLocation,
        IReadOnlyList<VplType>? typeArguments,
        List<Argument> arguments)
    {
        var receiverType = receiver.Type;
        var candidates = receiverType.IsStructOrEnum || receiverType.IsReference || receiverType.IsTypeParameter
            ? LookupMethods(receiverType, name)
            : [];
        if (candidates.Count == 0)
        {
            return ErrorExpression(nameLocation, LookupField(receiverType, name) is not null
                ? $"'{DisplayName(receiverType)}.{name}' is a field, not a method"
                : $"'{DisplayName(receiverType)}' has no method '{name}'");
        }

        var method = Resolve(location, $"overload of '{DisplayName(receiverType)}.{name}'", candidates, typeArguments, arguments);
        if (method is null)
            return new BoundErrorExpression(location);
        var declaring = method.Symbol.ContainingType!;
        if (method.Symbol.IsStatic)
        {
            return ErrorExpression(nameLocation,
                $"static method '{name}' must be called through the type: '{declaring.Name}.{name}(...)'");
        }
        CheckAccessible(nameLocation, declaring, method.Symbol.IsPrivate, $"method '{declaring.Name}.{name}'");
        return MakeCall(location, method, receiver, arguments);
    }

    BoundExpression BindSuperMethodCall(CallExpr e, SuperExpr super, MemberAccessExpr member, IReadOnlyList<VplType>? typeArguments, List<Argument> arguments)
    {
        if (!hasThis || currentType?.Type.BaseType is not { } baseType)
        {
            return ErrorExpression(super.Location, hasThis
                ? $"'{currentType!.Name}' has no base class"
                : "'super' can only be used in instance methods and constructors of a class");
        }
        if (arguments.Any(a => a.Expression.Type == VplType.Error))
            return new BoundErrorExpression(e.Location);
        var candidates = LookupMethods(baseType, member.Name);
        if (candidates.Count == 0)
            return ErrorExpression(member.NameLocation, $"'{baseType.Symbol!.Name}' has no method '{member.Name}'");
        var method = Resolve(e.Location, $"overload of '{baseType.Symbol!.Name}.{member.Name}'", candidates, typeArguments, arguments);
        if (method is null)
            return new BoundErrorExpression(e.Location);
        var declaring = method.Symbol.ContainingType!;
        if (method.Symbol.IsStatic)
            return ErrorExpression(member.NameLocation, $"static method '{member.Name}' must be called through the type: '{declaring.Name}.{member.Name}(...)'");
        if (method.Symbol.IsAbstract)
            return ErrorExpression(member.NameLocation, $"cannot call abstract method '{declaring.Name}.{member.Name}' through 'super'");
        CheckAccessible(member.NameLocation, declaring, method.Symbol.IsPrivate, $"method '{declaring.Name}.{member.Name}'");
        return MakeCall(e.Location, method, new BoundThis(super.Location, currentType.Type), arguments, isSuperCall: true);
    }

    /// <summary>Binds the receiver of <c>a?.member</c> and a temporary local holding its non-null value.</summary>
    (BoundExpression Receiver, LocalSymbol Value)? BindConditionalReceiver(ConditionalAccessExpr e)
    {
        var receiver = BindExpression(e.Target);
        if (receiver.Type == VplType.Error)
            return null;
        if (!receiver.Type.IsNullable)
        {
            Error(e.Target.Location, $"'?.' is only needed on nullable values, and '{receiver.Type}' is not nullable; use '.'");
            return null;
        }
        return (receiver, new LocalSymbol("value", receiver.Type.NonNull, isParameter: false) { IsTemporary = true });
    }

    BoundExpression BindConditionalFieldAccess(ConditionalAccessExpr e)
    {
        if (BindConditionalReceiver(e) is not var (receiver, value))
            return new BoundErrorExpression(e.Location);
        var access = BindFieldAccess(e.Location, new BoundLocalAccess(e.Target.Location, value), e.Name, e.NameLocation);
        return MakeConditionalAccess(e.Location, receiver, value, access);
    }

    BoundExpression BindConditionalCall(CallExpr e, ConditionalAccessExpr access, List<Argument> arguments)
    {
        if (BindConditionalReceiver(access) is not var (receiver, value) || arguments.Any(a => a.Expression.Type == VplType.Error))
            return new BoundErrorExpression(e.Location);
        var call = BindMethodCall(e.Location, new BoundLocalAccess(access.Target.Location, value), access.Name, access.NameLocation, null, arguments);
        return MakeConditionalAccess(e.Location, receiver, value, call);
    }

    BoundExpression MakeConditionalAccess(Location location, BoundExpression receiver, LocalSymbol value, BoundExpression access)
    {
        if (access.Type == VplType.Error)
            return access;
        if (access.Type == VplType.Void || access.Type.IsReference)
            return new BoundConditionalAccess(location, receiver, value, access, access.Type == VplType.Void ? access.Type : access.Type.Nullable);
        return ErrorExpression(location, $"'?.' cannot produce a nullable '{access.Type}': only class and interface types can be nullable for now");
    }

    BoundExpression MakeCall(Location location, MethodReference method, BoundExpression? receiver, List<Argument> arguments, bool isSuperCall = false)
    {
        // Calling a method on an object never modifies the place holding the reference.
        if (receiver is not null && !receiver.Type.IsReference && !method.Symbol.IsReadOnly)
        {
            var (kind, reason) = ClassifyPlace(receiver);
            if (kind == PlaceKind.ReadOnly)
            {
                var what = receiver is BoundThis ? "'this'" : $"'{TextOf(receiver.Location)}'";
                return ErrorExpression(location,
                    $"cannot call '{method.Symbol.Name}' on {what} because {reason}; only 'readonly' methods can be called on it");
            }
        }
        QueueBoundChecks(method.Symbol.TypeParameters, method.TypeArguments, location);
        return new BoundCall(location, method, receiver, ConvertArguments(method, arguments), isSuperCall);
    }

    List<BoundExpression> ConvertArguments(MethodReference method, List<Argument> arguments)
    {
        var result = new List<BoundExpression>();
        for (int i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (method.ParameterRefKind(i) != RefKind.Ref)
            {
                result.Add(Convert(argument.Expression, method.ParameterType(i)));
                continue;
            }

            var (kind, reason) = ClassifyPlace(argument.Expression);
            if (kind == PlaceKind.Temporary)
                Error(argument.Location, "a 'ref' argument must be a variable or field");
            else if (kind == PlaceKind.ReadOnly)
                Error(argument.Location, $"cannot pass '{TextOf(argument.Expression.Location)}' by 'ref' because {reason}");
            else if (argument.Expression is BoundFieldAccess { Field.IsWeak: true })
                Error(argument.Location, "a 'weak' field cannot be passed by 'ref'");
            result.Add(argument.Expression);
        }
        return result;
    }

    /// <summary>
    /// Picks the overload to call. Type arguments are inferred from the arguments unless given explicitly.
    /// Among equally good candidates a non-generic one wins.
    /// </summary>
    MethodReference? Resolve(
        Location location,
        string description,
        IReadOnlyList<Candidate> candidates,
        IReadOnlyList<VplType>? typeArguments,
        List<Argument> arguments)
    {
        var sameArity = candidates
            .Where(c => c.Symbol.Parameters.Count == arguments.Count && (typeArguments is null || c.Symbol.TypeParameters.Count == typeArguments.Count))
            .ToList();
        if (sameArity.Count == 0)
        {
            if (typeArguments is not null && candidates.Any(c => c.Symbol.Parameters.Count == arguments.Count))
                Error(location, $"no {description} takes {typeArguments.Count} type argument{(typeArguments.Count == 1 ? "" : "s")}");
            else
                Error(location, $"no {description} takes {arguments.Count} argument{(arguments.Count == 1 ? "" : "s")}");
            return null;
        }

        var instantiated = new List<MethodReference>();
        string? inferenceError = null;
        foreach (var candidate in sameArity)
        {
            var inferred = candidate.Symbol.IsGeneric
                ? typeArguments ?? InferTypeArguments(candidate.Symbol, candidate.Owner, arguments, out inferenceError)
                : [];
            if (inferred is not null)
                instantiated.Add(new MethodReference(candidate.Symbol, candidate.Owner, inferred));
        }
        if (instantiated.Count == 0)
        {
            Error(location, inferenceError!);
            return null;
        }

        if (sameArity.Count == 1 && ExplainRefMismatch(instantiated[0], arguments) is { } mismatch)
        {
            Error(mismatch.Location, mismatch.Message);
            return null;
        }

        var applicable = instantiated.Where(m => IsApplicable(m, arguments)).ToList();
        if (applicable.Count == 0)
        {
            var types = string.Join(", ", arguments.Select(a => (a.IsRef ? "ref " : "") + a.Expression.Type));
            Error(location, $"no {description} accepts ({types})");
            return null;
        }

        var exact = applicable
            .Where(m => Enumerable.Range(0, arguments.Count).All(i => m.ParameterType(i) == arguments[i].Expression.Type))
            .ToList();
        if (exact.Count > 0)
            return exact.FirstOrDefault(m => !m.Symbol.IsGeneric) ?? exact[0];

        // Java's rule: pick the overload whose parameters all convert to every other candidate's parameters.
        var mostSpecific = applicable.Where(m => applicable.All(n => n == m || IsMoreSpecific(m, n))).ToList();
        if (mostSpecific.Count > 1 && mostSpecific.Count(m => !m.Symbol.IsGeneric) == 1)
            mostSpecific = [.. mostSpecific.Where(m => !m.Symbol.IsGeneric)];
        if (mostSpecific.Count == 1)
            return mostSpecific[0];

        Error(location, $"call is ambiguous between {string.Join(" and ", applicable.Select(m => $"'{m}'"))}");
        return null;
    }

    /// <summary>
    /// Infers each type parameter from the arguments in its positions. When positions disagree, the
    /// type that every argument converts to wins, so <c>max(1, 2L)</c> infers <c>long</c>.
    /// </summary>
    IReadOnlyList<VplType>? InferTypeArguments(FunctionSymbol function, VplType? owner, List<Argument> arguments, out string? error)
    {
        var ownerMap = owner is null ? TypeMap.Empty : TypeMap.For(owner);
        var bindings = function.TypeParameters.ToDictionary(p => p, _ => new List<(BoundExpression? Expression, VplType Type)>());

        void Unify(VplType parameter, BoundExpression? argument, VplType argumentType)
        {
            if (parameter.TypeParameter is { } p && bindings.TryGetValue(p, out var found))
            {
                found.Add((argument, argumentType));
            }
            else if (parameter.Symbol is not null && parameter.Symbol == argumentType.Symbol)
            {
                // Inside a type argument there are no conversions, so these must match exactly.
                for (int i = 0; i < parameter.TypeArguments.Count; i++)
                    Unify(parameter.TypeArguments[i], null, argumentType.TypeArguments[i]);
            }
        }

        for (int i = 0; i < arguments.Count; i++)
            Unify(ownerMap.Apply(function.Parameters[i].Type), arguments[i].Expression, arguments[i].Expression.Type);

        var result = new List<VplType>();
        foreach (var parameter in function.TypeParameters)
        {
            var found = bindings[parameter];
            if (found.Count == 0)
            {
                error = $"cannot infer type argument '{parameter.Name}' of '{function.Name}'; specify it explicitly, as in '{function.Name}<...>(...)'";
                return null;
            }
            var chosen = found.Select(b => b.Type).Distinct().FirstOrDefault(candidate =>
                candidate != VplType.Null && found.All(b => b.Expression is not null ? CanConvert(b.Expression, candidate) : b.Type == candidate));
            if (chosen is null)
            {
                error = $"cannot infer type argument '{parameter.Name}' of '{function.Name}': it could be {string.Join(" or ", found.Select(b => $"'{b.Type}'").Distinct())}";
                return null;
            }
            result.Add(chosen);
        }
        error = null;
        return result;
    }

    static bool IsApplicable(MethodReference method, List<Argument> arguments) =>
        Enumerable.Range(0, arguments.Count).All(i => method.ParameterRefKind(i) == RefKind.Ref
            ? arguments[i].IsRef && arguments[i].Expression.Type == method.ParameterType(i)
            : !arguments[i].IsRef && CanConvert(arguments[i].Expression, method.ParameterType(i)));

    static (Location Location, string Message)? ExplainRefMismatch(MethodReference method, List<Argument> arguments)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            bool isRefParameter = method.ParameterRefKind(i) == RefKind.Ref;
            if (isRefParameter && !argument.IsRef)
                return (argument.Location, $"argument {i + 1} must be passed with 'ref'");
            if (!isRefParameter && argument.IsRef)
                return (argument.Location, $"argument {i + 1} must not be passed with 'ref'");
            if (isRefParameter && argument.Expression.Type != method.ParameterType(i))
                return (argument.Location, $"a 'ref' argument must have exactly type '{method.ParameterType(i)}', not '{argument.Expression.Type}'");
        }
        return null;
    }

    static bool IsMoreSpecific(MethodReference m, MethodReference n) =>
        Enumerable.Range(0, m.Symbol.Parameters.Count).All(i => Conversions.IsImplicit(m.ParameterType(i), n.ParameterType(i)));

    BoundExpression BindNew(NewExpr e)
    {
        var arguments = BindArguments(e.Arguments);
        var type = BindType(e.Type, allowVoid: false);
        if (type == VplType.Error || arguments.Any(a => a.Expression.Type == VplType.Error))
            return new BoundErrorExpression(e.Location);
        if (type.IsStruct)
            return ErrorExpression(e.Type.Location, $"structs are created without 'new', as in '{e.Type.Name}(...)'");
        if (type.IsEnum)
            return ErrorExpression(e.Type.Location, $"enums are created from their variants without 'new', as in '{e.Type.Name}.{type.Symbol!.Variants.FirstOrDefault()?.Name}'");
        if (type.IsInterface)
            return ErrorExpression(e.Type.Location, $"cannot create an instance of interface '{type}'");
        if (!type.IsClass)
            return ErrorExpression(e.Type.Location, $"'{type}' cannot be created with 'new'");
        if (type.Symbol!.IsAbstract)
            return ErrorExpression(e.Type.Location, $"cannot create an instance of abstract class '{type}'");

        var constructor = Resolve(e.Location, $"constructor of '{type}'", ConstructorsOf(type), null, arguments);
        if (constructor is null)
            return new BoundErrorExpression(e.Location);
        CheckAccessible(e.Location, type.Symbol, constructor.Symbol.IsPrivate, $"constructor '{constructor}'");
        return new BoundNew(e.Location, type, constructor, ConvertArguments(constructor, arguments));
    }

    BoundExpression BindInitializer(InitializerExpr e)
    {
        var (typeName, arguments) = e.Target switch
        {
            NameExpr n => (n, new List<Argument>()),
            CallExpr { Callee: NameExpr n } call => (n, BindArguments(call.Arguments)),
            _ => throw new InvalidOperationException("the parser only produces initializers on names and calls"),
        };
        if (AsTypeName(typeName) is not { } type || (type != VplType.Error && !type.IsStruct))
            return ErrorExpression(typeName.Location, $"'{typeName.Name}' is not a struct; '{{ ... }}' initializers can only follow a struct name");
        if (type == VplType.Error)
            return new BoundErrorExpression(e.Location);
        return BindConstruction(e.Location, type, arguments, e.Fields);
    }

    BoundExpression BindConstruction(Location location, VplType type, List<Argument> arguments, IReadOnlyList<FieldInit> fieldInits)
    {
        if (arguments.Any(a => a.Expression.Type == VplType.Error))
            return new BoundErrorExpression(location);

        var s = type.Symbol!;
        var constructor = Resolve(location, $"constructor of '{type}'", ConstructorsOf(type), null, arguments);
        if (constructor is null)
            return new BoundErrorExpression(location);
        CheckAccessible(location, s, constructor.Symbol.IsPrivate, $"constructor '{constructor}'");

        var map = TypeMap.For(type);
        var initializers = new List<BoundFieldInitializer>();
        foreach (var init in fieldInits)
        {
            var initField = s.FindField(init.Name);
            var value = BindExpression(init.Value, initField is null ? null : map.Apply(initField.Type));
            if (initField is not { } field)
            {
                Error(init.Location, $"'{s.Name}' has no field '{init.Name}'");
                continue;
            }
            if (initializers.Any(i => i.Field == field))
            {
                Error(init.Location, $"field '{init.Name}' is initialized more than once");
                continue;
            }
            CheckAccessible(init.Location, s, field.IsPrivate, $"field '{s.Name}.{init.Name}'");
            initializers.Add(new BoundFieldInitializer(field, Convert(value, map.Apply(field.Type))));
        }

        if (constructor.Symbol.IsImplicit)
        {
            var missing = s.Fields.FirstOrDefault(f =>
                f.Declaration.Initializer is null
                && initializers.All(i => i.Field != f)
                && !Conversions.IsDefaultable(map.Apply(f.Type)));
            if (missing is not null)
            {
                return ErrorExpression(location,
                    $"'{type}' has no default value because field '{missing.Name}' has type '{map.Apply(missing.Type)}'; initialize it explicitly, as in '{s.Name}{{ {missing.Name} = ... }}'");
            }
        }

        return new BoundConstruction(location, type, constructor, ConvertArguments(constructor, arguments), initializers);
    }

    BoundExpression BindCast(CastExpr e)
    {
        var operand = BindExpression(e.Operand);
        var type = BindType(e.Type, allowVoid: false);
        if (operand.Type == VplType.Error || type == VplType.Error)
            return new BoundErrorExpression(e.Location);
        if (operand.Type == type)
            return operand;
        if (!Conversions.IsExplicit(operand.Type, type))
            return ErrorExpression(e.Location, $"cannot convert '{operand.Type}' to '{type}'");
        return new BoundConversion(e.Location, type, operand);
    }

    BoundExpression BindConditional(ConditionalExpr e, VplType? expected)
    {
        var condition = BindCondition(e.Condition);
        var whenTrue = BindExpression(e.WhenTrue, expected);
        var whenFalse = BindExpression(e.WhenFalse, expected);
        if (whenTrue.Type == VplType.Error || whenFalse.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);

        var (t, f) = (whenTrue.Type, whenFalse.Type);
        VplType? type = t == f ? t
            : Conversions.IsImplicit(t, f) || IsConvertibleConstant(whenTrue, f) ? f
            : Conversions.IsImplicit(f, t) || IsConvertibleConstant(whenFalse, t) ? t
            : t == VplType.Null && f.IsReference ? f.Nullable
            : f == VplType.Null && t.IsReference ? t.Nullable
            : null;
        if (type is null || type == VplType.Void || type == VplType.Null)
            return ErrorExpression(e.Location, $"the branches of '?:' have incompatible types '{t}' and '{f}'");
        return new BoundConditional(e.Location, condition, Convert(whenTrue, type), Convert(whenFalse, type), type);
    }
}
