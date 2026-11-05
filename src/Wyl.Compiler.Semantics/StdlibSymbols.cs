using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

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

    /// <summary>The module of raw memory and the cycle collector, whose extern functions destroy values and make default ones.</summary>
    public static Module? MemoryModule => Package.Std.Root.FindChild("memory");

    public static bool IsMemoryFunction(FunctionSymbol function) =>
        function.Declaration is { } declaration && declaration.Location.Source.Module == MemoryModule;

    /// <summary>Weak references, which <c>T</c> and <c>T?</c> convert to.</summary>
    public TypeSymbol? Weak { get; private set; }

    /// <summary>Types string interpolation writes through <c>toString</c>.</summary>
    public TypeSymbol? Printable { get; private set; }

    /// <summary>Types in an order.</summary>
    public TypeSymbol? Comparable { get; private set; }

    /// <summary>
    /// The built-in types whose members and interfaces the standard library declares, each in an
    /// <c>extern struct</c> of its name. That declaration is not a type of its own: its name keeps
    /// naming the built-in type.
    /// </summary>
    static readonly Dictionary<string, WylType> HostedTypes =
        ((WylType[])[WylType.String, WylType.Bool, WylType.Char, WylType.Float, WylType.Double, .. WylType.Integers]).ToDictionary(t => t.Name);

    public static bool IsHostedTypeName(string name) => HostedTypes.ContainsKey(name);

    /// <summary>The hosted primitive types, whose extern members the runtime's <c>wyl::Builtin</c> has.</summary>
    public static IEnumerable<WylType> HostedPrimitives => HostedTypes.Values.Where(t => t != WylType.String);

    public static bool IsHostedPrimitive(WylType type) => type != WylType.String && HostedTypes.ContainsValue(type);

    /// <summary>The <c>extern struct</c> that declares each hosted built-in type's members.</summary>
    readonly Dictionary<WylType, TypeSymbol> hosts = [];

    /// <summary>
    /// The types the language itself is built on, because its syntax needs them (spec
    /// programs.builtin-names): every file sees them without importing them, and no other module may declare
    /// their names. <c>String</c> is a builtin type, and <c>Weak</c> and <c>Memory</c> are imported.
    /// </summary>
    public IEnumerable<TypeSymbol> LanguageTypes =>
        new[] { Result, Array, Span, ReadOnlySpan, Spannable, ReadOnlySpannable, Borrowable, Printable, Comparable }.OfType<TypeSymbol>();

    public TypeSymbol? LanguageType(string name) => LanguageTypes.FirstOrDefault(t => t.Name == name);

    /// <summary>
    /// Whether the name is one every file sees (spec programs.builtin-names), which nothing outside
    /// <c>std</c> may declare or import, so that it always means the language's own declaration.
    /// </summary>
    public bool IsLanguageName(string name) => name == "String" || LanguageType(name) is not null;

    /// <summary>The function that builds the <c>String[]</c> a <c>main(String[] args)</c> receives (Stdlib/arguments.wyl).</summary>
    public FunctionSymbol? ProgramArguments { get; private set; }

    public void Register(FunctionSymbol function)
    {
        if (function is { Name: "programArguments", Declaration.Location.Source.Module.IsRoot: true })
            ProgramArguments = function;
    }

    /// <summary>Whether a type is <c>String[]</c>, the type of <c>main</c>'s one parameter.</summary>
    public bool IsStringArray(WylType type) =>
        !type.IsNullable && type.Symbol is { } s && s == Array && type.TypeArguments is [var element] && element == WylType.String;

    public void Register(TypeSymbol symbol)
    {
        if (symbol.IsExtern && HostedTypes.TryGetValue(symbol.Name, out var hosted))
        {
            hosts[hosted] = symbol;
            return;
        }
        switch (symbol.Name)
        {
            case "Result": Result = symbol; break;
            case "Array": Array = symbol; break;
            case "Span": Span = symbol; break;
            // The runtime's String::bytes returns one (runtime/wyl/core.h).
            case "ReadOnlySpan": ReadOnlySpan = symbol; symbol.IsNamedByRuntime = true; break;
            case "Spannable": Spannable = symbol; break;
            case "ReadOnlySpannable": ReadOnlySpannable = symbol; break;
            case "Borrowable": Borrowable = symbol; break;
            case "Weak": Weak = symbol; symbol.IsWeak = true; break;
            case "Printable": Printable = symbol; break;
            case "Comparable": Comparable = symbol; break;
        }
    }

    /// <summary>The type whose declared members and interfaces a value of the type has: itself, except for a hosted built-in type.</summary>
    public WylType MemberHost(WylType type) => hosts.TryGetValue(type, out var host) ? host.Type : type;

    /// <summary>Whether a type implements <see cref="Printable"/>, which a bound or interpolation may ask.</summary>
    public bool IsPrintable(WylType type) => Printable is { } printable && SatisfiesBound(type, printable.Type);

    /// <summary>Whether a type satisfies an interface bound: implements it, itself or through its host.</summary>
    public bool SatisfiesBound(WylType type, WylType bound) => Conversions.IsSubtype(MemberHost(type), bound);

    public bool IsSpan(WylType type) => type.Symbol is { } s && (s == Span || s == ReadOnlySpan);

    public bool IsWeak(WylType type) => type.Symbol is { } s && s == Weak;

    /// <summary>
    /// The element type of a standard library container: a class implementing <see cref="ReadOnlySpannable"/>,
    /// which stores its elements behind a pointer rather than in fields. The cycle collector visits
    /// them through <c>asReadOnlySpan</c>. A user's class may return a span of its own fields, whose
    /// references would then be counted twice, so only the standard library's classes qualify.
    /// </summary>
    public WylType? ContainerElement(WylType type)
    {
        if (type.Symbol is not { Kind: TypeKind.Class } s || ReadOnlySpannable is not { } hook || !s.Declaration.Location.Source.Module.Package.IsStd)
            return null;
        IEnumerable<WylType> candidates = [type, .. Conversions.Supertypes(type)];
        return candidates.FirstOrDefault(t => t.Symbol == hook)?.TypeArguments[0];
    }
}
