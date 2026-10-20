using System.Numerics;

namespace Vpl.Compiler.Semantics;

public sealed class VplType
{
    public static readonly VplType Void = new("void");
    public static readonly VplType Bool = new("bool");
    public static readonly VplType Int = new("int");
    public static readonly VplType Long = new("long");
    public static readonly VplType Float = new("float");
    public static readonly VplType Double = new("double");
    public static readonly VplType String = new("String");

    /// <summary>The type of an expression that already produced an error; converts to anything to avoid cascades.</summary>
    public static readonly VplType Error = new("<error>");

    VplType(string name, StructSymbol? @struct = null)
    {
        Name = name;
        Struct = @struct;
    }

    internal static VplType ForStruct(StructSymbol symbol) => new(symbol.Name, symbol);

    public string Name { get; }
    public StructSymbol? Struct { get; }

    public bool IsStruct => Struct is not null;
    public bool IsInteger => this == Int || this == Long;
    public bool IsFloatingPoint => this == Float || this == Double;
    public bool IsNumeric => IsInteger || IsFloatingPoint;

    public override string ToString() => Name;
}

public static class Conversions
{
    /// <summary>Only conversions that can never lose information are implicit.</summary>
    public static bool IsImplicit(VplType from, VplType to) =>
        from == to
        || from == VplType.Error
        || to == VplType.Error
        || (from == VplType.Int && (to == VplType.Long || to == VplType.Double))
        || (from == VplType.Float && to == VplType.Double);

    public static bool IsExplicit(VplType from, VplType to) =>
        IsImplicit(from, to) || (from.IsNumeric && to.IsNumeric);

    /// <summary>Whether <c>==</c> is defined: primitives other than String, and structs whose fields all are.</summary>
    public static bool SupportsEquality(VplType type) => type.Struct is { } s
        ? s.FindNonEquatableField() is null
        : type != VplType.String && type != VplType.Void;

    public static bool IsExactlyRepresentable(long value, VplType floatingType) => floatingType == VplType.Float
        ? new BigInteger((float)value) == value
        : new BigInteger((double)value) == value;
}
