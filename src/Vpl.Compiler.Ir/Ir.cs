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
    public List<IrFunction> Functions { get; } = [];

    /// <summary>
    /// How many times the pipeline has changed the program, so that facts found from it can tell
    /// when they describe an earlier program.
    /// </summary>
    public int Generation { get; private set; }

    public void Changed() => Generation++;

    /// <summary>Each for-each loop's element variable, and where it is declared, for <c>InPlaceElements</c>.</summary>
    public Dictionary<IrLocal, Location> LoopElements { get; } = [];

    /// <summary>The source expression or statement most statements were lowered from, for errors that only the IR can find.</summary>
    public Dictionary<IrStatement, Location> Origins { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Where each <see cref="IrNew"/> and <see cref="IrClosure"/> is in the source, for tests.</summary>
    public Dictionary<IrRvalue, Location> Creations { get; } = new(ReferenceEqualityComparer.Instance);
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

    /// <summary>
    /// For a lambda, the locals around it that its body, or a lambda it makes, may assign or change
    /// in place, as <c>LambdaWrites</c> finds them each time the IR is verified.
    /// Code that runs once it is made may run it.
    /// </summary>
    public HashSet<IrLocal> ChangesAround { get; } = [];

    public IrBlock Body { get; set; } = new();

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

/// <summary>
/// A local, parameter, or temporary. A temporary is defined once, by a <see cref="IrLet"/>, or for a
/// value that comes out of control flow, declared by a <see cref="IrDeclare"/> and stored exactly
/// once on every path.
/// </summary>
public sealed class IrLocal(string name, VplType type, IrLocalKind kind)
{
    public string Name { get; } = name;

    /// <summary>
    /// As for a <see cref="LocalSymbol"/>: what a <c>ref T</c> refers to is <c>T</c>, with
    /// <see cref="RefKind"/>; a <c>ref? T</c> has that type. A hold's or slot's is the object's.
    /// </summary>
    public VplType Type { get; set; } = type;

    /// <summary>Settable so that a pass can turn a hold into a borrow, which keeps every place naming the local.</summary>
    public IrLocalKind Kind { get; set; } = kind;
    public RefKind RefKind { get; init; }
    public bool IsTemporary { get; init; }
    public bool IsParameter { get; init; }
    public bool IsThis { get; init; }

    /// <summary>An escaping lambda's copy of a local, or of <c>this</c> if it has no <see cref="Symbol"/>.</summary>
    public bool IsCapture { get; init; }

    /// <summary>The source local it is, if any.</summary>
    public LocalSymbol? Symbol { get; init; }

    /// <summary>
    /// A loop element borrowed in place: an alias of an element of what the loop iterates, which
    /// is borrowed while it lives, so only storing over an element of its type can destroy it. The
    /// verifier checks that nothing does, by the effects and the whole-program write summaries.
    /// </summary>
    public bool IsElement { get; set; }

    bool changedByLambda;

    /// <summary>
    /// Some lambda of the function may assign it or change it in place, so code that runs anywhere in
    /// the function may change it. This is the whole-function answer, for lowering and the passes; the
    /// verifier asks only the lambdas made before a point (<see cref="IrFunction.ChangesAround"/>).
    /// Lowering sets it conservatively, for every writable local a non-escaping lambda captures,
    /// and once the lowered program is verified, <c>LambdaWrites</c> replaces it
    /// with the union of the lambdas' <see cref="IrFunction.ChangesAround"/>. The verifier checks
    /// that it covers them. A reference can't be made to refer elsewhere: a lambda storing
    /// through it writes its target.
    /// </summary>
    public bool IsChangedByLambda
    {
        get => changedByLambda && Kind != IrLocalKind.Reference;
        set => changedByLambda = value;
    }

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
}

public sealed class IrBlock : IrStatement
{
    public override T Accept<T>(IIrStatementVisitor<T> visitor) => visitor.Block(this);

    public List<IrStatement> Statements { get; } = [];

    /// <summary>
    /// A block holding only temporaries: one statement's, which are released when it completes
    /// (spec §3.2, When references are released), or a switch expression's, which are its
    /// statement's. It is a block only if releasing one of them may be observed (<see cref="DeclaresOwner"/>).
    /// </summary>
    public bool IsStatement { get; init; }

    /// <summary>Whether it declares a local, other than those in <paramref name="except"/>, whose release may run code.</summary>
    public bool DeclaresOwner(params IrLocal?[] except) => Statements.Any(statement => statement switch
    {
        IrLet let => !except.Contains(let.Local) && IrEffects.ReleaseMayRunCode(let.Local),
        IrDeclare declare => !except.Contains(declare.Local) && IrEffects.ReleaseMayRunCode(declare.Local),
        _ => false,
    });

    public IrBlock() { }

    public IrBlock(IEnumerable<IrStatement> statements) => Statements.AddRange(statements);
}

/// <summary><c>let x = rvalue</c>: declares and initializes a local in the enclosing block.</summary>
public sealed class IrLet(IrLocal local, IrRvalue value) : IrStatement
{
    public override T Accept<T>(IIrStatementVisitor<T> visitor) => visitor.Let(this);

    public IrLocal Local { get; } = local;
    public IrRvalue Value { get; set; } = value;
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
}

/// <summary><c>check.kind operands @loc</c>: panics unless the condition holds.</summary>
public sealed class IrCheck(IrCheckKind kind, IReadOnlyList<IrOperand> operands, IrLoc loc) : IrStatement
{
    public override T Accept<T>(IIrStatementVisitor<T> visitor) => visitor.Check(this);

    public IrCheckKind Kind { get; } = kind;
    public List<IrOperand> Operands { get; } = [.. operands];
    public IrLoc Loc { get; } = loc;
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

    public IrBlock Body { get; set; } = new();
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
