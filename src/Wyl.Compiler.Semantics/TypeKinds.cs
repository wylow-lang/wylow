using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.Semantics;

// Each kind interns what it builds in a table keyed by what it is built from. The built-in types
// are shared by compilations running in parallel (as in the test runner), so the tables are
// thread-safe, or two compilations' `int[3]`s would differ; and weak, so a compilation's types
// die with it.

/// <summary>A declared struct, class, interface, or enum applied to type arguments.</summary>
public sealed class NamedType : WylType
{
    // In the order they were made, which the instantiations a program emits follow.
    static readonly ConditionalWeakTable<TypeSymbol, Dictionary<IReadOnlyList<WylType>, NamedType>> instances = new();

    NamedType(TypeSymbol symbol, IReadOnlyList<WylType> typeArguments)
    {
        Symbol = symbol;
        TypeArguments = typeArguments;
    }

    internal static NamedType Of(TypeSymbol symbol, IReadOnlyList<WylType> typeArguments)
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
    public override IReadOnlyList<WylType> TypeArguments { get; }

    public override IReadOnlyList<WylType> Parts => TypeArguments;

    public override WylType MapParts(Func<WylType, WylType> map)
    {
        if (TypeArguments.Count == 0)
            return this;
        WylType[] mapped = [.. TypeArguments.Select(map)];
        // Interning looks the arguments up under a lock, so a type that maps to itself skips it.
        return mapped.SequenceEqual(TypeArguments, ReferenceEqualityComparer.Instance) ? this : Symbol.Construct(mapped);
    }

    private protected override string BuildName() =>
        TypeArguments.Count == 0 ? Symbol.DisplayName : $"{Symbol.DisplayName}<{string.Join(", ", TypeArguments)}>";
}

/// <summary>A type parameter, which stands for the type argument it is given.</summary>
public sealed class TypeParameterType : WylType
{
    static readonly ConditionalWeakTable<TypeParameterSymbol, TypeParameterType> types = new();

    TypeParameterType(TypeParameterSymbol parameter) => Parameter = parameter;

    internal static TypeParameterType Of(TypeParameterSymbol parameter) => types.GetValue(parameter, static p => new(p));

    public TypeParameterSymbol Parameter { get; }

    public override IReadOnlyList<WylType> Parts => [];
    public override WylType MapParts(Func<WylType, WylType> map) => this;
    private protected override string BuildName() => Parameter.Name;
}

/// <summary>
/// <c>T?</c>. A nullable reference is still a reference to the same class, so it keeps the symbol; a
/// nullable value is a different type, which must not be mistaken for its struct or enum.
/// </summary>
public sealed class NullableType : WylType
{
    static readonly ConditionalWeakTable<WylType, NullableType> forms = new();

    NullableType(WylType nonNull) => NonNull = nonNull;

    internal static NullableType Of(WylType nonNull) => forms.GetValue(nonNull, static t => new(t));

    public override WylType NonNull { get; }
    public override TypeSymbol? Symbol => NonNull.IsReference ? NonNull.Symbol : null;
    public override IReadOnlyList<WylType> TypeArguments => NonNull.IsReference ? NonNull.TypeArguments : [];

    public override IReadOnlyList<WylType> Parts => [NonNull];
    public override WylType MapParts(Func<WylType, WylType> map) => map(NonNull).Nullable;
    private protected override string BuildName() => Grouped(NonNull) + "?";
}

/// <summary>A type whose values hold or point to elements, selected by index: a fixed array or a pointer.</summary>
public abstract class IndexedType : WylType
{
    private protected IndexedType(WylType element) => Element = element;

    public WylType Element { get; }

    public override IReadOnlyList<WylType> Parts => [Element];
}

/// <summary>A pointer <c>T*</c>, for unsafe code.</summary>
public sealed class PointerType : IndexedType
{
    static readonly ConditionalWeakTable<WylType, PointerType> pointers = new();

    PointerType(WylType element) : base(element) { }

    internal static PointerType To(WylType element) => pointers.GetValue(element, static e => new(e));

    public override WylType MapParts(Func<WylType, WylType> map) => map(Element).PointerTo;
    private protected override string BuildName() => Grouped(Element) + "*";
}

/// <summary>A fixed array <c>T[N]</c>, whose elements are stored inline.</summary>
public sealed class FixedArrayType : IndexedType
{
    static readonly ConditionalWeakTable<WylType, ConcurrentDictionary<int, FixedArrayType>> arrays = new();

    FixedArrayType(WylType element, int length) : base(element) => Length = length;

    internal static FixedArrayType Of(WylType element, int length) =>
        arrays.GetValue(element, static _ => new()).GetOrAdd(length, static (n, e) => new FixedArrayType(e, n), element);

    /// <summary>The <c>N</c> of <c>T[N]</c>.</summary>
    public int Length { get; }

    public override WylType MapParts(Func<WylType, WylType> map) => map(Element).FixedArrayOf(Length);

    // Like C, the outer dimension comes first: `int[4][2]` is four `int[2]`s.
    private protected override string BuildName() => Grouped(Innermost) + Dimensions;
    WylType Innermost => Element is FixedArrayType inner ? inner.Innermost : Element;
    string Dimensions => $"[{Length}]" + (Element is FixedArrayType inner ? inner.Dimensions : "");
}

/// <summary>
/// A function type <c>fn(A, ref B) -> R</c>. It has four forms: escaping or not, and shared or not.
/// The escaping one is a heap closure that may be stored, and the other may only be called and
/// passed on during the call it was given to.
/// </summary>
public sealed class FunctionType : WylType
{
    static readonly ConditionalWeakTable<WylType, ConcurrentDictionary<IReadOnlyList<FunctionParameterType>, Forms>> byReturnType = new();

    sealed class Forms(IReadOnlyList<FunctionParameterType> parameters, WylType returnType)
    {
        public IReadOnlyList<FunctionParameterType> Parameters { get; } = parameters;
        public WylType ReturnType { get; } = returnType;

        FunctionType? plain, escaping, shared, sharedEscaping, cFunction;

        /// <summary>The <c>extern fn</c> form, which has no other forms.</summary>
        public FunctionType Extern
        {
            get
            {
                if (Volatile.Read(ref cFunction) is { } existing)
                    return existing;
                var created = new FunctionType(this, isEscaping: true, isShared: false) { IsExtern = true };
                return Interlocked.CompareExchange(ref cFunction, created, null) ?? created;
            }
        }

        public FunctionType Get(bool isEscaping, bool isShared)
        {
            ref FunctionType? slot = ref plain;
            if (isEscaping && isShared)
                slot = ref sharedEscaping;
            else if (isEscaping)
                slot = ref escaping;
            else if (isShared)
                slot = ref shared;
            if (Volatile.Read(ref slot) is { } existing)
                return existing;
            var created = new FunctionType(this, isEscaping, isShared);
            return Interlocked.CompareExchange(ref slot, created, null) ?? created;
        }
    }

    readonly Forms forms;

    FunctionType(Forms forms, bool isEscaping, bool isShared)
    {
        this.forms = forms;
        IsEscaping = isEscaping;
        IsShared = isShared;
    }

    internal static FunctionType Of(IReadOnlyList<FunctionParameterType> parameters, WylType returnType) =>
        byReturnType.GetValue(returnType, static _ => new(FunctionParameterListComparer.Instance))
            .GetOrAdd(parameters, static (p, r) => new Forms([.. p], r), returnType)
            .Get(false, false);

    public IReadOnlyList<FunctionParameterType> Parameters => forms.Parameters;
    public WylType ReturnType => forms.ReturnType;
    public override bool IsEscaping { get; }
    public override bool IsShared { get; }

    /// <summary>
    /// Whether the type is an <c>extern fn</c>, a C function pointer (spec <c>interop.callbacks</c>).
    /// It captures nothing, so it is its own escaping and non-escaping form, and it is never shared.
    /// </summary>
    public bool IsExtern { get; private init; }

    public FunctionType Escaping => IsExtern ? this : forms.Get(true, IsShared);
    public FunctionType NonEscaping => IsExtern ? this : forms.Get(false, IsShared);
    public FunctionType Shared => IsExtern ? throw new InvalidOperationException("an extern fn has no shared form") : forms.Get(IsEscaping, true);
    public FunctionType NonShared => IsExtern ? this : forms.Get(IsEscaping, false);
    public FunctionType WithForms(bool escaping, bool shared) => IsExtern ? this : forms.Get(escaping, shared);

    /// <summary>The <c>extern fn</c> type with these parameters and return type.</summary>
    public FunctionType Extern => forms.Extern;

    public override IReadOnlyList<WylType> Parts => [.. Parameters.Select(p => p.Type), ReturnType];

    public override WylType MapParts(Func<WylType, WylType> map)
    {
        var mapped = map(ReturnType).FunctionReturning([.. Parameters.Select(p => p with { Type = map(p.Type) })]);
        return IsExtern ? mapped.Extern : mapped.WithForms(IsEscaping, IsShared);
    }

    private protected override string BuildName() => $"{(IsShared ? "shared " : IsExtern ? "extern " : "")}fn({string.Join(", ", Parameters)}) -> {ReturnType}";
}

/// <summary>
/// <c>ref? T</c> or <c>in? T</c>, a reference that may refer to nothing. Such a type is only ever
/// that of a <c>ref</c> or <c>in</c> local, parameter, or return, and of the expressions reading
/// one; it is not nullable in the sense of <see cref="WylType.IsNullable"/>, so no operation on
/// values accepts it until a null check narrows it to <c>T</c>.
/// </summary>
public sealed class NullableReferenceType : WylType
{
    static readonly ConditionalWeakTable<WylType, NullableReferenceType> refs = new(), ins = new();

    NullableReferenceType(WylType referenced, RefKind kind)
    {
        Referenced = referenced;
        Kind = kind;
    }

    internal static NullableReferenceType To(WylType referenced, RefKind kind) => kind == RefKind.In
        ? ins.GetValue(referenced, static t => new(t, RefKind.In))
        : refs.GetValue(referenced, static t => new(t, RefKind.Ref));

    public WylType Referenced { get; }

    /// <summary>Whether a <c>ref?</c> or an <c>in?</c>.</summary>
    public RefKind Kind { get; }

    public override IReadOnlyList<WylType> Parts => [Referenced];
    public override WylType MapParts(Func<WylType, WylType> map) => map(Referenced).NullableReference(Kind);
    private protected override string BuildName() => $"{FunctionSymbol.RefPrefix(Kind).Trim()}? {Referenced}";
}
