using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    static WylType? BuiltinType(string name) => name switch
    {
        "void" => WylType.Void,
        "bool" => WylType.Bool,
        "sbyte" => WylType.SByte,
        "byte" => WylType.Byte,
        "short" => WylType.Short,
        "ushort" => WylType.UShort,
        "int" => WylType.Int,
        "uint" => WylType.UInt,
        "long" => WylType.Long,
        "ulong" => WylType.ULong,
        "nint" => WylType.NInt,
        "nuint" => WylType.NUInt,
        "char" => WylType.Char,
        "float" => WylType.Float,
        "double" => WylType.Double,
        "String" => WylType.String,
        _ => null,
    };

    TypeParameterSymbol? LookupTypeParameter(string name) => typeParameters.LastOrDefault(p => p.Name == name);

    WylType BindType(TypeSyntax syntax, bool allowVoid)
    {
        var type = syntax.Shape switch
        {
            TypeShape.Named when syntax.Modules.Count > 0 => BindPathType(syntax),
            TypeShape.Named => BindTypeName(syntax.Name, syntax.TypeArguments, syntax.Location),
            TypeShape.Function => BindFunctionType(syntax),
            TypeShape.NullableReference => BindNullableReferenceType(syntax),
            _ => BindCompositeType(syntax),
        };
        if (type == WylType.Void && !allowVoid)
        {
            Error(Catalog.VoidNotAllowed(syntax.Location));
            return WylType.Error;
        }
        if (!syntax.IsNullable || type == WylType.Error)
            return type;
        if (type.CanBeNullable)
            return type.Nullable;
        Error(type.IsPointer ? Catalog.NullablePointer(syntax.Location, type.ToString()) : Catalog.NullableView(syntax.Location, type.ToString()));
        return WylType.Error;
    }

    /// <summary>
    /// <c>ref? T</c> or <c>in? T</c>, which the parser only reads where a reference is declared.
    /// What it may refer to is checked as for <c>ref T</c>, where it is declared.
    /// </summary>
    WylType BindNullableReferenceType(TypeSyntax syntax)
    {
        // The parser reads `ref? var` only for a local, which infers the type itself, and a loop
        // variable, which can't be `ref?`.
        var element = syntax.Element ?? throw new InvalidOperationException("'ref? var' is bound only where its type is inferred");
        var referenced = BindType(element, allowVoid: false);
        return referenced == WylType.Error ? referenced : referenced.NullableReference(syntax.ReferenceKind);
    }

    WylType BindCompositeType(TypeSyntax syntax)
    {
        var element = BindType(syntax.Element!, allowVoid: syntax.Shape == TypeShape.Pointer);
        if (element == WylType.Void)
            return element.PointerTo;
        if (element == WylType.Error || !CheckStorable(element, syntax.Element!.Location, syntax.Shape == TypeShape.Pointer ? "pointed to" : "an array's element"))
            return WylType.Error;
        switch (syntax.Shape)
        {
            case TypeShape.Pointer:
                return element.PointerTo;
            case TypeShape.FixedArray when syntax.Length > int.MaxValue:
                Error(Catalog.FixedArrayLength(syntax.Location));
                return WylType.Error;
            case TypeShape.FixedArray:
                return element.FixedArrayOf((int)syntax.Length);
            default:
                return Construct(stdlib.Array ?? throw new InvalidOperationException("the standard library declares no 'Array'"), [element], syntax.Location);
        }
    }

    WylType BindTypeName(string name, IReadOnlyList<TypeSyntax> typeArgumentSyntax, Location location)
    {
        var arguments = typeArgumentSyntax.Select(a => BindType(a, allowVoid: false)).ToList();
        if (arguments.Contains(WylType.Error))
            return WylType.Error;

        if (LookupTypeParameter(name) is { } parameter)
            return arguments.Count == 0 ? parameter.Type : NotGeneric(name, location);
        if (BuiltinType(name) is { } builtin)
            return arguments.Count == 0 ? builtin : NotGeneric(name, location);
        if (LookupType(name, location) is { } s)
            return Construct(s, arguments, location);

        Error(HiddenElsewhere(name, location) ?? Catalog.UnknownType(location, name));
        return WylType.Error;
    }

    WylType NotGeneric(string name, Location location)
    {
        Error(Catalog.NotGeneric(location, name));
        return WylType.Error;
    }

    WylType Construct(TypeSymbol s, IReadOnlyList<WylType> arguments, Location location)
    {
        if (!s.Reach.Includes(location.Source))
        {
            CheckVisible(location, s.Reach, $"{SyntaxFacts.KindName(s.Kind)} '{s.Name}'");
            return WylType.Error;
        }
        if (!CheckTypeArguments(arguments, location))
            return WylType.Error;
        if (arguments.Count == s.TypeParameters.Count)
        {
            if (s.IsWeak && !CheckWeakTarget(arguments[0], location))
                return WylType.Error;
            if (s.IsAtomic && !arguments[0].IsInteger)
            {
                Error(Catalog.AtomicOfNonInteger(location, arguments[0].ToString()));
                return WylType.Error;
            }
            QueueBoundChecks(s.TypeParameters, arguments, location);
            return s.Construct(arguments);
        }
        if (!s.IsGeneric)
            return NotGeneric(s.Name, location);
        Error(Catalog.TypeArgumentCount(location, s.Name, s.TypeParameters.Count, arguments.Count));
        return WylType.Error;
    }

    /// <summary>
    /// A weak reference refers to an object, and can always be null. A type parameter is rejected,
    /// since it may stand for a struct.
    /// </summary>
    bool CheckWeakTarget(WylType target, Location location)
    {
        if (target.IsReference && !target.IsNullable)
            return true;
        Error(target.IsReference ? Catalog.WeakOfNullable(location, target.NonNull.ToString()) : Catalog.WeakOfValue(location, target.ToString()));
        return false;
    }

    /// <summary>A reference, or null, converts implicitly to a weak reference to it.</summary>
    bool IsWeakConversion(WylType from, WylType to) =>
        to.Symbol is { IsWeak: true } && (from == WylType.Null || (from.IsReference && Conversions.IsImplicit(from, to.TypeArguments[0].Nullable, currentFunction)));

    /// <summary>A type argument must be storable.</summary>
    bool CheckTypeArguments(IReadOnlyList<WylType> arguments, Location location)
    {
        bool ok = true;
        foreach (var argument in arguments)
        {
            if (!CheckStorable(argument, location, "a type argument"))
                ok = false;
        }
        return ok;
    }

    void QueueBoundChecks(IReadOnlyList<TypeParameterSymbol> parameters, IReadOnlyList<WylType> arguments, Location location)
    {
        var map = TypeMap.Empty.With(parameters, arguments);
        foreach (var (parameter, argument) in parameters.Zip(arguments))
            boundChecks.Add((argument, parameter, map, location, currentFunction));
    }

    void CheckBounds()
    {
        // A check runs after every body is bound, so it carries the function its type was written in.
        foreach (var (argument, parameter, map, location, function) in boundChecks)
        {
            foreach (var (name, bound) in UnsatisfiedBounds(parameter, argument, map, function))
            {
                Error(bound is null
                    ? Catalog.PropertyNotSatisfied(location, argument.ToString(), name, parameter.Name)
                    : Catalog.BoundNotSatisfied(location, argument.ToString(), name, parameter.Name));
            }
        }
    }

    /// <summary>
    /// The bounds of <paramref name="parameter"/> that <paramref name="argument"/> fails (spec
    /// <c>generics.bounds</c>), with <paramref name="map"/> applied to the interfaces: each as its
    /// name, and the interface, or null for the properties <c>copyable</c> and <c>sendable</c>.
    /// </summary>
    IEnumerable<(string Name, WylType? Interface)> UnsatisfiedBounds(TypeParameterSymbol parameter, WylType argument, TypeMap map, FunctionSymbol? function)
    {
        if (argument == WylType.Error)
            yield break;
        if (parameter.IsCopyableBound && !argument.IsCopyableIn(function))
            yield return ("copyable", null);
        if (parameter.IsSendableBound && !argument.IsSendableIn(function))
            yield return ("sendable", null);
        foreach (var bound in parameter.Bounds.Select(map.Apply))
        {
            if (!stdlib.SatisfiesBound(argument, bound, function))
                yield return (bound.ToString(), bound);
        }
    }

    /// <summary>Whether a method's type arguments satisfy the bounds of its own type parameters.</summary>
    bool SatisfiesBounds(MethodReference method) =>
        method.Symbol.TypeParameters.Zip(method.TypeArguments).All(p => !UnsatisfiedBounds(p.First, p.Second, method.Map, currentFunction).Any());

    IReadOnlyList<WylType>? BindTypeArguments(IReadOnlyList<TypeSyntax> syntax) =>
        syntax.Count == 0 ? null : [.. syntax.Select(a => BindType(a, allowVoid: false))];

    /// <summary>A field of the type or, for classes, of a base class, with the type it was found in.</summary>
    static (FieldSymbol Field, WylType Owner)? LookupField(WylType type, string name)
    {
        for (var t = type.Narrowed; t?.Symbol is not null; t = t.BaseType)
        {
            if (t.Symbol.FindField(name) is { } field)
                return (field, t);
        }
        return null;
    }

    static (FieldSymbol Field, WylType Owner)? LookupStaticField(WylType type, string name)
    {
        for (var t = type.NonNull; t?.Symbol is not null; t = t.BaseType)
        {
            if (t.Symbol.FindStaticField(name) is { } field)
                return (field, t);
        }
        return null;
    }

    /// <summary>
    /// Methods callable on a value of the type: its own, then inherited ones that it does not
    /// override. A type parameter has the methods of its bounds.
    /// </summary>
    List<Candidate> LookupMethods(WylType type, string name)
    {
        type = stdlib.MemberHost(type);
        var result = new List<Candidate>();
        var seen = new HashSet<string>();
        IEnumerable<WylType> searched = type.IsTypeParameter ? Conversions.Supertypes(type, currentFunction) : [type, .. Conversions.Supertypes(type, currentFunction)];
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

    static string DisplayName(WylType type) => type.Symbol?.Name ?? type.Name;
}
