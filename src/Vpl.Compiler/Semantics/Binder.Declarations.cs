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
        if ((decl.Modifiers & Modifiers.Extern) != 0 && !Stdlib.Contains(decl.Location.Source))
            Error(decl.NameLocation, "extern structs are only supported in the standard library");
        HasCallerLocation(decl);
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
            else if (type.Symbol == stdlib.Borrowable && s.Kind != TypeKind.Class && s.Kind != TypeKind.Interface)
                Error(interfaceSyntax.Location, "only classes can be 'Borrowable'; a value is borrowed through whatever holds it");
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
            IsUnsafe = (decl.Modifiers & Modifiers.Unsafe) != 0,
            PassesLocation = HasCallerLocation(decl),
            BuiltinCppName = ExternName(decl, null),
        };
        typeParameters = [];
        CheckReturnsView(function);

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
            HasCallerLocation(decl);
            var field = new FieldSymbol(decl.Name, BindType(decl.Type, allowVoid: false), s, decl);
            if (s.IsExtern)
                Error(decl.NameLocation, "an extern struct cannot declare fields; its C++ class holds its data");
            CheckStorable(field.Type, decl.Type.Location, "the type of a field");            if (s.FindField(decl.Name) is not null)
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
                if (s.IsExtern)
                {
                    Error(decl.NameLocation, "an extern struct cannot declare constructors");
                    continue;
                }
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
                    IsUnsafe = (modifiers & Modifiers.Unsafe) != 0,
                    PassesLocation = HasCallerLocation(decl),
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
                ReturnRefKind = decl.ReturnRefKind,
                IsProperty = decl.IsProperty,
                IsUnsafe = (modifiers & Modifiers.Unsafe) != 0,
                PassesLocation = HasCallerLocation(decl),
                BuiltinCppName = ExternName(decl, s),
            };
            if (method.ReturnRefKind != RefKind.None)
                CheckReturnsReference(method);
            CheckReturnsView(method);
            if (method.PassesLocation && method.IsDispatched)
                Error(decl.NameLocation, "'@CallerLocation' cannot be used on virtual, abstract, or interface methods yet");
            if (method.IsBuiltin && !method.IsStatic && !s.IsExtern)
                Error(decl.NameLocation, "extern methods must be static, except in an extern struct");
            else if (s.IsExtern && !method.IsBuiltin)
                Error(decl.NameLocation, "the methods of an extern struct must be extern");
            if (method.IsProperty && method.IsStatic)
                Error(decl.NameLocation, "static properties are not supported yet");
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

    /// <summary>Reports annotations that don't apply to the declaration, and returns whether it has <c>@CallerLocation</c>.</summary>
    bool HasCallerLocation(Declaration decl)
    {
        bool found = false;
        foreach (var annotation in decl.Annotations)
        {
            if (annotation.Name == "CallerLocation" && decl is FunctionDecl)
                found = true;
            else if (annotation.Name == "CallerLocation")
                Error(annotation.Location, "'@CallerLocation' can only be used on functions, methods, and constructors");
            else if (annotation.Name == "Acyclic" && decl is not TypeDecl { Kind: TypeKind.Class })
                Error(annotation.Location, "'@Acyclic' can only be used on classes");
            else if (annotation.Name == "Acyclic")
                continue;
            else
                Error(annotation.Location, $"unknown annotation '@{annotation.Name}'");
        }
        return found;
    }

    /// <summary>
    /// The C++ function an extern function calls: <c>vpl::name</c>, or for a method of a standard
    /// library type, <c>vpl::type::name</c> with the type's name in lower camel case.
    /// </summary>
    string? ExternName(FunctionDecl decl, TypeSymbol? owner)
    {
        if ((decl.Modifiers & Modifiers.Extern) == 0)
            return null;
        if (!Stdlib.Contains(decl.Location.Source))
            Error(decl.NameLocation, "extern functions are only supported in the standard library for now");
        if (owner is { IsExtern: true } && (decl.Modifiers & Modifiers.Static) == 0)
            return decl.Name;
        return owner is null ? $"vpl::{decl.Name}" : $"vpl::{char.ToLowerInvariant(owner.Name[0])}{owner.Name[1..]}::{decl.Name}";
    }

    /// <summary>A returned span views the receiver, so only instance methods may return one.</summary>
    void CheckReturnsView(FunctionSymbol function)
    {
        if (function.ReturnType.IsRefStruct && !function.IsInstanceMethod)
            Error(function.Declaration!.NameLocation, $"only instance methods can return '{function.ReturnType}', which views the method's receiver");
    }

    /// <summary>A returned reference borrows the receiver, so only instance methods that the caller calls directly may return one.</summary>
    void CheckReturnsReference(FunctionSymbol method)
    {
        var location = method.Declaration!.NameLocation;
        var keyword = FunctionSymbol.RefPrefix(method.ReturnRefKind).Trim();
        if (method.IsStatic)
            Error(location, $"a static method cannot return '{keyword}'; a returned reference must point into the method's receiver");
        else if (method.IsDispatched)
            Error(location, $"methods returning '{keyword}' cannot be virtual or abstract yet");
        else if (method.ReturnType == VplType.Void)
            Error(location, $"'void' cannot be returned by '{keyword}'");
        else if (method.ReturnType.IsReference)
            Error(location, $"'{keyword}' cannot be used with type '{method.ReturnType}'; class objects are already references");
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
                var fieldType = BindType(parameter.Type, allowVoid: false);
                CheckStorable(fieldType, parameter.Type.Location, "the type of a variant's field");
                fields.Add(new FieldSymbol(parameter.Name, fieldType, s, fieldDecl));
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
                // A nullable value holds its value inline too.
                while (fieldType.IsFixedArray || fieldType.IsNullableValue)
                    fieldType = fieldType.IsFixedArray ? fieldType.ElementType! : fieldType.NonNull;
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
