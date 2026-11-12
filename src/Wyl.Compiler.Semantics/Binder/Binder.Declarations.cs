using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    TypeSymbol? DeclareType(TypeDecl decl)
    {
        // Only declares a built-in type's members and interfaces, so its name keeps naming the built-in type.
        if (decl is { Kind: TypeKind.Struct, Block.IsStd: true } && StdlibSymbols.IsHostedTypeName(decl.Name))
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
        HasCallerLocation(decl);
        var symbol = new TypeSymbol(decl, DeclareTypeParameters(decl.TypeParameters, []));
        if (decl.Block is { } block)
            symbol.CppName = BlockMemberCppName(decl, decl.Name, block);
        CheckExternType(symbol);
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
            else
            {
                if (s.Kind == TypeKind.Interface && type.Symbol!.IsUnsafe && !s.IsUnsafe)
                    Error(Catalog.SafeInterfaceExtendsUnsafe(interfaceSyntax.Location, s.Name, type.ToString()));
                else if (s.Kind != TypeKind.Interface && type.Symbol!.IsUnsafe && interfaceSyntax.Unsafe is null)
                    Error(Catalog.UnsafeInterfaceWithoutUnsafe(interfaceSyntax.Location, type.ToString()));
                if (!type.Symbol!.IsUnsafe && interfaceSyntax.Unsafe is { } unsafeAt)
                    Error(Catalog.UnsafeOnSafeInterface(unsafeAt, type.ToString()));
                s.Interfaces.Add(type);
                if (s.Kind is not (TypeKind.Class or TypeKind.Interface))
                    valueTypeInterfaces.Add((type, interfaceSyntax.Location));
            }
        }
    }

    /// <summary>The interfaces structs and enums list, checked once every interface's own list is bound.</summary>
    readonly List<(WylType Interface, Location At)> valueTypeInterfaces = [];

    /// <summary>
    /// A value is borrowed through whatever holds it, so a struct or enum can't be <c>Borrowable</c>,
    /// whether it lists the interface or one that extends it.
    /// </summary>
    void CheckBorrowableValueTypes()
    {
        foreach (var (type, at) in valueTypeInterfaces)
        {
            if (stdlib.Borrowable is { } borrowable && Conversions.IsSubtype(type, borrowable.Type, function: null))
                Error(Catalog.BorrowableNonClass(at));
            // Only an object's Visit calls visitElements.
            if (stdlib.ElementOwner is { } owner && Conversions.Supertypes(type, function: null).Prepend(type).FirstOrDefault(t => t.Symbol == owner) is { } owned)
                Error(Catalog.ElementOwnerNonClass(at, owned.ToString()));
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

    void PropagateSharedClasses(IReadOnlyList<TypeSymbol> all)
    {
        foreach (var s in all.Where(s => s.Kind == TypeKind.Class && (s.Declaration.Modifiers & Modifiers.Shared) != 0))
            s.IsShared = true;
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var s in all.Where(s => s.Kind == TypeKind.Class))
            {
                bool shared = s.BaseType?.Symbol is { IsShared: true } || s.IsShared;
                if (!shared)
                    continue;
                if (!s.IsShared)
                {
                    s.IsShared = true;
                    changed = true;
                }
                if (s.BaseType?.Symbol is { Kind: TypeKind.Class, IsShared: false } baseClass)
                {
                    baseClass.IsShared = true;
                    changed = true;
                }
            }
        }
    }

    FunctionSymbol DeclareFunction(FunctionDecl decl)
    {
        var ownTypeParameters = DeclareTypeParameters(decl.TypeParameters, []);
        typeParameters = ownTypeParameters;
        BindBounds(decl.TypeParameters, ownTypeParameters);
        if (decl.Where.Count > 0)
            Error(Catalog.WhereNotAllowed(decl.Where[0].Location, "bound a free function's type parameter", "put the bound where the type parameter is declared"));
        var parameters = BindParameters(decl.Parameters, out var receiver);
        // An extension's receiver is its first parameter (spec functions.extensions).
        if (receiver is not null)
            parameters.Insert(0, receiver);
        var function = new FunctionSymbol(decl.Name, parameters, BindType(decl.ReturnType!, allowVoid: true))
        {
            IsExtension = receiver is not null,
            Declaration = decl,
            Body = decl.Body,
            TypeParameters = ownTypeParameters,
            Reach = Reach.Of(decl),
            IsUnsafe = (decl.Modifiers & Modifiers.Unsafe) != 0 || IsUnsafeExtern(decl, null),
            PassesLocation = HasCallerLocation(decl),
            BuiltinCppName = ExternFunctionName(decl, null),
            IsPure = IsPure(decl, null),
            IsCCallback = IsCCallback(decl, null),
        };
        typeParameters = [];
        CheckExternFunction(function, null);
        CheckReturnsView(function);
        AddTopLevelFunction(function, decl);
        return function;
    }

    /// <summary>Adds a top-level function to its module's or its file's names, unless its name or signature is taken.</summary>
    void AddTopLevelFunction(FunctionSymbol function, FunctionDecl decl)
    {
        if (TakesLanguageName(decl.Name, decl.NameLocation))
            return;
        if (ClashingFile(decl, decl.Name) is { } other)
        {
            Error(Catalog.DuplicateName(decl.NameLocation, decl.Name, null, other.Name, null));
            return;
        }
        if (OwnType(decl.Name, decl.Location.Source) is { } type)
        {
            Error(Catalog.DuplicateName(decl.NameLocation, decl.Name, type.Declaration.NameLocation, null, SyntaxFacts.KindNameWithArticle(type.Kind)));
            return;
        }
        // A constant's name is a value, so it shares its name with no function (spec interop.extern.constants).
        if (OwnFunctions(decl.Name, decl.Location.Source).FirstOrDefault(f => f.IsExternConstant != function.IsExternConstant || f.IsExternConstant) is { } named)
        {
            Error(Catalog.DuplicateName(decl.NameLocation, decl.Name, named.Declaration?.NameLocation, null, named.IsExternConstant ? "a constant" : "a function"));
            return;
        }

        // Within what any one file sees, each signature is declared once: the overloads this file
        // sees, and if this one is visible beyond its file, the private ones of the module's others.
        var existing = FindSameFunction(OwnFunctions(decl.Name, decl.Location.Source), function)
            ?? (decl.Visibility == Visibility.File ? null : FindSameFunction(PrivateElsewhere(decl.Name, decl.Location.Source), function));
        if (existing is null)
            AddFunction(function, decl);
        else
        {
            Error(Catalog.DuplicateFunction(decl.NameLocation, "function", function.Signature, existing.Declaration?.NameLocation, DiffersOnlyInEscaping(function, existing) ? existing.Signature : null));
        }
    }

    /// <summary>
    /// Identifies a method signature as seen from a type whose type arguments <paramref name="map"/>
    /// applies. A function's own type parameters compare by position. Nullability is ignored, since
    /// <c>T</c> and <c>T?</c> are the same type at runtime and can't be told apart by overloading,
    /// and so is a reference's, since <c>ref x</c> could be passed to either.
    /// </summary>
    static string MethodKey(FunctionSymbol function, TypeMap map)
    {
        var parameters = string.Join(",", function.Parameters.Select(p => p.RefKind + TypeKey(function, map.Apply(p.Type))));
        return $"{function.Name}|{function.TypeParameters.Count}|{parameters}";
    }

    static string TypeKey(FunctionSymbol function, WylType type) =>
        type is NullableReferenceType reference ? TypeKey(function, reference.Referenced)
        : type.IsNullable ? TypeKey(function, type.NonNull)
        : type is TypeParameterType { Parameter: var p } && function.TypeParameters.Contains(p) ? $"#{function.TypeParameters.ToList().IndexOf(p)}"
        : type.Symbol is { } s && type.TypeArguments.Count > 0 ? $"{s.Name}<{string.Join(",", type.TypeArguments.Select(a => TypeKey(function, a)))}>"
        : type.Name;

    /// <summary>
    /// The bounds of a function's own type parameters, by position, in which two top-level
    /// functions with the same parameter types may differ (spec <c>functions.overloading</c>).
    /// </summary>
    static string BoundsKey(FunctionSymbol function) =>
        string.Join(";", function.TypeParameters.Select(p =>
            $"{(p.IsCopyableBound ? "copyable&" : "")}{(p.IsSendableBound ? "sendable&" : "")}{string.Join("&", p.Bounds.Select(b => TypeKey(function, b)).Order(StringComparer.Ordinal))}"));

    static FunctionSymbol? FindSameSignature(IEnumerable<FunctionSymbol>? candidates, FunctionSymbol function) =>
        candidates?.FirstOrDefault(f => MethodKey(f, TypeMap.Empty) == MethodKey(function, TypeMap.Empty));

    /// <summary>A top-level function of the same parameter types and the same bounds.</summary>
    static FunctionSymbol? FindSameFunction(IEnumerable<FunctionSymbol>? candidates, FunctionSymbol function) =>
        candidates?.FirstOrDefault(f => MethodKey(f, TypeMap.Empty) == MethodKey(function, TypeMap.Empty) && BoundsKey(f) == BoundsKey(function));

    /// <summary>Whether two methods have the same parameter and return types, including nullability.</summary>
    static bool HasSameTypes(MethodReference a, MethodReference b) =>
        a.ReturnType == b.ReturnType
        && Enumerable.Range(0, a.Symbol.Parameters.Count).All(i => a.ParameterType(i) == b.ParameterType(i));

    /// <summary>
    /// A function's or method's parameters, and its by-value receiver, if it writes one as its first
    /// parameter (spec <c>structs.methods.by-value</c>).
    /// </summary>
    List<LocalSymbol> BindParameters(IReadOnlyList<ParameterSyntax> parameters, out LocalSymbol? receiver)
    {
        var result = new List<LocalSymbol>();
        receiver = null;
        foreach (var parameter in parameters)
        {
            if (result.Any(p => p.Name == parameter.Name) || receiver?.Name == parameter.Name)
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
            if (parameter.IsReceiver)
                receiver = local;
            else
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
            HasCallerLocation(decl, s);
            var type = BindType(decl.Type, allowVoid: false);
            CheckStorable(type, decl.Type.Location, "the type of a field");
            var field = new FieldSymbol(decl.Name, type, s, decl);
            if (s.IsExtern)
            {
                field.CppName = WrittenCppName(decl) ?? decl.Name;
                if (decl.Initializer is not null)
                    Error(Catalog.ExternFieldInitializer(decl.NameLocation, decl.Name, s.IsUnion ? "union" : "struct"));
            }
            var previous = s.FindField(decl.Name) ?? s.FindStaticField(decl.Name);
            if (previous is not null)
            {
                Error(Catalog.DuplicateField(decl.NameLocation, decl.Name, previous.Declaration.NameLocation));
                continue;
            }
            TakesLanguageName(decl.Name, decl.NameLocation);
            if (field.IsStatic)
            {
                CheckStaticField(field, s);
                s.StaticFields.Add(field);
            }
            else
            {
                CheckSharedField(field, s);
                s.Fields.Add(field);
            }
        }

        var writtenEquality = WrittenEquality(s);
        foreach (var decl in s.Declaration.Methods.Concat(writtenEquality))
        {
            // A constructor has its type's name, which the type's declaration checked.
            if (!decl.IsConstructor)
                TakesLanguageName(decl.Name, decl.NameLocation);
            var ownTypeParameters = DeclareTypeParameters(decl.TypeParameters, s.TypeParameters);
            typeParameters = [.. s.TypeParameters, .. ownTypeParameters];
            BindBounds(decl.TypeParameters, ownTypeParameters);
            int signatureChecks = boundChecks.Count;
            var parameters = BindParameters(decl.Parameters, out var receiver);
            var receiverSyntax = decl.Parameters.FirstOrDefault(p => p.IsReceiver);
            var modifiers = decl.Modifiers;
            bool writesReceiver = receiver is not null;
            if (writesReceiver && decl.IsConstructor)
                Error(Catalog.ReceiverOutsideValueMethod(receiverSyntax!.Location));
            receiver = decl.IsConstructor ? null : receiver;
            bool isInterface = s.Kind == TypeKind.Interface;
            // An interface method is as visible as its interface (spec programs.visibility).
            var reach = isInterface ? s.Reach : Reach.Of(decl).Within(s.Reach);
            if (decl.IsConstructor)
            {
                if (s.IsExtern)
                {
                    Error(Catalog.ExternStructConstructor(decl.NameLocation, s.IsUnion ? "union" : "struct"));
                    continue;
                }
                if (parameters.Count == 0 && !writesReceiver && s.Kind == TypeKind.Struct)
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
                    PassesLocation = HasCallerLocation(decl, s),
                };
                BindWhere(decl, constructor, s);
                CheckSignatureIn(constructor, signatureChecks);
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
                ReceiverParameter = receiver,
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
                IsUnsafe = (modifiers & Modifiers.Unsafe) != 0 || IsUnsafeExtern(decl, s),
                PassesLocation = HasCallerLocation(decl, s),
                BuiltinCppName = ExternFunctionName(decl, s),
                IsPure = IsPure(decl, s),
                IsCCallback = IsCCallback(decl, s),
            };
            CheckExternFunction(method, s);
            BindWhere(decl, method, s);
            CheckSignatureIn(method, signatureChecks);
            if (receiver is not null)
                CheckReceiver(method, s, receiverSyntax!, receiver);
            else
            {
                if (method.ReturnRefKind != RefKind.None)
                    CheckReturnsReference(method);
                CheckReturnsView(method);
            }
            if (method.PassesLocation && method.IsDispatched)
                Error(Catalog.CallerLocationOnDispatched(decl.NameLocation));
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

    /// <summary>
    /// Reports annotations that don't apply to the declaration, and returns whether it has
    /// <c>@CallerLocation</c>. A member of a type gives its type as <paramref name="owner"/>.
    /// </summary>
    bool HasCallerLocation(Declaration decl, TypeSymbol? owner = null)
    {
        bool found = false;
        bool isExtern = decl is FunctionDecl function && IsExternFunction(function, owner);
        foreach (var annotation in decl.Annotations)
        {
            if (annotation.Name == "CallerLocation" && decl is FunctionDecl located)
            {
                found = true;
                CheckNoArguments(annotation);
                if (isExtern && BlockOf(located, owner) is { IsStd: false })
                    Error(Catalog.CallerLocationOnExtern(annotation.Location));
            }
            else if (annotation.Name == "Acyclic" && decl is TypeDecl { Kind: TypeKind.Class })
            {
                CheckNoArguments(annotation);
            }
            else if (annotation.Name is "Pure" or "Safe" && isExtern)
            {
                CheckNoArguments(annotation);
            }
            else if (annotation.Name == CCallbackName && decl is FunctionDecl callback && IsCCallback(callback, owner))
            {
                CheckNoArguments(annotation);
            }
            // On a member of a block, or on a field of a block's struct or union (spec interop.extern.names).
            else if (annotation.Name == CppNameName && (decl.Block is not null || decl is FieldDecl && owner is { IsExtern: true }))
            {
                StringArgument(annotation, "name");
            }
            else if (annotation.Name == SuppressWarnings)
            {
                CheckSuppressWarnings(annotation);
            }
            // '@Repr' is checked with the enum's variants.
            else if (annotation.Name != "Repr" || decl is not TypeDecl { Kind: TypeKind.Enum })
            {
                ReportMisplacedAnnotation(annotation);
            }
        }
        return found;
    }

    /// <summary>A returned span views the receiver, so only instance methods may return one.</summary>
    void CheckReturnsView(FunctionSymbol function)
    {
        if (function.ReturnType.IsRefStruct && !function.IsInstanceMethod)
            Error(Catalog.ViewReturnFromNonInstance(function.Declaration!.NameLocation, function.ReturnType.ToString()));
    }

    /// <summary>A returned reference points into the receiver or into what it reaches, so only instance methods may return one.</summary>
    void CheckReturnsReference(FunctionSymbol method)
    {
        var location = method.Declaration!.NameLocation;
        var keyword = FunctionSymbol.RefPrefix(method.ReturnRefKind).Trim() + (method.ReturnType.IsNullableReference ? "?" : "");
        var referenced = method.ReturnType.Referred;
        if (method.IsStatic)
            Error(Catalog.StaticReferenceReturn(location, keyword));
        else if (referenced == WylType.Void)
            Error(Catalog.VoidReferenceReturn(location, keyword));
        else if (referenced.NonNull.IsFunction)
            Error(Catalog.ReferenceToPassedWithoutCopying(location, keyword, referenced.NonNull.ToString()));
    }

    /// <summary>
    /// What a method with a by-value receiver must be and take: not a class's or interface's, not a
    /// constructor or an indexer, not `mut` or `static`, a receiver of the declaring type itself, and
    /// no reference or view returned (spec <c>structs.methods.by-value</c>).
    /// </summary>
    void CheckReceiver(FunctionSymbol method, TypeSymbol owner, ParameterSyntax syntax, LocalSymbol receiver)
    {
        var location = syntax.Location;
        if (owner.Kind is not (TypeKind.Struct or TypeKind.Enum) || method.IsIndexer || method.IsIndexerSetter)
            Error(Catalog.ReceiverOutsideValueMethod(location));
        if (receiver.RefKind != RefKind.None)
            Error(Catalog.ReceiverMode(location));
        if (receiver.Type != WylType.Error && receiver.Type != owner.Type)
            Error(Catalog.ReceiverType(location, owner.Type.ToString(), receiver.Type.ToString()));
        var modifiers = new List<string>();
        if ((method.Declaration!.Modifiers & Modifiers.Mut) != 0)
            modifiers.Add("'mut'");
        if (method.IsStatic)
            modifiers.Add("'static'");
        if (modifiers.Count > 0)
            Error(Catalog.ReceiverModifier(location, string.Join(" or ", modifiers)));
        if (method.ReturnRefKind != RefKind.None || method.ReturnType.IsNullableReference || method.ReturnType.IsRefStruct)
            Error(Catalog.ReceiverReferenceReturn(location, method.ReturnType.ToString()));
    }
}
