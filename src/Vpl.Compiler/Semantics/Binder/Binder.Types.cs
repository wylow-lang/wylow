using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

public sealed partial class Binder
{
    static VplType? BuiltinType(string name) => name switch
    {
        "void" => VplType.Void,
        "bool" => VplType.Bool,
        "sbyte" => VplType.SByte,
        "byte" => VplType.Byte,
        "short" => VplType.Short,
        "ushort" => VplType.UShort,
        "int" => VplType.Int,
        "uint" => VplType.UInt,
        "long" => VplType.Long,
        "ulong" => VplType.ULong,
        "nint" => VplType.NInt,
        "nuint" => VplType.NUInt,
        "char" => VplType.Char,
        "float" => VplType.Float,
        "double" => VplType.Double,
        "String" => VplType.String,
        _ => null,
    };

    TypeParameterSymbol? LookupTypeParameter(string name) => typeParameters.LastOrDefault(p => p.Name == name);

    VplType BindType(TypeSyntax syntax, bool allowVoid)
    {
        var type = syntax.Shape switch
        {
            TypeShape.Named => BindTypeName(syntax.Name, syntax.TypeArguments, syntax.Location),
            TypeShape.Function => BindFunctionType(syntax),
            _ => BindCompositeType(syntax),
        };
        if (type == VplType.Void && !allowVoid)
        {
            Error(syntax.Location, "'void' cannot be used here");
            return VplType.Error;
        }
        if (!syntax.IsNullable || type == VplType.Error)
            return type;
        if (type.CanBeNullable)
            return type.Nullable;
        Error(syntax.Location, type.IsPointer
            ? $"'{type}?' is not needed: a pointer can already be null"
            : $"'{type}' cannot be nullable, since it is a 'ref struct'");
        return VplType.Error;
    }

    VplType BindCompositeType(TypeSyntax syntax)
    {
        var element = BindType(syntax.Element!, allowVoid: false);
        if (element == VplType.Error || !CheckStorable(element, syntax.Element!.Location, syntax.Shape == TypeShape.Pointer ? "pointed to" : "an array's element"))
            return VplType.Error;
        switch (syntax.Shape)
        {
            case TypeShape.Pointer:
                return element.PointerTo;
            case TypeShape.FixedArray when syntax.Length > int.MaxValue:
                Error(syntax.Location, "a fixed array's length must fit in an 'int'");
                return VplType.Error;
            case TypeShape.FixedArray:
                return element.FixedArrayOf((int)syntax.Length);
            default:
                if (stdlib.Array is not { } array)
                {
                    Error(syntax.Location, "'T[]' arrays are not supported yet");
                    return VplType.Error;
                }
                return Construct(array, [element], syntax.Location);
        }
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
        if (!CheckTypeArguments(arguments, location))
            return VplType.Error;
        if (arguments.Count == s.TypeParameters.Count)
        {
            if (s == stdlib.Weak && !CheckWeakTarget(arguments[0], location))
                return VplType.Error;
            QueueBoundChecks(s.TypeParameters, arguments, location);
            return s.Construct(arguments);
        }
        if (!s.IsGeneric)
            return NotGeneric(s.Name, location);
        int count = s.TypeParameters.Count;
        Error(location, $"'{s.Name}' requires {count} type argument{(count == 1 ? "" : "s")}");
        return VplType.Error;
    }

    /// <summary>
    /// A weak reference refers to an object, and can always be null. A type parameter is rejected,
    /// since it may stand for a struct.
    /// </summary>
    bool CheckWeakTarget(VplType target, Location location)
    {
        if (target.IsReference && !target.IsNullable)
            return true;
        Error(location, target.IsReference
            ? $"write 'Weak<{target.NonNull}>': a weak reference can always be null"
            : $"'Weak' needs a class or interface type, not '{target}'");
        return false;
    }

    /// <summary>A reference, or null, converts implicitly to a weak reference to it.</summary>
    bool IsWeakConversion(VplType from, VplType to) =>
        stdlib.IsWeak(to) && (from == VplType.Null || (from.IsReference && Conversions.IsImplicit(from, to.TypeArguments[0].Nullable)));

    bool CheckTypeArguments(IReadOnlyList<VplType> arguments, Location location) =>
        arguments.All(a => CheckStorable(a, location, "a type argument"));

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
}
