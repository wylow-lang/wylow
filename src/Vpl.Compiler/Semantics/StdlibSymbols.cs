using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Semantics;

/// <summary>
/// The standard library types the compiler has hooks for, found by name among the declarations of
/// the standard library's sources. Each is null only if the standard library failed to declare it.
/// </summary>
public sealed class StdlibSymbols
{
    /// <summary><c>try</c> and the unused-result error.</summary>
    public TypeSymbol? Result { get; private set; }

    /// <summary>The class that <c>T[]</c> stands for.</summary>
    public TypeSymbol? Array { get; private set; }

    /// <summary>The span types that fixed arrays and <see cref="Spannable"/> types convert to.</summary>
    public TypeSymbol? Span { get; private set; }
    public TypeSymbol? ReadOnlySpan { get; private set; }

    /// <summary>Types that convert to spans and can be iterated, through <c>asSpan</c> and <c>asReadOnlySpan</c>.</summary>
    public TypeSymbol? Spannable { get; private set; }
    public TypeSymbol? ReadOnlySpannable { get; private set; }

    /// <summary>Classes whose <c>beginBorrow</c> and <c>endBorrow</c> the compiler calls around every borrow.</summary>
    public TypeSymbol? Borrowable { get; private set; }

    /// <summary>Weak references, which <c>T</c> and <c>T?</c> convert to.</summary>
    public TypeSymbol? Weak { get; private set; }

    /// <summary>Types string interpolation can write, which the runtime's formatted types satisfy without implementing.</summary>
    public TypeSymbol? Printable { get; private set; }

    /// <summary>Types in an order, which the integers, <c>char</c>, and <c>String</c> satisfy without implementing.</summary>
    public TypeSymbol? Comparable { get; private set; }

    /// <summary>
    /// The members of the built-in <c>String</c>, declared by an <c>extern struct</c> of that name.
    /// It is not a type: <c>String</c> always names <see cref="VplType.String"/>.
    /// </summary>
    public TypeSymbol? String { get; private set; }

    public void Register(TypeSymbol symbol)
    {
        switch (symbol.Name)
        {
            case "Result": Result = symbol; break;
            case "Array": Array = symbol; break;
            case "Span": Span = symbol; break;
            case "ReadOnlySpan": ReadOnlySpan = symbol; break;
            case "Spannable": Spannable = symbol; break;
            case "ReadOnlySpannable": ReadOnlySpannable = symbol; break;
            case "Borrowable": Borrowable = symbol; break;
            case "Weak": Weak = symbol; break;
            case "Printable": Printable = symbol; break;
            case "Comparable": Comparable = symbol; break;
            case "String": String = symbol; break;
        }
    }

    /// <summary>The type whose declared members a value of the type has: itself, except for <c>String</c>.</summary>
    public VplType MemberHost(VplType type) => type == VplType.String && String is { } s ? s.Type : type;

    /// <summary>Whether a type is printable: formatted by the runtime, or implementing <see cref="Printable"/>.</summary>
    public bool IsPrintable(VplType type) =>
        Builtins.FormattedTypes.Contains(type) || (Printable is { } printable && Conversions.IsSubtype(type, printable.Type));

    public bool IsPrintableToString(FunctionSymbol method) => method.Name == "toString" && method.ContainingType is { } s && s == Printable;

    /// <summary>The integers, <c>char</c>, and <c>String</c>, which the runtime compares itself.</summary>
    public static bool IsBuiltinComparable(VplType type) => type.IsInteger || type == VplType.Char || type == VplType.String;

    /// <summary>Whether a type satisfies the bound, as a builtin comparable type does <c>Comparable</c> of itself.</summary>
    public bool SatisfiesBound(VplType type, VplType bound) =>
        Conversions.IsSubtype(type, bound)
        || (bound.Symbol == Printable && IsPrintable(type))
        || (bound.Symbol == Comparable && IsBuiltinComparable(type) && bound.TypeArguments[0] == type);

    public bool IsComparableCompareTo(FunctionSymbol method) => method.Name == "compareTo" && method.ContainingType is { } s && s == Comparable;

    public bool IsSpan(VplType type) => type.Symbol is { } s && (s == Span || s == ReadOnlySpan);

    public bool IsWeak(VplType type) => type.Symbol is { } s && s == Weak;

    /// <summary>
    /// The element type of a standard library container: a class implementing <see cref="ReadOnlySpannable"/>,
    /// which stores its elements behind a pointer rather than in fields. The cycle collector visits
    /// them through <c>asReadOnlySpan</c>. A user's class may return a span of its own fields, whose
    /// references would then be counted twice, so only the standard library's classes qualify.
    /// </summary>
    public VplType? ContainerElement(VplType type)
    {
        if (type.Symbol is not { Kind: TypeKind.Class } s || ReadOnlySpannable is not { } hook || !Stdlib.Contains(s.Declaration.Location.Source))
            return null;
        IEnumerable<VplType> candidates = [type, .. Conversions.Supertypes(type)];
        return candidates.FirstOrDefault(t => t.Symbol == hook)?.TypeArguments[0];
    }
}
