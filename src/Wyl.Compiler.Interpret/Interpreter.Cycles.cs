using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;

namespace Wyl.Compiler.Interpret;

// The cycle collector, a port of runtime/wyl/cycles.h: synchronous trial deletion (Bacon and Rajan,
// "Concurrent Cycle Collection in Reference Counted Systems", 2001), run only when the program
// calls memory::collectCycles. Traversals use explicit stacks, since a cycle may be a long chain. A
// reference the walk misses only makes its target look referenced from outside, so the collector
// frees less, never something in use.

public sealed partial class Interpreter
{
    /// <summary>What the collector did since the last call, as <c>wyl::CycleStats</c>.</summary>
    internal sealed class CycleStats
    {
        public long Buffered;
        public long Visited;
        public long Freed;
    }

    internal sealed class ThreadState
    {
        public List<HeapObject?> Buffer = [];
        public int Capacity;
        public int Holes;
        public int Destroying;
        public CycleStats Stats = new();
        public List<HeapObject?> Roots = [];
        public List<HeapObject> Marked = [], Stack = [], Garbage = [];
        public int Depth;
        public long LastWaiting;
        public int GrowingCalls;
        public bool WarnedGrowing;
        public List<(string Name, int Count)> FreedClasses = [];
        // A thread holds at most one lock and waits for nothing while it holds it.
        public bool HoldsLock;
    }

    List<HeapObject?> buffer { get => current.Value!.Buffer; set => current.Value!.Buffer = value; }
    int capacity { get => current.Value!.Capacity; set => current.Value!.Capacity = value; }
    int holes { get => current.Value!.Holes; set => current.Value!.Holes = value; }
    int destroying { get => current.Value!.Destroying; set => current.Value!.Destroying = value; }
    CycleStats stats { get => current.Value!.Stats; set => current.Value!.Stats = value; }
    List<HeapObject?> roots { get => current.Value!.Roots; set => current.Value!.Roots = value; }
    List<HeapObject> marked { get => current.Value!.Marked; set => current.Value!.Marked = value; }
    List<HeapObject> stack { get => current.Value!.Stack; set => current.Value!.Stack = value; }
    List<HeapObject> garbage { get => current.Value!.Garbage; set => current.Value!.Garbage = value; }
    int depth { get => current.Value!.Depth; set => current.Value!.Depth = value; }
    long lastWaiting { get => current.Value!.LastWaiting; set => current.Value!.LastWaiting = value; }
    int growingCalls { get => current.Value!.GrowingCalls; set => current.Value!.GrowingCalls = value; }
    bool warnedGrowing { get => current.Value!.WarnedGrowing; set => current.Value!.WarnedGrowing = value; }
    List<(string Name, int Count)> freedClasses => current.Value!.FreedClasses;

    /// <summary>How many objects were created, which gives each its creation number and so its age.</summary>
    long objectsCreated;

    // Debug builds' diagnostics, as runtime/wyl/cycles.h keeps them.
    const int GrowingCallsToWarn = 5;
    const int CollectionsToWarn = 10;

    /// <summary>A reference to a counted object went away: destroy it at zero, or buffer it for the collector.</summary>
    public void Released(HeapObject heap, long count)
    {
        if (count <= 0)
            DestroyHeapObject(heap);
        // Only a cyclic object whose count stayed above zero may have joined a cycle; any other flag
        // means the collector already has it (wyl::Object::release).
        else if (heap.Cyclic && !heap.Buffered && !heap.Dying && !heap.Gray && !heap.White && !heap.Finalized)
            AddToBuffer(heap);
    }

    // ---------------------------------------------------------------------------------------------
    // The buffer
    // ---------------------------------------------------------------------------------------------

    void AddToBuffer(HeapObject heap)
    {
        Add(heap);
        stats.Buffered++;
    }

    void Add(HeapObject heap)
    {
        if (buffer.Count == capacity && holes * 2 >= buffer.Count)
            Compact();
        // The runtime's "too many objects" panic needs 2^32 buffered objects, which no program here reaches.
        buffer.Add(heap);
        heap.Buffered = true;
        heap.Slot = (uint)buffer.Count;
        if (buffer.Count > capacity)
            capacity += Math.Max(1, capacity / 2);
    }

    void RemoveFromBuffer(HeapObject heap)
    {
        buffer[(int)heap.Slot - 1] = null;
        holes++;
        heap.Slot = 0;
        heap.Buffered = false;
    }

    void Compact()
    {
        // Indexed, since assigning into a List makes its enumerator refuse to go on.
        int kept = 0;
        for (int i = 0; i < buffer.Count; i++)
        {
            if (buffer[i] is not { } heap)
                continue;
            buffer[kept++] = heap;
            heap.Slot = (uint)kept;
        }
        buffer.RemoveRange(kept, buffer.Count - kept);
        holes = 0;
    }

    /// <summary>
    /// Makes the graph's buffer this thread's while the graph's code runs, and takes the thread's
    /// buffer back when the code stops. The objects keep their slots, which number places in the
    /// buffer that is current.
    /// </summary>
    void SwapGraph(GraphBuffer graph)
    {
        var thread = current.Value!;
        (thread.Buffer, graph.Buffer) = (graph.Buffer, thread.Buffer);
        (thread.Capacity, graph.Capacity) = (graph.Capacity, thread.Capacity);
        (thread.Holes, graph.Holes) = (graph.Holes, thread.Holes);
    }

    /// <summary>Adds a graph's buffered objects to this thread's buffer, renumbering their slots.</summary>
    void MergeGraph(GraphBuffer graph)
    {
        foreach (var heap in graph.Buffer)
        {
            if (heap is { } present)
                Add(present);
        }
        graph.Buffer = [];
        graph.Capacity = 0;
        graph.Holes = 0;
    }

    /// <summary>Releases a graph's state: its buffer joins the releasing thread's.</summary>
    public void ReleasingIsolated(IsolatedStateValue state) => MergeGraph(state.Buffer);

    // ---------------------------------------------------------------------------------------------
    // Trial deletion
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Calls <paramref name="visit"/> on each strong reference the object holds, in the order its
    /// generated <c>Visit</c> visits them: a base's before its own, fields in declaration order,
    /// a container's elements last, and a closure's captures in the order it holds them.
    /// </summary>
    void ForEachReference(HeapObject heap, ReferenceVisitor visit)
    {
        switch (heap)
        {
            case ObjectRef obj:
                // The chain is most derived first; a class's Visit starts with its base's.
                foreach (var type in obj.Chain.Reverse())
                {
                    foreach (var cell in obj.Fields[type.Symbol!])
                        VisitCellReferences(cell, visit);
                    if (OwnsContainerElement(type))
                        VisitContainerElements(obj, type, visit);
                }
                break;
            case ClosureValue closure:
                foreach (var cell in closure.Captures)
                    VisitCellReferences(cell, visit);
                break;
        }
    }

    /// <summary>The references that matter to the collector: those an object on a cycle may hold.</summary>
    void ForEachChild(HeapObject heap, Action<HeapObject> visit) =>
        ForEachReference(heap, (target, _) =>
        {
            if (target.Cyclic)
                visit(target);
        });

    /// <summary>A strong reference in a value, and what forgets it, as <c>wyl::Visit</c> finds and clears one.</summary>
    delegate void ReferenceVisitor(HeapObject target, Action clear);

    void VisitCellReferences(Cell cell, ReferenceVisitor visit)
    {
        switch (cell.Value)
        {
            case ObjectRef obj:
                visit(obj, () => ClearCell(cell));
                break;
            case FunctionValue { Closure: { } closure } function:
                visit(closure, () => ClearClosure(function));
                break;
            case StructValue value:
                foreach (var field in value.Fields)
                    VisitCellReferences(field, visit);
                break;
            case EnumValue value:
                foreach (var field in value.Fields)
                    VisitCellReferences(field, visit);
                break;
            case ArrayValue value:
                foreach (var element in value.Elements)
                    VisitCellReferences(element, visit);
                break;
            case OptionalValue { Inner: { } inner }:
                VisitCellReferences(inner, visit);
                break;
        }
    }

    /// <summary>Forgets a reference in a cell, releasing the count it held, as <c>reference = nullptr</c> does.</summary>
    void ClearCell(Cell cell)
    {
        var old = cell.Value;
        cell.Value = cell.Type.IsReference || cell.Type.IsNullableReference ? new RefValue(null) : NullValue.Instance;
        Release(cell.Type, old);
    }

    void ClearClosure(FunctionValue function)
    {
        if (function.Closure is not { } closure)
            return;
        function.Closure = null;
        Released(closure, closure.ReleasedOne());
    }

    /// <summary>Whether a class declares the container elements its own <c>Visit</c> walks, rather than inheriting them.</summary>
    bool OwnsContainerElement(WylType type) =>
        stdlib.ContainerElement(type) is not null
        && (type.BaseType is not { } baseType || stdlib.ContainerElement(baseType) is null);

    /// <summary>
    /// Visits a container's elements, which its generated <c>Visit</c> reaches through
    /// <c>asReadOnlySpan</c>, so the interpreter never assumes the container's layout.
    /// </summary>
    void VisitContainerElements(ObjectRef obj, WylType type, ReferenceVisitor visit)
    {
        if (!type.Symbol!.Methods.TryGetValue("asReadOnlySpan", out var methods) || methods.FirstOrDefault() is not { } method)
            return;
        var reference = new MethodReference(method, type, []);
        var function = program.Functions.Single(method)
            ?? throw new InterpreterException($"no lowered function for '{method.Signature}'");
        var frame = new Frame(reference.Map);
        if (function.This is { } self)
            frame.Locals[self] = new OwnedStorage(new Cell(type, obj));
        if (Invoke(function, frame) is not StructValue span)
            throw new InterpreterException($"'{method.Name}' did not return a span");
        var symbol = span.Type.Symbol!;
        if (symbol.FindField("data") is not { } data || symbol.FindField("size") is not { } size)
            throw new InterpreterException($"'{span.Type}' declares no 'data' and 'size'");
        int length = (int)(long)Integer(span.Fields[symbol.Fields.IndexOf(size)].Value);
        // An empty span views a null pointer, which C++ never dereferences.
        if (length == 0)
            return;
        if (span.Fields[symbol.Fields.IndexOf(data)].Value is not PointerValue pointer)
            throw new InterpreterException($"'{span.Type}' does not view storage");
        for (int i = 0; i < length; i++)
        {
            var element = pointer.Buffer.Read(pointer.Index + i)
                ?? throw new InterpreterException("a container element was never initialized");
            VisitCellReferences(element, visit);
        }
    }

    /// <summary>
    /// Subtracts the references among the objects reachable from <paramref name="root"/>, marking
    /// them gray, breadth first. Fails, undoing every mark and subtraction of the group, once more
    /// than <paramref name="budget"/> objects are marked in all.
    /// </summary>
    bool MarkGray(HeapObject root, long budget)
    {
        if (root.Gray)
            return true;
        int start = marked.Count;
        if (start >= budget)
            return false;
        root.Gray = true;
        marked.Add(root);
        int expanded = start;
        while (expanded < marked.Count)
        {
            ForEachChild(marked[expanded++], child =>
            {
                child.ReleasedOne();
                if (!child.Gray)
                {
                    child.Gray = true;
                    marked.Add(child);
                }
            });
            if (marked.Count > budget)
            {
                for (int i = start; i < expanded; i++)
                    ForEachChild(marked[i], child => child.Retain());
                for (int i = start; i < marked.Count; i++)
                    marked[i].Gray = false;
                marked.RemoveRange(start, marked.Count - start);
                return false;
            }
        }
        return true;
    }

    /// <summary>Gray objects still referenced from outside the group are live, and so is everything they reach; the rest turn white.</summary>
    void Scan(HeapObject root)
    {
        stack.Add(root);
        while (stack.Count > 0)
        {
            var heap = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (!heap.Gray)
                continue;
            if (heap.Count > 0)
            {
                ScanBlack(heap);
            }
            else
            {
                heap.Gray = false;
                heap.White = true;
                ForEachChild(heap, child => stack.Add(child));
            }
        }
    }

    /// <summary>Restores the references a live object and everything it reaches hold.</summary>
    void ScanBlack(HeapObject root)
    {
        int bottom = stack.Count;
        root.Gray = false;
        root.White = false;
        stack.Add(root);
        while (stack.Count > bottom)
        {
            var heap = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            ForEachChild(heap, child =>
            {
                child.Retain();
                if (child.Gray || child.White)
                {
                    child.Gray = false;
                    child.White = false;
                    stack.Add(child);
                }
            });
        }
    }

    void CollectWhite(HeapObject root)
    {
        if (!root.White)
            return;
        int next = garbage.Count;
        root.White = false;
        root.Dying = true;
        garbage.Add(root);
        while (next < garbage.Count)
        {
            ForEachChild(garbage[next++], child =>
            {
                if (child.White)
                {
                    child.White = false;
                    child.Dying = true;
                    garbage.Add(child);
                }
            });
        }
    }

    /// <summary>Trial deletion from the first <paramref name="count"/> roots, leaving the garbage found in <see cref="garbage"/>.</summary>
    void FindGarbage(int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (roots[i] is { } root)
                Scan(root);
        }
        for (int i = 0; i < count; i++)
        {
            if (roots[i] is { } root)
                CollectWhite(root);
        }
    }

    /// <summary>
    /// Runs every destructor while every object of the garbage is intact, then frees it. The
    /// collector holds a count on each object meanwhile, so no release can destroy one early.
    /// Destructors run oldest first: the order the collector found the objects in depends on which
    /// releases buffered them, which the compiler's own temporaries change.
    /// </summary>
    void DestroyGarbage()
    {
        var created = (uint)Interlocked.Read(ref objectsCreated);
        garbage.Sort((a, b) => b.Age(created).CompareTo(a.Age(created)));
        if (!options.Release)
            NoteFreed();
        foreach (var heap in garbage)
            ForEachChild(heap, child => child.Retain());
        foreach (var heap in garbage)
        {
            heap.Retain();
            heap.Buffered = false;
            heap.ClearWeak();
            heap.Slot = 0;
        }

        // Only the garbage is finalized, since earlier collections freed what they finalized.
        foreach (var heap in garbage)
        {
            heap.Finalized = true;
            Finalize(heap);
        }

        // Destructors may have changed references among the garbage, but none may have left one to
        // it anywhere else: without the references among it, only the collector's count is left.
        foreach (var heap in garbage)
            ForEachChild(heap, child =>
            {
                if (child.Finalized)
                    child.ReleasedOne();
            });
        foreach (var heap in garbage)
        {
            if (heap.Count != 1)
                throw new WylPanic("a destructor kept a reference to an object of the cycle being collected", "<destructor>");
        }

        // Clearing a reference among the garbage releases the count just taken off for it.
        foreach (var heap in garbage)
            ForEachReference(heap, (target, clear) =>
            {
                if (target.Finalized)
                    target.Retain();
                clear();
            });
        foreach (var heap in garbage)
            Delete(heap);
        stats.Freed += garbage.Count;
    }

    /// <summary>Runs an object's destructor bodies without releasing its fields, as <c>Finalize</c> does.</summary>
    void Finalize(HeapObject heap)
    {
        if (heap is not ObjectRef obj)
            return;
        foreach (var type in obj.Chain.ToList())
        {
            obj.Active = type;
            // The standard library's containers release their elements with their fields, so their
            // destructor bodies run after every destructor of the cycle, as their class's does.
            if (!type.Symbol!.HasSeparateDestructor)
                continue;
            if (type.Symbol.Destructor is { } destructor && program.Functions.Single(destructor) is { } function)
            {
                var frame = new Frame(TypeMap.For(type));
                if (function.This is { } self)
                    frame.Locals[self] = new OwnedStorage(new Cell(type, obj));
                Invoke(function, frame);
            }
        }
    }

    /// <summary>Runs a collection, as <c>wyl::CycleCollector::collect</c> does.</summary>
    void Collect(long budget, string location)
    {
        if (destroying > 0)
            throw new WylPanic("memory::collectCycles cannot be called from a destructor", location);
        destroying++;
        try
        {
            // The roots trade storage with the buffer.
            roots.AddRange(buffer);
            buffer.Clear();
            holes = 0;

            int processed = 0;
            for (; processed < roots.Count; processed++)
            {
                if (roots[processed] is { } root && !MarkGray(root, budget))
                    break;
            }
            stats.Visited += marked.Count;

            FindGarbage(processed);
            for (int i = 0; i < processed; i++)
            {
                if (roots[i] is { } root)
                {
                    root.Buffered = false;
                    root.Slot = 0;
                }
            }

            // Back in the buffer before any destructor runs, since one may destroy them.
            for (int i = processed; i < roots.Count; i++)
            {
                if (roots[i] is { } root && !root.Dying)
                    Add(root);
            }
            roots.Clear();
            marked.Clear();

            if (garbage.Count > 0)
                DestroyGarbage();
            garbage.Clear();
            if (!options.Release)
                NoteWaiting();
        }
        finally
        {
            destroying--;
        }
    }

    /// <summary>What the collector did since the last call, which resets it.</summary>
    CycleStats TakeStats()
    {
        var taken = stats;
        stats = new CycleStats();
        return taken;
    }
}
