using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    List<TypeParameterSymbol> DeclareTypeParameters(IReadOnlyList<TypeParameterSyntax> syntax, IReadOnlyList<TypeParameterSymbol> outer)
    {
        var result = new List<TypeParameterSymbol>();
        foreach (var parameter in syntax)
        {
            if (result.Any(p => p.Name == parameter.Name))
                Error(Catalog.DuplicateTypeParameter(parameter.Location, parameter.Name));
            else if (outer.Any(p => p.Name == parameter.Name))
                Error(Catalog.HidingTypeParameter(parameter.Location, parameter.Name));
            else
                TakesLanguageName(parameter.Name, parameter.Location);
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
                if (bound == WylType.Error)
                    continue;
                if (!bound.IsInterface || bound.IsNullable)
                    Error(Catalog.NonInterfaceBound(boundSyntax.Location, bound.ToString()));
                else
                    symbols[i].Bounds.Add(bound);
            }
        }
    }

    TypeSymbol? DeclareType(TypeDecl decl)
    {
        // Only declares the built-in String's members, so its name keeps naming the built-in type.
        if (decl is { Name: "String", Kind: TypeKind.Struct } && (decl.Modifiers & Modifiers.Extern) != 0 && decl.Location.Source.Module.Package.IsStd)
            return new TypeSymbol(decl, []);
        if (TakesLanguageName(decl.Name, decl.NameLocation))
            return null;
        if (ClashingFile(decl, decl.Name) is { } other)
        {
            Error(Catalog.DuplicateName(decl.NameLocation, decl.Name, null, other.Name, null));
            return null;
        }
        if (OwnType(decl.Name, decl.Location.Source) is not null || BuiltinType(decl.Name) is not null)
        {
            Error(Catalog.DuplicateName(decl.NameLocation, decl.Name, OwnType(decl.Name, decl.Location.Source)?.Declaration.NameLocation, null, null));
            return null;
        }
        if ((decl.Modifiers & Modifiers.Extern) != 0 && !decl.Location.Source.Module.Package.IsStd)
            Error(Catalog.ExternStructOutsideStd(decl.NameLocation));
        HasCallerLocation(decl);
        var symbol = new TypeSymbol(decl, DeclareTypeParameters(decl.TypeParameters, []));
        TableOf(decl).Types[decl.Name] = symbol;
        return symbol;
    }

    void BindSupertypes(TypeSymbol s)
    {
        var decl = s.Declaration;
        if (decl.BaseType is { } baseSyntax && BindType(baseSyntax, allowVoid: false) is var baseType && baseType != WylType.Error)
        {
            if (!baseType.IsClass)
                Error(baseType.IsInterface ? Catalog.ExtendInterface(baseSyntax.Location, baseType.ToString()) : Catalog.ExtendNonClass(baseSyntax.Location, baseType.ToString()));
            else if (baseType.IsNullable)
                Error(Catalog.NullableBase(baseSyntax.Location));
            else if (baseType.Symbol!.IsFinal)
                Error(Catalog.ExtendFinal(baseSyntax.Location, baseType.ToString()));
            else
                s.BaseType = baseType;
        }

        foreach (var interfaceSyntax in decl.Interfaces)
        {
            var type = BindType(interfaceSyntax, allowVoid: false);
            if (type == WylType.Error)
                continue;
            if (!type.IsInterface || type.IsNullable)
                Error(Catalog.NotAnInterface(interfaceSyntax.Location, type.ToString()));
            else if (s.Interfaces.Any(i => i.Symbol == type.Symbol))
                Error(Catalog.InterfaceListedTwice(interfaceSyntax.Location, type.Symbol!.Name));
            else if (type.Symbol == stdlib.Borrowable && s.Kind != TypeKind.Class && s.Kind != TypeKind.Interface)
                Error(Catalog.BorrowableNonClass(interfaceSyntax.Location));
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
                    Error(Catalog.InheritanceCycle(s.Declaration.NameLocation, s.Name));
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
            Reach = Reach.Of(decl),
            IsUnsafe = (decl.Modifiers & Modifiers.Unsafe) != 0,
            PassesLocation = HasCallerLocation(decl),
            BuiltinCppName = ExternName(decl, null),
            IsPure = IsPure(decl),
        };
        typeParameters = [];
        CheckReturnsView(function);

        if (TakesLanguageName(decl.Name, decl.NameLocation))
            return function;
        if (ClashingFile(decl, decl.Name) is { } other)
        {
            Error(Catalog.DuplicateName(decl.NameLocation, decl.Name, null, other.Name, null));
            return function;
        }
        if (OwnType(decl.Name, decl.Location.Source) is { } type)
        {
            Error(Catalog.DuplicateName(decl.NameLocation, decl.Name, type.Declaration.NameLocation, null, SyntaxFacts.KindNameWithArticle(type.Kind)));
            return function;
        }

        // Within what any one file sees, each signature is declared once: the overloads this file
        // sees, and if this one is visible beyond its file, the private ones of the module's others.
        var existing = FindSameSignature(OwnFunctions(decl.Name, decl.Location.Source), function)
            ?? (decl.Visibility == Visibility.File ? null : FindSameSignature(PrivateElsewhere(decl.Name, decl.Location.Source), function));
        if (existing is null)
            AddFunction(function, decl);
        else
        {
            Error(Catalog.DuplicateFunction(decl.NameLocation, "function", function.Signature, existing.Declaration?.NameLocation, DiffersOnlyInEscaping(function, existing) ? existing.Signature : null));
        }
        return function;
    }

    /// <summary>
    /// Identifies a method signature as seen from a type whose type arguments <paramref name="map"/>
    /// applies. A function's own type parameters compare by position. Nullability is ignored, since
    /// <c>T</c> and <c>T?</c> are the same type at runtime and can't be told apart by overloading,
    /// and so is a reference's, since <c>ref x</c> could be passed to either.
    /// </summary>
    static string MethodKey(FunctionSymbol function, TypeMap map)
    {
        string Key(WylType type) =>
            type is NullableReferenceType reference ? Key(reference.Referenced)
            : type.IsNullable ? Key(type.NonNull)
            : type is TypeParameterType { Parameter: var p } && function.TypeParameters.Contains(p) ? $"#{function.TypeParameters.ToList().IndexOf(p)}"
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
                Error(Catalog.DuplicateParameter(parameter.Location, parameter.Name));
            else
                TakesLanguageName(parameter.Name, parameter.Location);
            var type = BindVariableType(parameter.Type, parameter.Escaping, parameter.Annotations, isParameter: true);
            CheckParameterMode(parameter.RefKind, type, parameter.Location);
            if (parameter.IsMutable && type.NonNull.IsNonEscapingFunction)
                Error(Catalog.NonEscapingMut(parameter.Location));
            // A 'ref?' parameter can default to null.
            bool mustRefer = parameter.RefKind == RefKind.Ref && !type.IsNullableReference;
            if (parameter.Default is not null && mustRefer)
                Error(Catalog.RefParameterDefault(parameter.Default.Location, parameter.Name));
            var local = new LocalSymbol(parameter.Name, type, isParameter: true, parameter.RefKind)
            {
                IsMutable = parameter.IsMutable,
                DefaultSyntax = mustRefer ? null : parameter.Default,
            };
            if (local.DefaultSyntax is not null)
                local.DefaultDeclaration = local;
            result.Add(local);
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
            var type = BindType(decl.Type, allowVoid: false);
            CheckStorable(type, decl.Type.Location, "the type of a field");
            var field = new FieldSymbol(decl.Name, type, s, decl);
            if (s.IsExtern)
                Error(Catalog.ExternStructField(decl.NameLocation));
            if (s.FindField(decl.Name) is not null)
                Error(Catalog.DuplicateField(decl.NameLocation, decl.Name, s.FindField(decl.Name)!.Declaration.NameLocation));
            else
            {
                TakesLanguageName(decl.Name, decl.NameLocation);
                s.Fields.Add(field);
            }
        }

        foreach (var decl in s.Declaration.Methods)
        {
            // A constructor has its type's name, which the type's declaration checked.
            if (!decl.IsConstructor)
                TakesLanguageName(decl.Name, decl.NameLocation);
            var ownTypeParameters = DeclareTypeParameters(decl.TypeParameters, s.TypeParameters);
            typeParameters = [.. s.TypeParameters, .. ownTypeParameters];
            BindBounds(decl.TypeParameters, ownTypeParameters);
            var parameters = BindParameters(decl.Parameters);
            var modifiers = decl.Modifiers;
            bool isInterface = s.Kind == TypeKind.Interface;
            // An interface method is as visible as its interface (spec programs.visibility).
            var reach = isInterface ? s.Reach : Reach.Of(decl).Within(s.Reach);
            if (decl.IsConstructor)
            {
                if (s.IsExtern)
                {
                    Error(Catalog.ExternStructConstructor(decl.NameLocation));
                    continue;
                }
                if (parameters.Count == 0 && s.Kind == TypeKind.Struct)
                {
                    Error(Catalog.ParameterlessStructConstructor(decl.NameLocation));
                    continue;
                }
                var constructor = new FunctionSymbol(s.Name, parameters, WylType.Void)
                {
                    Declaration = decl,
                    Body = decl.Body,
                    ContainingType = s,
                    IsConstructor = true,
                    Reach = reach,
                    IsUnsafe = (modifiers & Modifiers.Unsafe) != 0,
                    PassesLocation = HasCallerLocation(decl),
                };
                if (FindSameSignature(s.Constructors, constructor) is { } existingConstructor)
                {
                    Error(Catalog.DuplicateFunction(decl.NameLocation, "constructor", constructor.Signature, existingConstructor.Declaration?.NameLocation, DiffersOnlyInEscaping(constructor, existingConstructor) ? existingConstructor.Signature : null));
                    continue;
                }
                s.Constructors.Add(constructor);
                s.Members.Add(constructor);
                continue;
            }

            var method = new FunctionSymbol(decl.Name, parameters, BindType(decl.ReturnType!, allowVoid: true))
            {
                Declaration = decl,
                Body = decl.Body,
                TypeParameters = ownTypeParameters,
                ContainingType = s,
                IsStatic = (modifiers & Modifiers.Static) != 0,
                // A class method can't change the reference it is called through; an enum value can't be modified at all.
                IsReadOnly = s.Kind != TypeKind.Class && (modifiers & (Modifiers.Mut | Modifiers.Static)) == 0,
                Reach = reach,
                IsVirtual = (modifiers & Modifiers.Virtual) != 0,
                IsOverride = (modifiers & Modifiers.Override) != 0,
                IsAbstract = isInterface || (modifiers & Modifiers.Abstract) != 0,
                IsFinal = (modifiers & Modifiers.Final) != 0,
                ReturnRefKind = decl.ReturnRefKind,
                IsUnsafe = (modifiers & Modifiers.Unsafe) != 0,
                PassesLocation = HasCallerLocation(decl),
                BuiltinCppName = ExternName(decl, s),
                IsPure = IsPure(decl),
            };
            if (method.ReturnRefKind != RefKind.None)
                CheckReturnsReference(method);
            CheckReturnsView(method);
            if (method.PassesLocation && method.IsDispatched)
                Error(Catalog.CallerLocationOnDispatched(decl.NameLocation));
            if (method.IsBuiltin && !method.IsStatic && !s.IsExtern)
                Error(Catalog.InstanceExternMethod(decl.NameLocation));
            else if (s.IsExtern && !method.IsBuiltin)
                Error(Catalog.NonExternMethodInExternStruct(decl.NameLocation));
            // C++ templates cannot be virtual.
            if (method.IsGeneric && isInterface)
                Error(Catalog.GenericInterfaceMethod(decl.NameLocation));
            else if (method.IsGeneric && (method.IsVirtual || method.IsOverride || method.IsAbstract))
                Error(Catalog.GenericVirtualMethod(decl.NameLocation));
            if ((modifiers & Modifiers.Abstract) != 0 && !s.IsAbstract)
                Error(Catalog.AbstractMethodInConcreteClass(decl.NameLocation, s.Name, decl.Name));
            if (s.FindField(decl.Name) is not null || s.FindVariant(decl.Name) is not null)
            {
                Error(Catalog.MemberNameTaken(decl.NameLocation, decl.Name, s.Kind == TypeKind.Enum ? "variant" : "field", s.FindField(decl.Name)?.Declaration.NameLocation ?? s.FindVariant(decl.Name)!.Declaration.NameLocation));
                continue;
            }
            if (!s.Methods.TryGetValue(decl.Name, out var overloads))
                s.Methods[decl.Name] = overloads = [];
            if (FindSameSignature(overloads, method) is { } existingMethod)
            {
                Error(Catalog.DuplicateFunction(decl.NameLocation, "method", method.Signature, existingMethod.Declaration?.NameLocation, DiffersOnlyInEscaping(method, existingMethod) ? existingMethod.Signature : null));
                continue;
            }
            overloads.Add(method);
            s.Members.Add(method);
        }

        if (s.Declaration.Destructor is { } destructor)
        {
            s.Destructor = new FunctionSymbol("~" + s.Name, [], WylType.Void)
            {
                Body = destructor.Body,
                ContainingType = s,
                IsDestructor = true,
            };
            s.Members.Add(s.Destructor);
        }
        typeParameters = [];
    }

    /// <summary>
    /// The constructor a struct without declared constructors gets, taking every field in order.
    /// Its body is written as source, <c>this.f = f;</c> for each field, so it is checked like any constructor.
    /// </summary>
    static void DeclareMemberwiseConstructor(TypeSymbol s)
    {
        var location = s.Declaration.NameLocation;
        var body = new BlockStmt(location, [.. s.Fields.Select(f => new AssignStmt(location,
            new MemberAccessExpr(location, new ThisExpr(location), f.Name, location, []),
            TokenKind.Assign, location, new NameExpr(location, f.Name, [])))]);
        var constructor = new FunctionSymbol(s.Name, [.. s.Fields.Select(f => new LocalSymbol(f.Name, f.Type, isParameter: true) { Field = f })], WylType.Void)
        {
            IsMemberwise = true,
            Body = body,
            ContainingType = s,
            IsConstructor = true,
            // It sets every field, so it reaches only where all of them do (spec structs.constructors.memberwise).
            Reach = s.Fields.Select(f => f.Reach).Aggregate(s.Reach, (least, field) => field.Within(least)),
        };
        s.Constructors.Add(constructor);
        s.Members.Add(constructor);
    }

    /// <summary>Reports annotations that don't apply to the declaration, and returns whether it has <c>@CallerLocation</c>.</summary>
    bool HasCallerLocation(Declaration decl)
    {
        bool found = false;
        foreach (var annotation in decl.Annotations)
        {
            if (annotation.Name == "CallerLocation" && decl is FunctionDecl)
            {
                found = true;
                CheckNoArguments(annotation);
            }
            else if (annotation.Name == "Acyclic" && decl is TypeDecl { Kind: TypeKind.Class })
            {
                CheckNoArguments(annotation);
            }
            else if (annotation.Name == "Pure" && decl is FunctionDecl function && IsPure(function))
            {
                CheckNoArguments(annotation);
            }
            // '@Repr' is checked with the enum's variants.
            else if (annotation.Name != "Repr" || decl is not TypeDecl { Kind: TypeKind.Enum })
            {
                ReportMisplacedAnnotation(annotation);
            }
        }
        return found;
    }

    /// <summary>An extern function marked <c>@Pure</c>, which the standard library uses for what can't panic or change anything.</summary>
    static bool IsPure(FunctionDecl decl) =>
        decl.Annotations.Any(a => a.Name == "Pure") && (decl.Modifiers & Modifiers.Extern) != 0 && decl.Location.Source.Module.Package.IsStd;

    /// <summary>
    /// The C++ function an extern function calls: <c>wyl::name</c>, or for a method of a standard
    /// library type, <c>wyl::type::name</c> with the type's name in lower camel case.
    /// </summary>
    string? ExternName(FunctionDecl decl, TypeSymbol? owner)
    {
        if ((decl.Modifiers & Modifiers.Extern) == 0)
            return null;
        if (!decl.Location.Source.Module.Package.IsStd)
            Error(Catalog.ExternFunctionOutsideStd(decl.NameLocation));
        if (owner is { IsExtern: true } && (decl.Modifiers & Modifiers.Static) == 0)
            return decl.Name;
        if (owner is not null)
            return $"wyl::{char.ToLowerInvariant(owner.Name[0])}{owner.Name[1..]}::{decl.Name}";
        // A function of std::memory is wyl::memory::allocate, as the runtime declares it.
        return $"wyl::{string.Concat(decl.Location.Source.Module.Path.Select(name => name + "::"))}{decl.Name}";
    }

    /// <summary>A returned span views the receiver, so only instance methods may return one.</summary>
    void CheckReturnsView(FunctionSymbol function)
    {
        if (function.ReturnType.IsRefStruct && !function.IsInstanceMethod)
            Error(Catalog.ViewReturnFromNonInstance(function.Declaration!.NameLocation, function.ReturnType.ToString()));
    }

    /// <summary>A returned reference borrows the receiver, so only instance methods that the caller calls directly may return one.</summary>
    void CheckReturnsReference(FunctionSymbol method)
    {
        var location = method.Declaration!.NameLocation;
        var keyword = FunctionSymbol.RefPrefix(method.ReturnRefKind).Trim() + (method.ReturnType.IsNullableReference ? "?" : "");
        var referenced = method.ReturnType.Referred;
        if (method.IsStatic)
            Error(Catalog.StaticReferenceReturn(location, keyword));
        else if (method.IsDispatched)
            Error(Catalog.DispatchedReferenceReturn(location, keyword));
        else if (referenced == WylType.Void)
            Error(Catalog.VoidReferenceReturn(location, keyword));
        else if (referenced.PassedWithoutCopying)
            Error(Catalog.ReferenceToPassedWithoutCopying(location, keyword, (referenced.NonNull.IsFunction ? referenced.NonNull : referenced).ToString(), referenced.NonNull.IsFunction));
    }

    void DeclareVariants(TypeSymbol s)
    {
        foreach (var decl in s.Declaration.Variants)
        {
            var fields = new List<FieldSymbol>();
            foreach (var parameter in decl.Fields ?? [])
            {
                if (fields.Any(f => f.Name == parameter.Name))
                    Error(Catalog.DuplicateField(parameter.Location, parameter.Name, fields.First(f => f.Name == parameter.Name).Declaration.NameLocation));
                else
                    TakesLanguageName(parameter.Name, parameter.Location);
                // A variant's fields are as visible as the enum.
                var fieldDecl = new FieldDecl(parameter.Location, Modifiers.None, parameter.Type, parameter.Name, parameter.Location, null)
                {
                    Visibility = s.Declaration.Visibility,
                };
                foreach (var annotation in parameter.Annotations)
                    ReportMisplacedAnnotation(annotation);
                var fieldType = BindType(parameter.Type, allowVoid: false);
                CheckStorable(fieldType, parameter.Type.Location, "the type of a variant's field");
                fields.Add(new FieldSymbol(parameter.Name, fieldType, s, fieldDecl));
            }
            if (s.FindVariant(decl.Name) is not null)
            {
                Error(Catalog.DuplicateVariant(decl.NameLocation, decl.Name));
                continue;
            }
            TakesLanguageName(decl.Name, decl.NameLocation);
            s.Variants.Add(new VariantSymbol(decl.Name, s, s.Variants.Count, decl, fields));
        }

        foreach (var repr in s.Declaration.Annotations.Where(a => a.Name == "Repr"))
        {
            if (repr.Arguments is not [TypeSyntax underlyingSyntax])
            {
                Error(Catalog.ReprArguments(repr.Location));
                continue;
            }
            var underlying = BindType(underlyingSyntax, allowVoid: false);
            if (s.UnderlyingType is not null)
                Error(Catalog.ReprTwice(repr.Location));
            else if (s.HasData)
                Error(Catalog.ReprWithData(repr.Location));
            else if (underlying != WylType.Error && !underlying.IsInteger)
                Error(Catalog.ReprNonInteger(underlyingSyntax.Location, underlying.ToString()));
            else
                s.UnderlyingType = underlying;
        }

        Int128 next = 0;
        var used = new Dictionary<Int128, VariantSymbol>();
        foreach (var variant in s.Variants)
        {
            if (variant.Declaration.Value is { } valueSyntax)
            {
                if (s.HasData)
                {
                    Error(Catalog.VariantValueWithData(valueSyntax.Location));
                    continue;
                }
                s.UnderlyingType ??= WylType.Int;
                if (VariantValue(valueSyntax, s.UnderlyingType) is not { } value)
                    continue;
                next = value;
            }
            variant.Value = next;
            if (used.TryGetValue(next, out var other))
                Error(Catalog.DuplicateVariantValue(variant.Declaration.NameLocation, variant.Name, other.Name, other.Declaration.NameLocation));
            used.TryAdd(next, variant);
            next++;
        }
        if (s.UnderlyingType is { } type && s.Variants.FirstOrDefault(v => !type.HeldRange!.Value.Contains(v.Value)) is { } tooLarge)
            Error(Catalog.VariantValueTooLarge(tooLarge.Declaration.NameLocation, tooLarge.Name, type.ToString()));
    }

    Int128? VariantValue(Expr syntax, WylType underlying)
    {
        var (literal, negated) = syntax switch
        {
            IntLiteralExpr l => (l, false),
            UnaryExpr { Operator: TokenKind.Minus, Operand: IntLiteralExpr l } => (l, true),
            _ => ((IntLiteralExpr?)null, false),
        };
        if (literal is null)
        {
            Error(Catalog.VariantValueNotLiteral(syntax.Location));
            return null;
        }
        Int128 value = negated ? -(Int128)literal.Value : literal.Value;
        if (!underlying.HeldRange!.Value.Contains(value))
        {
            Error(Catalog.VariantLiteralTooLarge(syntax.Location, underlying.ToString()));
            return null;
        }
        return value;
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
    }
}
