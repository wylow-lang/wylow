using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Ir;

/// <summary>
/// <c>x proj*</c> or <c>*x proj*</c>: a local's storage, or with <see cref="IsDeref"/>, what the local
/// refers to (a reference's place, an object or a hold's object, or a pointer's target), then
/// projections that stay in that storage. Only the base leaves the current storage, so the object
/// a reference into a place must hold is the base. Evaluating a place runs no code and never panics.
/// </summary>
public sealed class IrPlace
{
    public IrPlace(IrLocal local, bool isDeref = false, IReadOnlyList<IrProjection>? projections = null)
    {
        Local = local;
        IsDeref = isDeref;
        Projections = projections ?? [];
    }

    /// <summary>Settable so that a pass can name a local by what it is an alias of.</summary>
    public IrLocal Local { get; set; }

    public bool IsDeref { get; }
    public IReadOnlyList<IrProjection> Projections { get; }

    /// <summary>The value's type, or <see cref="WylType.Error"/> where a projection doesn't apply, which the verifier reports.</summary>
    public WylType Type => Projections.Count > 0 ? Projections[^1].Type : BaseType;

    public WylType BaseType => !IsDeref ? Local.Type : Local.Deref switch
    {
        IrDeref.Referenced => Local.Type.Referred,
        IrDeref.Pointee => ((PointerType)Local.Type).Element,
        _ => Local.Type.NonNull,
    };

    public static IrPlace Deref(IrLocal local) => new(local, isDeref: true);

    /// <summary>Whether the place is the first element a dereferenced pointer points to, which an element projection then indexes from.</summary>
    bool IsPointerTarget => IsDeref && Projections.Count == 0 && Local.Deref == IrDeref.Pointee;

    public IrPlace Field(FieldSymbol field) => With(new IrField(field, FieldType(Type, field)));

    public IrPlace Element(IrOperand index) =>
        With(new IrElement(index, IsPointerTarget ? Type : Type is FixedArrayType array ? array.Element : WylType.Error));

    public IrPlace VariantField(VariantSymbol variant, int index) =>
        With(new IrVariantField(variant, variant.Fields[index], Type.Symbol == variant.Owner ? TypeMap.For(Type).Apply(variant.Fields[index].Type) : WylType.Error));

    public IrPlace Assumed() => With(new IrAssumed(Type.IsNullableValue ? Type.NonNull : WylType.Error));

    IrPlace With(IrProjection projection) => new(Local, IsDeref, [.. Projections, projection]);

    /// <summary>A field's type in a value of <paramref name="holder"/>, which may inherit it; <see cref="WylType.Error"/> if it has no such field.</summary>
    public static WylType FieldType(WylType holder, FieldSymbol field)
    {
        IEnumerable<WylType> candidates = [holder.NonNull, .. Conversions.Supertypes(holder.NonNull)];
        return candidates.FirstOrDefault(t => t.Symbol == field.Owner) is { } owner ? TypeMap.For(owner).Apply(field.Type) : WylType.Error;
    }
}

public abstract class IrProjection(WylType type)
{
    /// <summary>The type of the place the projection leads to.</summary>
    public WylType Type { get; } = type;
}

/// <summary><c>.f</c>: a field of a struct, or, right after the base, of the object it refers to.</summary>
public sealed class IrField(FieldSymbol field, WylType type) : IrProjection(type)
{
    public FieldSymbol Field { get; } = field;
}

/// <summary>
/// <c>[i]</c>: an element of a fixed array, or right after a pointer's dereference, of what it
/// points into. Unchecked: a <see cref="IrCheck"/> precedes it. The index is a constant or a local.
/// </summary>
public sealed class IrElement(IrOperand index, WylType type) : IrProjection(type)
{
    public IrOperand Index { get; } = index;
}

/// <summary><c>.Variant.f</c>: a field of an enum value known to be that variant.</summary>
public sealed class IrVariantField(VariantSymbol variant, FieldSymbol field, WylType type) : IrProjection(type)
{
    public VariantSymbol Variant { get; } = variant;
    public FieldSymbol Field { get; } = field;
}

/// <summary><c>!</c>: the value of a nullable value known not to be null.</summary>
public sealed class IrAssumed(WylType type) : IrProjection(type);

/// <summary>What a call is given for a receiver or parameter.</summary>
public abstract class IrArgument;

/// <summary>A value: a constant, or a read of a place.</summary>
public abstract class IrOperand : IrArgument
{
    public abstract WylType Type { get; }
}

/// <summary>A constant; <see cref="Value"/> is as for a <see cref="BoundLiteral"/>, or null for <c>null</c>.</summary>
public sealed class IrConstant(WylType type, object? value) : IrOperand
{
    public object? Value { get; } = value;
    public override WylType Type { get; } = type;

    public static IrConstant Int(int value) => new(WylType.Int, value);
    public static IrConstant Bool(bool value) => new(WylType.Bool, value);
}

public enum IrOwnership
{
    /// <summary>A copy of the value, which takes its own references (+1).</summary>
    Copy,
    /// <summary>A value taken from a place that isn't used again, a whole local's or parameter's for a unique type: its references are taken over.</summary>
    Move,
    /// <summary>The value in place (+0), valid only while nothing writes the place.</summary>
    Borrow,
}

/// <summary><c>copy P</c>, <c>move P</c>, <c>borrow P</c>, or <c>borrow.element P</c>.</summary>
public sealed class IrRead(IrOwnership ownership, IrPlace place) : IrOperand
{
    public IrOwnership Ownership { get; set; } = ownership;
    public IrPlace Place { get; set; } = place;
    public override WylType Type => Place.Type;

    /// <summary>
    /// A borrow of an element of what a loop iterates, which is borrowed while the loop runs, so
    /// only a write to an element of its type, or to a part of one, may change what it borrows
    /// (<see cref="IrAccess.Element"/>); other writes to the place may be allowed. The verifier
    /// checks that nothing does, by the whole-program write summaries.
    /// </summary>
    public bool Element { get; private init; }

    public static IrRead Copy(IrPlace place) => new(IrOwnership.Copy, place);
    public static IrRead Move(IrPlace place) => new(IrOwnership.Move, place);
    public static IrRead Borrow(IrPlace place) => new(IrOwnership.Borrow, place);
    public static IrRead BorrowElement(IrPlace place) => new(IrOwnership.Borrow, place) { Element = true };
}

/// <summary><c>ref P</c> or <c>in P</c>: a place passed by reference, or a struct receiver.</summary>
public sealed class IrRefArgument(RefKind kind, IrPlace place) : IrArgument
{
    public RefKind Kind { get; } = kind;
    public IrPlace Place { get; } = place;
}

/// <summary><c>addr P</c>: a place passed to a <c>ref?</c> or <c>in?</c> parameter.</summary>
public sealed class IrAddrArgument(RefKind kind, IrPlace place) : IrArgument
{
    public RefKind Kind { get; } = kind;
    public IrPlace Place { get; } = place;
}

/// <summary>A field a memberwise constructor call leaves out, which the constructor defaults.</summary>
public sealed class IrFieldDefault(FieldSymbol field) : IrArgument
{
    public FieldSymbol Field { get; } = field;
}

/// <summary>A visitor of every rvalue kind, so that adding a kind breaks every visitor until it handles it.</summary>
public interface IIrRvalueVisitor<out T>
{
    T Use(IrUse value);
    T Unary(IrUnary value);
    T Binary(IrBinary value);
    T Call(IrCall value);
    T Invoke(IrInvoke value);
    T New(IrNew value);
    T Construct(IrConstruct value);
    T Variant(IrVariant value);
    T Array(IrArray value);
    T Default(IrDefault value);
    T Zeroed(IrZeroed value);
    T Closure(IrClosure value);
    T Lambda(IrLambda value);
    T Choose(IrChoose value);
    T Ref(IrRef value);
    T Addr(IrAddr value);
    T Data(IrData value);
    T Test(IrTest value);
    T Convert(IrConvert value);
    T Hold(IrHold value);
    T InUse(IrInUse value);
}

public abstract class IrRvalue
{
    public abstract T Accept<T>(IIrRvalueVisitor<T> visitor);
    public abstract WylType Type { get; }

    /// <summary>The location an operation panics at if it may, which it must then be given, or null if it can't.</summary>
    protected static IrLoc? PanicsAt(bool mayPanic, IrLoc? loc) =>
        mayPanic ? loc ?? throw new ArgumentException("an operation that may panic needs a location") : null;
}

public sealed class IrUse(IrOperand operand) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.Use(this);
    public IrOperand Operand { get; set; } = operand;
    public override WylType Type => Operand.Type;
}

/// <summary>An operator; an integer negation carries the location it panics at on overflow.</summary>
public sealed class IrUnary(BoundUnaryOperator op, IrOperand operand, IrLoc? loc = null) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.Unary(this);
    public BoundUnaryOperator Operator { get; } = op;
    public IrOperand Operand { get; set; } = operand;

    /// <summary>Where it panics: integer negation overflows.</summary>
    public IrLoc? Loc { get; } = PanicsAt(op == BoundUnaryOperator.Negate && operand.Type.IsInteger, loc);

    public override WylType Type => Operand.Type;
}

/// <summary>An operator; integer arithmetic carries the location it panics at.</summary>
public sealed class IrBinary(BoundBinaryOperator op, IrOperand left, IrOperand right, WylType type, IrLoc? loc = null) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.Binary(this);
    public BoundBinaryOperator Operator { get; } = op;
    public IrOperand Left { get; set; } = left;
    public IrOperand Right { get; set; } = right;

    /// <summary>Where it panics: integer arithmetic overflows or divides by zero.</summary>
    public IrLoc? Loc { get; } = PanicsAt(left.Type.IsInteger && op is BoundBinaryOperator.Add or BoundBinaryOperator.Subtract
        or BoundBinaryOperator.Multiply or BoundBinaryOperator.Divide or BoundBinaryOperator.Remainder, loc);

    public override WylType Type { get; } = type;
}

/// <summary>
/// A call of a function or method. A method's receiver is an object operand, a struct place
/// passed by <c>ref</c> or <c>in</c>, or a span. <see cref="Loc"/> is passed to an
/// <c>@CallerLocation</c> callee.
/// </summary>
public sealed class IrCall(MethodReference method, IrArgument? receiver, IReadOnlyList<IrArgument> arguments, IrLoc? loc = null) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.Call(this);
    public MethodReference Method { get; } = method;
    public IrArgument? Receiver { get; set; } = receiver;
    public List<IrArgument> Arguments { get; } = [.. arguments];
    public IrLoc? Loc { get; } = loc;

    /// <summary>Calls the base class's implementation without dispatch, or a constructor on the object being made.</summary>
    public bool IsSuperCall { get; init; }

    public override WylType Type => Method.ReturnType;
}

/// <summary>A call of a function value, whose parameters <see cref="Signature"/> describes as a method's.</summary>
public sealed class IrInvoke(IrOperand callee, MethodReference signature, IReadOnlyList<IrArgument> arguments) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.Invoke(this);
    public IrOperand Callee { get; set; } = callee;
    public MethodReference Signature { get; } = signature;
    public List<IrArgument> Arguments { get; } = [.. arguments];
    public override WylType Type => Signature.ReturnType;
}

/// <summary><c>new C(args)</c>, on the heap, or in <see cref="Slot"/>.</summary>
public sealed class IrNew(WylType type, MethodReference constructor, IReadOnlyList<IrArgument> arguments, IrLoc? loc = null) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.New(this);
    public MethodReference Constructor { get; } = constructor;
    public List<IrArgument> Arguments { get; } = [.. arguments];
    public IrLoc? Loc { get; } = loc;
    public IrLocal? Slot { get; set; }

    /// <summary>Where it is in the source, which tests mark when it is put in a slot.</summary>
    public required Location Origin { get; init; }

    public override WylType Type { get; } = type;
}

/// <summary>A struct value made by one of its constructors.</summary>
public sealed class IrConstruct(WylType type, MethodReference constructor, IReadOnlyList<IrArgument> arguments, IrLoc? loc = null) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.Construct(this);
    public MethodReference Constructor { get; } = constructor;
    public List<IrArgument> Arguments { get; } = [.. arguments];
    public IrLoc? Loc { get; } = loc;
    public override WylType Type { get; } = type;
}

public sealed class IrVariant(WylType type, VariantSymbol variant, IReadOnlyList<IrOperand> fields) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.Variant(this);
    public VariantSymbol Variant { get; } = variant;
    public List<IrOperand> Fields { get; } = [.. fields];
    public override WylType Type { get; } = type;
}

/// <summary>A fixed array literal.</summary>
public sealed class IrArray(WylType type, IReadOnlyList<IrOperand> elements) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.Array(this);
    public List<IrOperand> Elements { get; } = [.. elements];
    public override WylType Type { get; } = type;
}

public sealed class IrDefault(WylType type) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.Default(this);
    public override WylType Type { get; } = type;
}

/// <summary>
/// A struct's storage zeroed, which only a <see cref="IrFunction.Builds"/> function's value starts
/// as: a field's zero is its default value only if its type has no default code (<c>HasDefaultCode</c>).
/// </summary>
public sealed class IrZeroed(WylType type) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.Zeroed(this);
    public override WylType Type { get; } = type;
}

/// <summary>An escaping lambda's closure, holding copies of what it captures, on the heap or in <see cref="Slot"/>.</summary>
public sealed class IrClosure(IrFunction function, IReadOnlyList<IrOperand> captures, WylType type) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.Closure(this);
    public IrFunction Function { get; } = function;
    public List<IrOperand> Captures { get; } = [.. captures];
    public IrLocal? Slot { get; set; }

    /// <summary>The name diagnostics give the closure's object, as a class's name for its objects.</summary>
    public required string Name { get; init; }

    /// <summary>Where its lambda is in the source, which tests mark when it is put in a slot.</summary>
    public required Location Origin { get; init; }

    public override WylType Type { get; } = type;
}

/// <summary>A non-escaping lambda, whose body uses the locals around it as places. Making it does nothing.</summary>
public sealed class IrLambda(IrFunction function, WylType type) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.Lambda(this);
    public IrFunction Function { get; } = function;
    public override WylType Type { get; } = type;
}

/// <summary>The option of a choice of non-escaping functions that <see cref="Index"/> selects.</summary>
public sealed class IrChoose(IrOperand index, IReadOnlyList<IrOperand> options, WylType type) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.Choose(this);
    public IrOperand Index { get; set; } = index;
    public List<IrOperand> Options { get; } = [.. options];
    public override WylType Type { get; } = type;
}

/// <summary><c>ref P</c> or <c>in P</c>: a reference to a place, for a reference local.</summary>
public sealed class IrRef(RefKind kind, IrPlace place) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.Ref(this);
    public RefKind Kind { get; } = kind;
    public IrPlace Place { get; } = place;
    public override WylType Type => Place.Type;
}

/// <summary><c>addr P</c>: a <c>ref?</c> or <c>in?</c> to a place.</summary>
public sealed class IrAddr(RefKind kind, IrPlace place) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.Addr(this);
    public RefKind Kind { get; } = kind;
    public IrPlace Place { get; } = place;
    public override WylType Type => Place.Type.NullableReference(Kind);
}

/// <summary><c>data P</c>: a pointer to a fixed array's first element, which a span views.</summary>
public sealed class IrData(IrPlace place) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.Data(this);
    public IrPlace Place { get; } = place;
    public override WylType Type => (Place.Type as FixedArrayType)?.Element.PointerTo ?? WylType.Error;
}

public enum IrTestKind
{
    /// <summary>A <c>ref?</c>, <c>in?</c>, or nullable reference isn't null.</summary>
    NotNull,
    /// <summary>A nullable value isn't null.</summary>
    HasValue,
    /// <summary>An enum value is the variant.</summary>
    Tag,
    /// <summary>A non-null reference refers to an object of the type.</summary>
    Class,
}

public sealed class IrTest(IrTestKind kind, IrOperand operand) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.Test(this);
    public IrTestKind Kind { get; } = kind;
    public IrOperand Operand { get; set; } = operand;
    public VariantSymbol? Variant { get; init; }
    public WylType? Target { get; init; }
    public override WylType Type => WylType.Bool;
}

/// <summary>A conversion, which carries a location if it may panic.</summary>
public sealed class IrConvert(WylType type, IrOperand operand, IrLoc? loc = null) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.Convert(this);
    public IrOperand Operand { get; set; } = operand;

    /// <summary>Where it panics: an integer that is no enum value or <c>char</c>.</summary>
    public IrLoc? Loc { get; } = PanicsAt(type.IsEnum && !operand.Type.IsEnum || operand.Type.IsInteger && type == WylType.Char, loc);

    public override WylType Type { get; } = type;
}

/// <summary>
/// <c>hold P</c> pins the object at <c>P</c> until the hold ends; <c>hold.borrow P</c> also borrows it,
/// so that a <c>Borrowable</c> keeps the storage it hands out in place.
/// </summary>
public sealed class IrHold(IrPlace place, bool borrows) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.Hold(this);
    public IrPlace Place { get; } = place;
    public bool Borrows { get; set; } = borrows;
    public override WylType Type => Place.Type.NonNull;
}

/// <summary>
/// <c>inuse P</c>: while the local that holds it lives, the value at <c>P</c> is in use, and the
/// state it had is restored when the local ends (spec <c>memory.holds</c>). Its value is the state
/// it saved, which nothing reads.
/// </summary>
public sealed class IrInUse(IrPlace place) : IrRvalue
{
    public override T Accept<T>(IIrRvalueVisitor<T> visitor) => visitor.InUse(this);
    public IrPlace Place { get; } = place;
    public override WylType Type => WylType.Bool;
}
