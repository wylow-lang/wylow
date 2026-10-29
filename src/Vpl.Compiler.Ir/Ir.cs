using System.Collections;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Ir;

// The mid-level IR: structured control flow, flat instructions over single-assignment temporaries,
// places as a base and projections, and ownership explicit on every read. See docs/design/ir.md.
// Nodes are mutable classes compared by reference: passes rewrite them in place, and side tables
// key on them.

/// <summary>Every function, including every lambda and the standard library's, over the bound program's symbols.</summary>
public sealed class IrProgram(BoundProgram bound)
{
    public BoundProgram Bound { get; } = bound;
    public IrFunctions Functions { get; } = new();

    /// <summary>
    /// How many times the pipeline has changed the program, so that facts found from it can tell
    /// when they describe an earlier program.
    /// </summary>
    public int Generation { get; private set; }

    public void Changed() => Generation++;
}

/// <summary>
/// A program's lowered functions in the order they were lowered, with the index of what an
/// instruction names. A memberwise constructor is one function per form a call uses, so its symbol
/// names several; a struct's <c>make_default</c> is under a symbol no call names, found by its type.
/// Adding a function rebuilds the index, so it can't describe an earlier program.
/// </summary>
public sealed class IrFunctions : IReadOnlyList<IrFunction>
{
    static readonly IReadOnlyList<IrFunction> None = [];

    readonly List<IrFunction> functions = [];
    Dictionary<FunctionSymbol, List<IrFunction>>? bySymbol;
    Dictionary<TypeSymbol, IrFunction>? defaults;

    public int Count => functions.Count;
    public IrFunction this[int index] => functions[index];

    public void Add(IrFunction function)
    {
        functions.Add(function);
        bySymbol = null;
        defaults = null;
    }

    public void AddRange(IEnumerable<IrFunction> added)
    {
        functions.AddRange(added);
        bySymbol = null;
        defaults = null;
    }

    /// <summary>The functions under the symbol: one, or a memberwise constructor's forms.</summary>
    public IReadOnlyList<IrFunction> Of(FunctionSymbol symbol) =>
        (bySymbol ??= functions.GroupBy(f => f.Symbol).ToDictionary(g => g.Key, g => g.ToList())).TryGetValue(symbol, out var found) ? found : None;

    /// <summary>Whether any function is under the symbol.</summary>
    public bool Contains(FunctionSymbol symbol) => Of(symbol).Count > 0;

    /// <summary>The one function under the symbol, which every symbol but a memberwise constructor's names.</summary>
    public IrFunction? Single(FunctionSymbol symbol) => Of(symbol) is [var one] ? one : null;

    /// <summary>A memberwise constructor's forms, one function each.</summary>
    public IReadOnlyList<IrFunction> MemberwiseForms(FunctionSymbol constructor) =>
        [.. Of(constructor).Where(f => f.Builds is not null)];

    /// <summary>The form of a memberwise constructor that passes the fields of <paramref name="form"/>.</summary>
    public IrFunction? Memberwise(FunctionSymbol constructor, IReadOnlyList<bool> form) =>
        Of(constructor).FirstOrDefault(f => f.Builds is { } builds && builds.SequenceEqual(form));

    /// <summary>The function making a struct's default value, from the symbol no call names.</summary>
    public IrFunction? Default(TypeSymbol type) => DefaultsByType.GetValueOrDefault(type);

    /// <summary>Every struct's <c>make_default</c>, which a type parameter's default may be.</summary>
    public IReadOnlyList<IrFunction> Defaults => [.. DefaultsByType.Values];

    Dictionary<TypeSymbol, IrFunction> DefaultsByType => defaults ??= functions
        .Where(f => f is { Builds: not null, Symbol.IsMemberwise: false, Symbol.ContainingType: not null })
        .ToDictionary(f => f.Symbol.ContainingType!, f => f);

    public IEnumerator<IrFunction> GetEnumerator() => functions.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class IrFunction(FunctionSymbol symbol, string name)
{
    public FunctionSymbol Symbol { get; } = symbol;
    public string Name { get; } = name;

    /// <summary>A method's <c>this</c>: a struct's storage, or a class's object.</summary>
    public IrLocal? This { get; set; }

    public List<IrLocal> Parameters { get; } = [];

    /// <summary>
    /// A struct constructor's value under construction, which its <c>this</c> names and its
    /// returns return. The constructor has no <see cref="This"/>.
    /// </summary>
    public IrLocal? Struct { get; set; }

    /// <summary>
    /// For a struct's <c>make_default</c> or one form of its memberwise constructor, which fields
    /// it is passed: it builds its <see cref="Struct"/> from zeroed storage, storing each field its
    /// argument, or its initializer or its type's default value.
    /// </summary>
    public IReadOnlyList<bool>? Builds { get; set; }

    /// <summary>
    /// A class constructor's call of its base class's, the first statement of its body. C++ runs
    /// nothing before a base constructor, so each argument that needs statements is declared, then
    /// computed by a block of its own, and the call is the block's last statement.
    /// </summary>
    public IrBlock? BaseCall { get; set; }

    /// <summary>An <c>@CallerLocation</c> function's hidden parameter, which <see cref="IrLoc.Caller"/> names.</summary>
    public bool HasLocationParameter { get; set; }

    /// <summary>
    /// An escaping lambda's copies of what it captures, which the <see cref="IrClosure"/> making it
    /// passes in order. A non-escaping lambda has none: its body uses its creator's locals directly.
    /// </summary>
    public List<IrLocal> Captures { get; } = [];

    /// <summary>For a lambda, the function whose body creates it.</summary>
    public IrFunction? Enclosing { get; init; }

    /// <summary>The body, whose origin is the source body, if the source writes one.</summary>
    public IrBlock Body { get; set; } = new() { Origin = null };

    public bool IsLambda => Symbol.IsLambda;
    public bool IsEscaping => Symbol.IsEscaping;

    public override string ToString() => Name;
}

/// <summary>How a local is declared in C++.</summary>
public enum IrLocalKind
{
    /// <summary><c>T x</c>.</summary>
    Value,
    /// <summary><c>T&amp; x</c> for <c>ref</c> and <c>in</c>, <c>T* x</c> for <c>ref?</c> and <c>in?</c>.</summary>
    Reference,
    /// <summary><c>const T&amp; x</c>: an alias of a place that nothing writes while it lives.</summary>
    Borrowed,
    /// <summary>A strong reference that pins an object, and borrows it if made by <c>hold.borrow</c>.</summary>
    Hold,
    /// <summary><c>vpl::StackSlot&lt;T&gt;</c>, which a <c>new</c> or closure puts its object in.</summary>
    Slot,
}

/// <summary>What dereferencing a local reaches.</summary>
public enum IrDeref
{
    /// <summary>An object, whose field a place's first projection may be.</summary>
    Object,
    /// <summary>The place a reference refers to.</summary>
    Referenced,
    /// <summary>What a pointer points to, which an element projection indexes from.</summary>
    Pointee,
    /// <summary>A hold's generic value: an object, or for a struct, the hold's own copy.</summary>
    Generic,
}

/// <summary>What a local is in its function, which no pass changes.</summary>
public enum IrLocalRole
{
    /// <summary>A variable: a source local, or one lowering declares, which may be stored any number of times.</summary>
    Variable,
    /// <summary>
    /// Defined once, by a <see cref="IrLet"/>, or for a value that comes out of control flow,
    /// declared by a <see cref="IrDeclare"/> and stored exactly once on every path.
    /// </summary>
    Temporary,
    Parameter,
    /// <summary>A method's <c>this</c>: a class's object, or a struct method's caller's storage.</summary>
    This,
    /// <summary>An escaping lambda's copy of a local, or of <c>this</c> if it has no <see cref="IrLocal.Symbol"/>.</summary>
    Capture,
}

/// <summary>A local, parameter, or temporary.</summary>
public sealed class IrLocal(string name, VplType type, IrLocalKind kind, IrLocalRole role)
{
    public string Name { get; } = name;

    /// <summary>
    /// As for a <see cref="LocalSymbol"/>: what a <c>ref T</c> refers to is <c>T</c>, with
    /// <see cref="RefKind"/>; a <c>ref? T</c> has that type. A hold's or slot's is the object's.
    /// </summary>
    public VplType Type { get; set; } = type;

    /// <summary>Settable so that a pass can turn a hold into a borrow, which keeps every place naming the local.</summary>
    public IrLocalKind Kind { get; set; } = kind;

    /// <summary>What dereferencing it reaches, or null if it can't be dereferenced. A slot is only ever made into.</summary>
    public IrDeref? Deref => Kind switch
    {
        IrLocalKind.Reference => IrDeref.Referenced,
        IrLocalKind.Slot => null,
        _ when Type.NonNull.IsReference => IrDeref.Object,
        IrLocalKind.Hold when Type.IsTypeParameter => IrDeref.Generic,
        IrLocalKind.Value or IrLocalKind.Borrowed when Type.IsPointer => IrDeref.Pointee,
        _ => null,
    };

    public RefKind RefKind { get; init; }
    public IrLocalRole Role { get; } = role;
    public bool IsTemporary => Role == IrLocalRole.Temporary;
    public bool IsThis => Role == IrLocalRole.This;

    /// <summary>The source local it is, if any.</summary>
    public LocalSymbol? Symbol { get; init; }

    /// <summary>Where it is declared, if it is the variable a for-each loop copies each element into.</summary>
    public Location? LoopElement => Role == IrLocalRole.Variable ? Symbol?.LoopElement : null;

    public override string ToString() => Name;
}

/// <summary>A source location for a panic, or the location an <c>@CallerLocation</c> function was called from.</summary>
public sealed record IrLoc(Location? Source)
{
    public static readonly IrLoc Caller = new((Location?)null);
}

/// <summary>A visitor of every statement kind, so that adding a kind breaks every visitor until it handles it.</summary>
public interface IIrStatementVisitor<out T>
{
    T Block(IrBlock statement);
    T Let(IrLet statement);
    T Declare(IrDeclare statement);
    T Store(IrStore statement);
    T Do(IrDo statement);
    T Check(IrCheck statement);
    T If(IrIf statement);
    T Loop(IrLoop statement);
    T Break(IrBreak statement);
    T Continue(IrContinue statement);
    T Return(IrReturn statement);
    T End(IrEnd statement);
    T Unreachable(IrUnreachable statement);
}

public abstract class IrStatement
{
    public abstract T Accept<T>(IIrStatementVisitor<T> visitor);

    /// <summary>
    /// The source expression or statement it was lowered from, for the errors only the IR can find,
    /// or null for what the source doesn't write, like a function's implicit return. It is required
    /// so that no way of making a statement can leave it out.
    /// </summary>
    public required Location? Origin { get; init; }
}

public sealed class IrBlock : IrStatement
{
    public override T Accept<T>(IIrStatementVisitor<T> visitor) => visitor.Block(this);

    public List<IrStatement> Statements { get; } = [];

    /// <summary>
    /// A block holding only temporaries: one statement's, which are released when it completes
    /// (spec §3.2, When references are released), or a switch expression's, which are its
    /// statement's. <c>HoldElision</c> flattens one where releasing none of them may be observed
    /// (<see cref="DeclaresOwner"/>).
    /// </summary>
    public bool IsStatement { get; init; }

    /// <summary>Whether it declares a local, other than those in <paramref name="except"/>, whose release may run code.</summary>
    public bool DeclaresOwner(params IrLocal?[] except) => IrWalk.Declared(this).Any(l => !except.Contains(l) && IrEffects.ReleaseMayRunCode(l));

    public IrBlock() { }

    public IrBlock(IEnumerable<IrStatement> statements) => Statements.AddRange(statements);
}

/// <summary><c>let x = rvalue</c>: declares and initializes a local in the enclosing block.</summary>
public sealed class IrLet : IrStatement
{
    public override T Accept<T>(IIrStatementVisitor<T> visitor) => visitor.Let(this);

    public IrLocal Local { get; }
    public IrRvalue Value { get; set; }

    /// <summary>Defines a temporary of the kind the value makes.</summary>
    public IrLet(IrRvalue value)
    {
        var (kind, refKind) = Defines(value);
        Local = new IrLocal("t", value.Type, kind, IrLocalRole.Temporary) { RefKind = refKind };
        Value = value;
    }

    public IrLet(IrLocal local, IrRvalue value)
    {
        if (Mismatch(local, value, local.Name) is { } mismatch)
            throw new InvalidOperationException(mismatch);
        Local = local;
        Value = value;
    }

    /// <summary>
    /// The kind of local the value defines, and for a reference, the kind of reference. Every
    /// <c>let</c> is made by this rule, and the verifier checks each against it after every pass.
    /// The kind of reference a <c>ref?</c> or <c>in?</c> is is its type's.
    /// </summary>
    public static (IrLocalKind Kind, RefKind RefKind) Defines(IrRvalue value)
    {
        var kind = value switch
        {
            IrHold => IrLocalKind.Hold,
            IrUse { Operand: IrRead { Ownership: IrOwnership.Borrow } } => IrLocalKind.Borrowed,
            IrRef or IrAddr => IrLocalKind.Reference,
            IrCall { Method.Symbol.ReturnRefKind: not RefKind.None } => IrLocalKind.Reference,
            IrConvert { Type.IsNullableReference: true } => IrLocalKind.Reference,
            IrUse { Operand: IrRead { Place: { IsDeref: false, Projections.Count: 0, Local.Kind: IrLocalKind.Reference } } } => IrLocalKind.Reference,
            _ => IrLocalKind.Value,
        };
        if (kind != IrLocalKind.Reference)
            return (kind, RefKind.None);
        var refKind = value switch
        {
            _ when value.Type is NullableReferenceType nullable => nullable.Kind,
            IrRef reference => reference.Kind,
            IrCall call => call.Method.Symbol.ReturnRefKind,
            IrUse { Operand: IrRead read } => read.Place.Local.RefKind,
            _ => RefKind.None,
        };
        return (kind, refKind);
    }

    /// <summary>Why the local can't be defined by the value, if it can't.</summary>
    public static string? Mismatch(IrLocal local, IrRvalue value, string name)
    {
        var (kind, refKind) = Defines(value);
        if (local.Kind != kind)
            return $"a {local.Kind.ToString().ToLowerInvariant()} local is defined by what makes a {kind.ToString().ToLowerInvariant()} one";
        if (local.RefKind != refKind)
            return $"'{name}' is a '{FunctionSymbol.RefPrefix(local.RefKind).Trim()}' reference, but is defined with a '{FunctionSymbol.RefPrefix(refKind).Trim()}' one";
        return null;
    }
}

/// <summary>
/// <c>declare x</c>: declares a local without a value. It must be stored before it is read; a slot
/// needs no value. A hold declared this way is made by later <c>let</c>s, each letting go of what
/// it held before, so that it outlives the statement making it, as an <c>is ref</c> name's does.
/// </summary>
public sealed class IrDeclare(IrLocal local) : IrStatement
{
    public override T Accept<T>(IIrStatementVisitor<T> visitor) => visitor.Declare(this);

    public IrLocal Local { get; } = local;
}

/// <summary><c>store place = value</c>: stores the new value, then releases the old one.</summary>
public sealed class IrStore(IrPlace target, IrOperand value) : IrStatement
{
    public override T Accept<T>(IIrStatementVisitor<T> visitor) => visitor.Store(this);

    public IrPlace Target { get; } = target;
    public IrOperand Value { get; set; } = value;
}

/// <summary><c>do rvalue</c>: evaluates for its effects.</summary>
public sealed class IrDo(IrRvalue value) : IrStatement
{
    public override T Accept<T>(IIrStatementVisitor<T> visitor) => visitor.Do(this);

    public IrRvalue Value { get; set; } = value;
}

public enum IrCheckKind
{
    /// <summary>Panics unless <c>0 &lt;= index &lt; length</c>; its operands are the index and the length.</summary>
    Index,
    /// <summary>Panics if a <c>ref?</c>, <c>in?</c>, or nullable reference is null.</summary>
    NotNull,
    /// <summary>Panics if a nullable value is null.</summary>
    HasValue,
    /// <summary>Panics: no case of a switch matched. It has no operands.</summary>
    NoMatch,
    /// <summary>
    /// A source local the code after it uses as not null, as narrowing allows: the flow analysis
    /// proves it can't be null, reporting the user's error if it can. It never runs, so it has no location.
    /// </summary>
    Narrowed,
}

/// <summary><c>check.kind operands @loc</c>: panics unless the condition holds.</summary>
public sealed class IrCheck(IrCheckKind kind, IReadOnlyList<IrOperand> operands, IrLoc? loc) : IrStatement
{
    public override T Accept<T>(IIrStatementVisitor<T> visitor) => visitor.Check(this);

    public IrCheckKind Kind { get; } = kind;
    public List<IrOperand> Operands { get; } = [.. operands];

    /// <summary>Where it panics; null only for a <see cref="IrCheckKind.Narrowed"/> check.</summary>
    public IrLoc? Loc { get; } = loc;
}

public sealed class IrIf(IrOperand condition, IrBlock then, IrBlock? @else = null) : IrStatement
{
    public override T Accept<T>(IIrStatementVisitor<T> visitor) => visitor.If(this);

    public IrOperand Condition { get; set; } = condition;
    public IrBlock Then { get; } = then;
    public IrBlock? Else { get; set; } = @else;
}

/// <summary>
/// Runs <see cref="Body"/>, then <see cref="Continue"/>, until a <see cref="IrBreak"/>. A
/// <see cref="IrContinue"/> in the body goes to <see cref="Continue"/>.
/// </summary>
public sealed class IrLoop : IrStatement
{
    public override T Accept<T>(IIrStatementVisitor<T> visitor) => visitor.Loop(this);

    public IrBlock Body { get; set; } = new() { Origin = null };
    public IrBlock? Continue { get; set; }
}

public sealed class IrBreak(IrLoop loop) : IrStatement
{
    public override T Accept<T>(IIrStatementVisitor<T> visitor) => visitor.Break(this);

    public IrLoop Loop { get; } = loop;
}

public sealed class IrContinue(IrLoop loop) : IrStatement
{
    public override T Accept<T>(IIrStatementVisitor<T> visitor) => visitor.Continue(this);

    public IrLoop Loop { get; } = loop;
}

public sealed class IrReturn(IrOperand? value = null) : IrStatement
{
    public override T Accept<T>(IIrStatementVisitor<T> visitor) => visitor.Return(this);

    public IrOperand? Value { get; set; } = value;
}

/// <summary><c>end x</c>: ends a hold or a slot before its scope does. Ending an empty or ended slot does nothing.</summary>
public sealed class IrEnd(IrLocal local) : IrStatement
{
    public override T Accept<T>(IIrStatementVisitor<T> visitor) => visitor.End(this);

    public IrLocal Local { get; } = local;
}

public sealed class IrUnreachable(IrLoc loc) : IrStatement
{
    public override T Accept<T>(IIrStatementVisitor<T> visitor) => visitor.Unreachable(this);

    public IrLoc Loc { get; } = loc;
}
