using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Semantics;

// Each kind interns what it builds in a table keyed by what it is built from. The built-in types
// are shared by compilations running in parallel (as in the test runner), so the tables are
// thread-safe, or two compilations' `int[3]`s would differ; and weak, so a compilation's types
// die with it.

/// <summary>A declared struct, class, interface, or enum applied to type arguments.</summary>
public sealed class NamedType : VplType
{
    // In the order they were made, which the instantiations a program emits follow.
    static readonly ConditionalWeakTable<TypeSymbol, Dictionary<IReadOnlyList<VplType>, NamedType>> instances = new();

    NamedType(TypeSymbol symbol, IReadOnlyList<VplType> typeArguments)
    {
        Symbol = symbol;
        TypeArguments = typeArguments;
    }

    internal static NamedType Of(TypeSymbol symbol, IReadOnlyList<VplType> typeArguments)
    {
        var made = instances.GetValue(symbol, static _ => new(TypeListComparer.Instance));
        lock (made)
        {
            if (!made.TryGetValue(typeArguments, out var type))
                made[typeArguments] = type = new NamedType(symbol, [.. typeArguments]);
            return type;
        }
    }

    internal static IReadOnlyList<NamedType> InstancesOf(TypeSymbol symbol)
    {
        var made = instances.GetValue(symbol, static _ => new(TypeListComparer.Instance));
        lock (made)
            return [.. made.Values];
    }

    public override TypeSymbol Symbol { get; }
    public override IReadOnlyList<VplType> TypeArguments { get; }

    public override IReadOnlyList<VplType> Parts => TypeArguments;

    public override VplType MapParts(Func<VplType, VplType> map) =>
        TypeArguments.Count == 0 ? this : Symbol.Construct([.. TypeArguments.Select(map)]);

    private protected override string BuildName() =>
        TypeArguments.Count == 0 ? Symbol.Name : $"{Symbol.Name}<{string.Join(", ", TypeArguments)}>";
}

/// <summary>A type parameter, which stands for the type argument it is given.</summary>
public sealed class TypeParameterType : VplType
{
    static readonly ConditionalWeakTable<TypeParameterSymbol, TypeParameterType> types = new();

    TypeParameterType(TypeParameterSymbol parameter) => Parameter = parameter;

    internal static TypeParameterType Of(TypeParameterSymbol parameter) => types.GetValue(parameter, static p => new(p));

    public TypeParameterSymbol Parameter { get; }

    public override IReadOnlyList<VplType> Parts => [];
    public override VplType MapParts(Func<VplType, VplType> map) => this;
    private protected override string BuildName() => Parameter.Name;
}

/// <summary>
/// <c>T?</c>. A nullable reference is still a reference to the same class, so it keeps the symbol; a
/// nullable value is a different type, which must not be mistaken for its struct or enum.
/// </summary>
public sealed class NullableType : VplType
{
    static readonly ConditionalWeakTable<VplType, NullableType> forms = new();

    NullableType(VplType nonNull) => NonNull = nonNull;

    internal static NullableType Of(VplType nonNull) => forms.GetValue(nonNull, static t => new(t));

    public override VplType NonNull { get; }
    public override TypeSymbol? Symbol => NonNull.IsReference ? NonNull.Symbol : null;
    public override IReadOnlyList<VplType> TypeArguments => NonNull.IsReference ? NonNull.TypeArguments : [];

    public override IReadOnlyList<VplType> Parts => [NonNull];
    public override VplType MapParts(Func<VplType, VplType> map) => map(NonNull).Nullable;
    private protected override string BuildName() => Grouped(NonNull) + "?";
}

/// <summary>A type whose values hold or point to elements, selected by index: a fixed array or a pointer.</summary>
public abstract class IndexedType : VplType
{
    private protected IndexedType(VplType element) => Element = element;

    public VplType Element { get; }

    public override IReadOnlyList<VplType> Parts => [Element];
}

/// <summary>A pointer <c>T*</c>, for unsafe code.</summary>
public sealed class PointerType : IndexedType
{
    static readonly ConditionalWeakTable<VplType, PointerType> pointers = new();

    PointerType(VplType element) : base(element) { }

    internal static PointerType To(VplType element) => pointers.GetValue(element, static e => new(e));

    public override VplType MapParts(Func<VplType, VplType> map) => map(Element).PointerTo;
    private protected override string BuildName() => Grouped(Element) + "*";
}

/// <summary>A fixed array <c>T[N]</c>, whose elements are stored inline.</summary>
public sealed class FixedArrayType : IndexedType
{
    static readonly ConditionalWeakTable<VplType, ConcurrentDictionary<int, FixedArrayType>> arrays = new();

    FixedArrayType(VplType element, int length) : base(element) => Length = length;

    internal static FixedArrayType Of(VplType element, int length) =>
        arrays.GetValue(element, static _ => new()).GetOrAdd(length, static (n, e) => new FixedArrayType(e, n), element);

    /// <summary>The <c>N</c> of <c>T[N]</c>.</summary>
    public int Length { get; }

    public override VplType MapParts(Func<VplType, VplType> map) => map(Element).FixedArrayOf(Length);

    // Like C, the outer dimension comes first: `int[4][2]` is four `int[2]`s.
    private protected override string BuildName() => Grouped(Innermost) + Dimensions;
    VplType Innermost => Element is FixedArrayType inner ? inner.Innermost : Element;
    string Dimensions => $"[{Length}]" + (Element is FixedArrayType inner ? inner.Dimensions : "");
}

/// <summary>
/// A function type <c>(A, ref B) -> R</c>. It has two forms, written the same way: the escaping one
/// is a heap closure that may be stored, and the other may only be called and passed on during the
/// call it was given to.
/// </summary>
public sealed class FunctionType : VplType
{
    static readonly ConditionalWeakTable<VplType, ConcurrentDictionary<IReadOnlyList<FunctionParameterType>, FunctionType>> byReturnType = new();

    // The other form: created on demand by the non-escaping one, and given to the escaping one.
    FunctionType? twin;

    FunctionType(IReadOnlyList<FunctionParameterType> parameters, VplType returnType, FunctionType? nonEscaping)
    {
        Parameters = parameters;
        ReturnType = returnType;
        IsEscaping = nonEscaping is not null;
        twin = nonEscaping;
    }

    internal static FunctionType Of(IReadOnlyList<FunctionParameterType> parameters, VplType returnType) =>
        byReturnType.GetValue(returnType, static _ => new(FunctionParameterListComparer.Instance))
            .GetOrAdd(parameters, static (p, r) => new FunctionType([.. p], r, null), returnType);

    public IReadOnlyList<FunctionParameterType> Parameters { get; }
    public VplType ReturnType { get; }
    public override bool IsEscaping { get; }

    public FunctionType Escaping => IsEscaping ? this : twin ?? Publish(ref twin, new FunctionType(Parameters, ReturnType, this));
    public FunctionType NonEscaping => IsEscaping ? twin! : this;
    public FunctionType WithEscaping(bool escaping) => escaping ? Escaping : NonEscaping;

    public override IReadOnlyList<VplType> Parts => [.. Parameters.Select(p => p.Type), ReturnType];

    public override VplType MapParts(Func<VplType, VplType> map) =>
        map(ReturnType).FunctionReturning([.. Parameters.Select(p => p with { Type = map(p.Type) })]).WithEscaping(IsEscaping);

    private protected override string BuildName() => $"({string.Join(", ", Parameters)}) -> {ReturnType}";
}

/// <summary>
/// <c>ref? T</c> or <c>in? T</c>, a reference that may refer to nothing. Such a type is only ever
/// that of a <c>ref</c> or <c>in</c> local, parameter, or return, and of the expressions reading
/// one; it is not nullable in the sense of <see cref="VplType.IsNullable"/>, so no operation on
/// values accepts it until a null check narrows it to <c>T</c>.
/// </summary>
public sealed class NullableReferenceType : VplType
{
    static readonly ConditionalWeakTable<VplType, NullableReferenceType> refs = new(), ins = new();

    NullableReferenceType(VplType referenced, RefKind kind)
    {
        Referenced = referenced;
        Kind = kind;
    }

    internal static NullableReferenceType To(VplType referenced, RefKind kind) => kind == RefKind.In
        ? ins.GetValue(referenced, static t => new(t, RefKind.In))
        : refs.GetValue(referenced, static t => new(t, RefKind.Ref));

    public VplType Referenced { get; }

    /// <summary>Whether a <c>ref?</c> or an <c>in?</c>.</summary>
    public RefKind Kind { get; }

    public override IReadOnlyList<VplType> Parts => [Referenced];
    public override VplType MapParts(Func<VplType, VplType> map) => map(Referenced).NullableReference(Kind);
    private protected override string BuildName() => $"{FunctionSymbol.RefPrefix(Kind).Trim()}? {Referenced}";
}
