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
        }
    }

    public bool IsSpan(VplType type) => type.Symbol is { } s && (s == Span || s == ReadOnlySpan);
}
