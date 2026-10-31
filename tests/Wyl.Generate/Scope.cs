using Wyl.Compiler.Semantics;

namespace Wyl.Generate;

/// <summary>
/// A local in scope: its name, exact type, whether it may be assigned, and the range (of an integer,
/// or a <c>char</c>'s code point) every path keeps.
/// </summary>
sealed class Local(string name, WylType type, bool mutable)
{
    public string Name { get; } = name;
    public WylType Type { get; } = type;
    public bool Mutable { get; } = mutable;
    public IntegerRange? Range { get; set; }
    public ExactFloat? Exact { get; set; }
}

/// <summary>
/// The locals in scope, by block. A local's range is widened when a block that may assign it ends
/// and when a loop body starts, so every guard is valid on every path without a dataflow engine.
/// </summary>
sealed class Scope
{
    readonly List<Frame> frames = [new()];
    int nextName;

    sealed class Frame
    {
        public List<Local> Locals { get; } = [];
        public HashSet<Local> Written { get; } = [];
    }

    public Local Declare(WylType type, bool mutable, IntegerRange? range = null, ExactFloat? exact = null)
    {
        var local = new Local($"v{nextName++}", type, mutable) { Range = range, Exact = exact };
        frames[^1].Locals.Add(local);
        return local;
    }

    /// <summary>A parameter or a loop counter: named by its function or loop, never assignable.</summary>
    public Local Declare(string name, WylType type, IntegerRange? range = null)
    {
        var local = new Local(name, type, mutable: false) { Range = range };
        frames[^1].Locals.Add(local);
        return local;
    }

    public IEnumerable<Local> Visible => frames.SelectMany(frame => frame.Locals);

    public IReadOnlyList<Local> OfType(WylType type) => [.. Visible.Where(local => local.Type == type)];

    /// <summary>Records an assignment in the current block, so leaving it widens the local.</summary>
    public void MarkWritten(Local local) => frames[^1].Written.Add(local);

    public void Push() => frames.Add(new Frame());

    /// <summary>Leaves a block: every local it wrote may now hold any value of its type.</summary>
    public void Pop()
    {
        var frame = frames[^1];
        frames.RemoveAt(frames.Count - 1);
        foreach (var local in frame.Written)
            Widen(local);
    }

    /// <summary>A loop body runs more than once, so a mutable local may see any value of its type there.</summary>
    public void WidenMutables()
    {
        foreach (var local in Visible.Where(local => local.Mutable))
            Widen(local);
    }

    static void Widen(Local local)
    {
        if (local.Type.IsInteger)
            local.Range = IntegerFacts.Full(local.Type);
        else if (local.Type == WylType.Char)
            local.Range = ConversionFacts.CodePoints;
        else if (local.Type == WylType.Float || local.Type == WylType.Double)
            local.Exact = null;
    }
}
