using System.Text;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.Interpret;

// The interpreter's values and storage. Every place is a Cell, so a reference to a field is a
// reference to the same cell after any number of projections. What a value owns is Values' job,
// defined once for every kind here and driven by the WylType.

/// <summary>A type and the value in it, mutable in place: every place the IR names.</summary>
public sealed class Cell(WylType type, Value value)
{
    public WylType Type { get; } = type;
    public Value Value { get; set; } = value;

    public override string ToString() => $"{Type}: {Value}";
}

/// <summary>A Wylow value: an immutable scalar or string, or a tree of cells.</summary>
public abstract class Value;

/// <summary>An integer of any width, already normalized to its type's range.</summary>
public sealed class IntValue(Int128 bits) : Value
{
    public Int128 Bits { get; } = bits;

    public override string ToString() => Bits.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A <c>float</c> or <c>double</c>, which keeps its IEEE value; a float is rounded on every operation.</summary>
public sealed class FloatValue(double number) : Value
{
    public double Number { get; } = number;

    public override string ToString() => Number.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class BoolValue(bool flag) : Value
{
    public bool Flag { get; } = flag;

    public override string ToString() => Flag ? "true" : "false";
}

/// <summary>A <c>char</c>, a Unicode scalar value.</summary>
public sealed class CharValue(int codePoint) : Value
{
    public int CodePoint { get; } = codePoint;

    public override string ToString() => char.ConvertFromUtf32(CodePoint);
}

/// <summary>An immutable UTF-8 string; copies share the bytes, as the runtime's counted block does.</summary>
public sealed class StringValue(byte[] bytes) : Value
{
    public static readonly StringValue Empty = new([]);

    public byte[] Bytes { get; } = bytes;

    public static StringValue Of(string text) => new(Encoding.UTF8.GetBytes(text));

    public string Text => Encoding.UTF8.GetString(Bytes);
    public override string ToString() => Text;
}

/// <summary>Null of a nullable reference or a pointer.</summary>
public sealed class NullValue : Value
{
    public static readonly NullValue Instance = new();

    public override string ToString() => "null";
}

/// <summary>
/// A reference-counted thing the collector tracks, as <c>wyl::Object</c> carries: a class object or
/// an escaping lambda's closure. Each flag is one bit of the runtime header's state.
/// </summary>
internal static class HeapSync
{
    public static readonly object Lock = new();
}

public abstract class HeapObject : Value
{
    long m_Count = 1;

    /// <summary>The references to it; the <c>new</c> expression owns the first.</summary>
    public long Count { get => Interlocked.Read(ref m_Count); set => Interlocked.Exchange(ref m_Count, value); }

    public void Retain() => Interlocked.Increment(ref m_Count);

    public long ReleasedOne() => Interlocked.Decrement(ref m_Count);

    /// <summary>Its class may be part of a cycle, so the collector buffers and visits it.</summary>
    public bool Cyclic { get; set; }

    /// <summary>Its count dropped without reaching zero, so the collector will look at it.</summary>
    public bool Buffered { get; set; }

    /// <summary>The count reached zero, or the collector found it garbage: weak references read null.</summary>
    public bool Dying { get; set; }

    /// <summary>Trial deletion's marks; one with neither is black.</summary>
    public bool Gray { get; set; }
    public bool White { get; set; }

    /// <summary>The collector has run its destructors, which its destruction then skips.</summary>
    public bool Finalized { get; set; }

    /// <summary>How many objects were created before it, which orders a collected cycle's destructors.</summary>
    public uint Created { get; set; }

    uint slot;

    /// <summary>Its place in the cycle buffer, zero when it is not buffered.</summary>
    public virtual uint Slot
    {
        get => slot;
        set => slot = value;
    }

    /// <summary>Its creation number, which an object's weak block takes over from the header.</summary>
    public virtual uint AgeBasis => Created;

    /// <summary>How many objects were created after it, staying in creation order across the counter's wrap.</summary>
    public uint Age(uint created) => created - AgeBasis;

    /// <summary>Leaves the weak references behind: they read null from now on.</summary>
    public virtual void ClearWeak() => Dying = true;
}

/// <summary>
/// Shared by an object and the weak references to it, so that they can tell when it is gone. The
/// object holds one count and each weak reference one; the last to let go frees the block.
/// </summary>
public sealed class WeakBlock(ObjectRef target, uint slot, uint created)
{
    long m_Count = 1;

    public ObjectRef? Target { get; set; } = target;
    public long Count { get => Interlocked.Read(ref m_Count); set => Interlocked.Exchange(ref m_Count, value); }

    public void Retain() => Interlocked.Increment(ref m_Count);

    public long ReleasedOne() => Interlocked.Decrement(ref m_Count);

    /// <summary>The object's buffer slot and creation number, which the block takes over from its header.</summary>
    public uint Slot { get; set; } = slot;
    public uint Created { get; set; } = created;
}

/// <summary>
/// A class object: its fields, one array per class that declares them, an intrusive count, and the
/// class a dispatched call runs on it now (a base during its construction or destruction, as C++).
/// Every copy of a reference is this same instance with the count raised, so a field written
/// through one is seen through every other.
/// </summary>
public sealed class ObjectRef(WylType type, Dictionary<TypeSymbol, Cell[]> fields) : HeapObject
{
    public WylType Type { get; } = type;
    public Dictionary<TypeSymbol, Cell[]> Fields { get; } = fields;

    /// <summary>The class whose methods dispatch on it: its own, or a base while it is constructed or destroyed.</summary>
    public WylType Active { get; set; } = type;

    /// <summary>The block shared with the weak references to it, once it has one.</summary>
    public WeakBlock? Weak { get; private set; }

    // While it has a weak block, the block holds the buffer slot and the creation number.
    public override uint Slot
    {
        get => Weak is { } weak ? weak.Slot : base.Slot;
        set
        {
            if (Weak is { } weak)
                weak.Slot = value;
            else
                base.Slot = value;
        }
    }

    public override uint AgeBasis => Weak?.Created ?? base.AgeBasis;

    /// <summary>The classes of its chain, most derived first, which each own the fields they declare.</summary>
    public IEnumerable<WylType> Chain
    {
        get
        {
            for (WylType? current = Type.NonNull; current is not null && current.Symbol is { Kind: TypeKind.Class }; current = current.BaseType)
                yield return current;
        }
    }

    /// <summary>Its weak block, made on the first weak reference; null for an object already dying.</summary>
    public WeakBlock? WeakBlock()
    {
        lock (HeapSync.Lock)
        {
            if (Dying)
                return null;
            return Weak ??= new WeakBlock(this, Slot, base.AgeBasis);
        }
    }

    /// <summary>
    /// Drops one reference and returns how many are left. A shared object's last release clears its
    /// weak block under the lock <see cref="WeakValue.Get"/> takes, so that call finds a count above
    /// zero or no target, and never revives an object another thread is destroying.
    /// </summary>
    public long ReleasedReference()
    {
        if (Type.Symbol is not { IsShared: true })
            return ReleasedOne();
        lock (HeapSync.Lock)
        {
            long count = ReleasedOne();
            if (count == 0)
                ClearWeak();
            return count;
        }
    }

    public override void ClearWeak()
    {
        lock (HeapSync.Lock)
        {
            Dying = true;
            if (Weak is not { } weak)
                return;
            Weak = null;
            Slot = weak.Slot;
            Created = weak.Created;
            weak.Target = null;
            weak.ReleasedOne();
        }
    }

    public Cell FieldCell(FieldSymbol field) => Fields[field.Owner][field.Owner.Fields.IndexOf(field)];

    public override string ToString() => $"{Type}";
}

/// <summary>A struct: its fields, in declaration order.</summary>
public sealed class StructValue(WylType type, Cell[] fields) : Value
{
    public WylType Type { get; } = type;
    public Cell[] Fields { get; } = fields;

    /// <summary>
    /// Whether it holds a value. A move or a <c>discard</c> takes that away, and its destructor's
    /// body does not run when it is released.
    /// </summary>
    public bool Engaged { get; set; } = true;

    /// <summary>Whether something refers into it, so releasing or replacing it panics (spec <c>memory.holds</c>).</summary>
    public bool InUse { get; set; }

    public override string ToString() => $"{Type} {{...}}";
}

/// <summary>An enum: the active variant and its fields.</summary>
public sealed class EnumValue(WylType type, VariantSymbol variant, Cell[] fields) : Value
{
    public WylType Type { get; } = type;
    public VariantSymbol Variant { get; } = variant;
    public Cell[] Fields { get; } = fields;

    /// <summary>Whether something refers into its payload, so releasing or replacing it panics.</summary>
    public bool InUse { get; set; }

    public override string ToString() => $"{Variant}";
}

/// <summary>A fixed array: its elements, stored inline.</summary>
public sealed class ArrayValue(WylType type, Cell[] elements) : Value
{
    public WylType Type { get; } = type;
    public Cell[] Elements { get; } = elements;

    /// <summary>
    /// The storage <c>data</c> points into, made once so that two pointers to this array compare
    /// equal, as their addresses do. Its cells are this array's own, so a write through a pointer
    /// is seen through the array.
    /// </summary>
    public Buffer View => view ??= Buffer.Over(Elements);

    Buffer? view;

    public override string ToString() => $"{Type} [{Elements.Length}]";
}

/// <summary>
/// Raw storage a pointer walks, a slot per element that is empty until a value is initialized in
/// it, as <c>memory::allocate</c> hands out. A fixed array's elements are a buffer whose cells it
/// shares; a string's bytes are a buffer of their own.
/// </summary>
public sealed class Buffer
{
    readonly Cell?[] cells;
    bool freed;

    Buffer(Cell?[] cells) => this.cells = cells;

    public static Buffer Allocate(int count) => new(new Cell?[count]);

    /// <summary>A buffer over an array's own element cells, which a write through a pointer reaches.</summary>
    public static Buffer Over(Cell[] elements) => new([.. elements.Select(cell => (Cell?)cell)]);

    /// <summary>The cell at <paramref name="index"/>, or null while the storage is uninitialized.</summary>
    public Cell? Read(int index)
    {
        Check(index);
        return cells[index];
    }

    public void Write(int index, Cell? cell)
    {
        Check(index);
        cells[index] = cell;
    }

    public void Free() => freed = true;

    /// <summary>Indexing outside a buffer, or using freed storage, is the program's undefined behavior.</summary>
    void Check(int index)
    {
        if (freed)
            throw new UndefinedBehavior("memory was used after it was freed");
        if ((uint)index >= (uint)cells.Length)
            throw new UndefinedBehavior($"a pointer read element {index} of storage holding {cells.Length}");
    }
}

public sealed class ThreadValue : Value
{
    internal LogicalThread? Thread { get; set; }

    public override string ToString() => "thread";
}

public sealed class ChannelState
{
    public readonly object Gate = new();
    public readonly Queue<Cell> Queue = new();
    public readonly ManualResetEventSlim Ready = new(false);
    public int Senders;
    public int Receivers;
    public bool Closed;
}

public sealed class SyncStateValue(ChannelState? state) : Value
{
    public ChannelState? State { get; } = state;

    public override string ToString() => "sync state";
}

/// <summary>
/// A mutex's storage: the lock, the value, and the value's graph. A semaphore is the lock, since a
/// thread waiting for it must also wake when the run ends.
/// </summary>
public sealed class MutexData(Cell content, GraphBuffer graph)
{
    public readonly Semaphore Gate = new(1, 1);
    public Cell Content { get; } = content;
    public GraphBuffer Graph { get; } = graph;
}

public sealed class MutexStateValue(MutexData? data) : Value
{
    public MutexData? Data { get; } = data;

    public override string ToString() => "mutex state";
}

/// <summary>An atomic's storage. The interpreter changes the value under the object's lock, which orders every operation as sequential consistency does.</summary>
public sealed class AtomicData(Value value)
{
    public Value Value { get; set; } = value;
}

public sealed class AtomicStateValue(AtomicData? data) : Value
{
    public AtomicData? Data { get; } = data;

    public override string ToString() => "atomic state";
}

/// <summary>The cycle buffer of one isolated graph, which travels inside its box.</summary>
public sealed class GraphBuffer
{
    public List<HeapObject?> Buffer = [];
    public int Capacity;
    public int Holes;
}

/// <summary>An isolated graph's state: the buffer its box releases on the thread that drops the box.</summary>
public sealed class IsolatedStateValue(GraphBuffer buffer) : Value
{
    public GraphBuffer Buffer { get; } = buffer;

    public override string ToString() => "isolated state";
}

/// <summary>A pointer: a buffer and the element it points at, which may be one past the end. Null is <see cref="NullValue"/>.</summary>
/// <summary>
/// The cycle collector's view of the values a class keeps in raw memory: <c>visit</c> reports each
/// one. A default value, which only the collector does not make, reports nothing.
/// </summary>
public sealed class ElementVisitorValue(Action<Cell>? visit) : Value
{
    public Action<Cell>? Visit { get; } = visit;
}

public sealed class PointerValue(Buffer buffer, int index) : Value
{
    public Buffer Buffer { get; } = buffer;
    public int Index { get; } = index;

    public override string ToString() => $"pointer to element {Index}";
}

/// <summary>A nullable value: the value, or null.</summary>
public sealed class OptionalValue(Cell? inner) : Value
{
    public Cell? Inner { get; } = inner;

    /// <summary>Whether something refers into the value it holds, so replacing or releasing it panics.</summary>
    public bool InUse { get; set; }

    public override string ToString() => Inner is null ? "null" : Inner.Value.ToString()!;
}

/// <summary>A <c>ref?</c> or <c>in?</c> value: the cell it refers to, or null.</summary>
public sealed class RefValue(Cell? cell) : Value
{
    public Cell? Cell { get; } = cell;

    public override string ToString() => Cell is null ? "null ref" : "ref";
}

/// <summary>
/// A function value: a lambda's code, the type arguments its body runs with, and what it reads when
/// it runs — the frame whose locals it uses as places, or the closure holding copies of what it
/// captured. Its escaping form counts its closure, as <c>wyl::Function</c> does; the non-escaping
/// form refers to one it doesn't own.
/// </summary>
internal sealed class FunctionValue(IrFunction function, TypeMap map, Interpreter.Frame? enclosing, ClosureValue? closure) : Value
{
    public IrFunction Function { get; } = function;
    public TypeMap Map { get; } = map;
    public Interpreter.Frame? Enclosing { get; } = enclosing;

    /// <summary>
    /// The closure this value calls with, which a copy holds its own count on and the collector
    /// clears when it collects the closure, as a <c>wyl::Function</c> holds one <c>Ref</c> per copy.
    /// </summary>
    public ClosureValue? Closure { get; set; } = closure;

    /// <summary>A copy: the same code, with its own reference to the closure, which is counted only where the type is escaping.</summary>
    public FunctionValue Copy()
    {
        Closure?.Retain();
        return new FunctionValue(Function, Map, Enclosing, Closure);
    }

    public override string ToString() => Function.Name;
}

/// <summary>
/// An escaping lambda's captures, counted like an object: copies of the function value share it, and
/// the last releases its captures, in the order the lambda captured them.
/// </summary>
public sealed class ClosureValue(string name, Cell[] captures) : HeapObject
{
    /// <summary>What diagnostics call it, as a class's name is for its objects.</summary>
    public string Name { get; } = name;

    public Cell[] Captures { get; } = captures;

    public override string ToString() => "closure";
}

/// <summary>
/// A <c>Weak&lt;T&gt;</c>: the block it shares with the object it watches, and the object it was
/// made from. It keeps the target only while it lives; <c>get</c> counts a new reference.
/// </summary>
public sealed class WeakValue(WeakBlock? block, ObjectRef? target) : Value
{
    public WeakBlock? Block { get; } = block;
    public ObjectRef? Target { get; } = target;

    /// <summary>The value of a weak reference to <paramref name="target"/>, which counts a weak reference on its block.</summary>
    public static WeakValue Of(ObjectRef? target)
    {
        if (target is null)
            return new WeakValue(null, null);
        var block = target.WeakBlock();
        if (block is not null)
            block.Retain();
        return new WeakValue(block, target);
    }

    public WeakValue Copy()
    {
        if (Block is { } block)
            block.Retain();
        return new WeakValue(Block, Target);
    }

    public void Release()
    {
        if (Block is { } block)
            block.ReleasedOne();
    }

    /// <summary>
    /// <c>get()</c>: a counted copy of the target while it lives, as <c>Ref&lt;T&gt; get()</c>
    /// returns, or a null reference once the object is gone.
    /// </summary>
    public Value Get()
    {
        lock (HeapSync.Lock)
        {
            if (Block is not { Target: { } target })
                return new RefValue(null);
            target.Retain();
            return target;
        }
    }

    public override string ToString() => "weak";
}

/// <summary>A choice of functions: the index of the one it selects, and every callable it may select, which it owns.</summary>
public sealed class ChoiceValue(int index, Cell[] options) : Value
{
    public int Index { get; } = index;
    public Cell[] Options { get; } = options;

    public override string ToString() => $"choice {Index}";
}

/// <summary>A value interpolation defers to write with its format specifier and alignment.</summary>
public sealed class FormattedValue(WylType innerType, Value inner, int kind, int precision, int alignment) : Value
{
    public WylType InnerType { get; } = innerType;
    public Value Inner { get; } = inner;
    public int Kind { get; } = kind;
    public int Precision { get; } = precision;
    public int Alignment { get; } = alignment;

    public override string ToString() => "formatted";
}

/// <summary>What a move leaves in a cell; reading or releasing it is a compiler bug.</summary>
public sealed class MovedValue : Value
{
    public static readonly MovedValue Instance = new();

    public override string ToString() => "<moved>";
}

/// <summary>What releasing a value asks when a counted object's count falls: to destroy or buffer it.</summary>
public interface IObjectLifetime
{
    /// <summary>A reference went away: the object's count has already been lowered by one, to <paramref name="count"/>.</summary>
    void Released(HeapObject obj, long count);

    /// <summary>
    /// A unique struct value is released: its destructor's body runs, if it still holds a value,
    /// before its fields are released.
    /// </summary>
    void ReleasingStruct(StructValue value);

    /// <summary>
    /// An isolated graph's state is released: its buffer joins the releasing thread's, and then the
    /// graph's value is released on that thread.
    /// </summary>
    void ReleasingIsolated(IsolatedStateValue state);
}
