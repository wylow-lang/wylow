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
        }
    }

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
