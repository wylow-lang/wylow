using Vpl.Compiler.Ir;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.CodeGen;

/// <summary>
/// Which IR temporaries the emitter prints inside their one use rather than declaring them
/// (docs/design/ir.md, §5.1), so that the C++ reads like the source. The rule nesting keeps:
/// every pair of conflicting instructions runs in the same relative order, under every order C++
/// allows, as in the IR. C++ orders an expression's parts only from the inside out, so a temporary
/// nests only if it conflicts with nothing C++ may evaluate before it: what its use reads itself,
/// and the operands already nested to its right. Declining to nest is always correct.
/// </summary>
public sealed partial class CppEmitter
{
    sealed class NestingPlan
    {
        /// <summary>Statements printed inside another, or not at all.</summary>
        public HashSet<IrStatement> Consumed { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Temporaries printed where they are used, with what defines them.</summary>
        public Dictionary<IrLocal, IrStatement> Nested { get; } = [];

        /// <summary>Index checks printed as <c>vpl::at</c> in the element they check.</summary>
        public Dictionary<IrElement, IrCheck> Fused { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// A local declared without a value and stored once at the end of the block after it, which
        /// prints as one declaration with the stored value: a statement's temporaries are released
        /// after the local is initialized from them.
        /// </summary>
        public Dictionary<IrLocal, (IrBlock Block, IrOperand Value)> Initialized { get; } = [];

        public Dictionary<IrLocal, int> Uses { get; } = [];

        /// <summary>What was decided, in order, so that a rejected candidate's decisions can be undone.</summary>
        public List<object> Log { get; } = [];

        public void Undo(int to)
        {
            while (Log.Count > to)
            {
                switch (Log[^1])
                {
                    case IrStatement statement: Consumed.Remove(statement); break;
                    case IrLocal local: Nested.Remove(local); break;
                    case IrElement element: Fused.Remove(element); break;
                    case (IrLocal used, int count): Uses[used] = count; break;
                }
                Log.RemoveAt(Log.Count - 1);
            }
        }
    }

    NestingPlan plan = new();

    void PlanNesting(IrFunction function, bool nest)
    {
        plan = new NestingPlan();
        foreach (var statement in IrWalk.Descendants(function.Body))
        {
            foreach (var place in IrWalk.Operands(statement).SelectMany(IrWalk.Places).Where(p => !IsWholeStoreTarget(statement, p)))
            {
                foreach (var local in IrWalk.PlaceLocals(place))
                    plan.Uses[local] = plan.Uses.GetValueOrDefault(local) + 1;
            }
        }
        if (nest)
            PlanBlock(function.Body);
    }

    static bool IsWholeStoreTarget(IrStatement statement, IrPlace place) =>
        statement is IrStore store && ReferenceEquals(store.Target, place) && place is { IsDeref: false, Projections.Count: 0 };

    void PlanBlock(IrBlock block)
    {
        var statements = block.Statements;
        foreach (var statement in statements)
        {
            foreach (var inner in InnerBlocks(statement))
                PlanBlock(inner);
        }
        for (int j = 0; j + 1 < statements.Count; j++)
        {
            if (statements[j] is IrDeclare { Local: var local } declare && statements[j + 1] is IrBlock initializer
                && TopLevel(initializer) is [IrStore { Target: { IsDeref: false, Projections.Count: 0 } target, Value: var value } store]
                && target.Local == local && local.Kind == IrLocalKind.Value && !IsNonEscaping(local.Type)
                && IrWalk.Descendants(block).OfType<IrStore>().Count(s => s.Target.Local == local && s.Target is { IsDeref: false, Projections.Count: 0 }) == 1)
            {
                plan.Initialized[local] = (initializer, value);
                plan.Consumed.Add(declare);
                plan.Consumed.Add(store);
            }
        }
        bool last = true;
        for (int i = statements.Count - 1; i >= 0; i--)
        {
            if (plan.Consumed.Contains(statements[i]))
                continue;
            Process(statements, i, last);
            last = false;
        }
    }

    static IEnumerable<IrBlock> InnerBlocks(IrStatement statement) => statement switch
    {
        IrBlock block => [block],
        IrIf conditional => conditional.Else is null ? [conditional.Then] : [conditional.Then, conditional.Else],
        IrLoop loop => loop.Continue is null ? [loop.Body] : [loop.Body, loop.Continue],
        _ => [],
    };

    List<IrStatement> TopLevel(IrBlock block) => [.. block.Statements.Where(s => !plan.Consumed.Contains(s))];

    /// <summary>Something an instruction evaluates that may be nested into it: a temporary it names, or an element whose check precedes it.</summary>
    abstract record Nestable;
    sealed record TemporaryUse(IrLocal Local) : Nestable;
    sealed record CheckedElement(IrElement Element, IrLocal Index) : Nestable;

    static IEnumerable<Nestable> Nestables(IrStatement statement)
    {
        foreach (var place in IrWalk.Operands(statement).SelectMany(IrWalk.Places))
        {
            if (place.Local.IsTemporary)
                yield return new TemporaryUse(place.Local);
            foreach (var element in place.Projections.OfType<IrElement>())
            {
                if (element.Index is IrRead { Place: { IsDeref: false, Projections.Count: 0, Local: var index } })
                    yield return new CheckedElement(element, index);
            }
        }
    }

    /// <summary>
    /// Decides what nests into statement <paramref name="k"/>, walking its operands right to left.
    /// A temporary nests only if everything between its definition and this statement already
    /// nests into it, so that nothing moves past a statement that stays separate. Returns the first
    /// statement nested into it, and what it and what is nested in it may do.
    /// </summary>
    (int Frontier, IrFootprint Tree) Process(List<IrStatement> statements, int k, bool last)
    {
        var statement = statements[k];
        if (statement is IrBlock initializer && plan.Initialized.Values.Any(v => v.Block == initializer))
        {
            var footprint = IrWalk.Footprint(initializer);
            footprint.CreatesOwner |= IrWalk.Descendants(initializer).OfType<IrLet>().Any(l => CreatesOwner(l.Value));
            return (k - 1, footprint);
        }
        var own = OwnFootprint(statement);
        var siblings = new IrFootprint();
        siblings.Reads.AddRange(own.Reads);
        int frontier = k;
        foreach (var nestable in Nestables(statement).Reverse())
        {
            int j = frontier - 1;
            if (j < 0)
                break;
            var candidate = statements[j];
            int mark = plan.Log.Count;
            switch (nestable)
            {
                case TemporaryUse { Local: var local }:
                    if (!Defines(candidate, local) || plan.Uses.GetValueOrDefault(local) != 1 || local.Kind is IrLocalKind.Hold or IrLocalKind.Slot)
                        continue;
                    break;
                case CheckedElement { Element: var element, Index: var index }:
                    if (candidate is not IrCheck { Kind: IrCheckKind.Index, Operands: [IrRead { Place: { IsDeref: false, Projections.Count: 0 } checkedPlace }, _] }
                        || checkedPlace.Local != index || plan.Fused.ContainsKey(element))
                        continue;
                    // The check and the element read the index once, as `vpl::at`'s argument.
                    plan.Log.Add((index, plan.Uses[index]));
                    plan.Uses[index]--;
                    break;
            }
            var (childFrontier, tree) = Process(statements, j, last);
            if (tree.ConflictsWith(siblings) || (tree.CreatesOwner && !last))
            {
                plan.Undo(mark);
                break;
            }
            plan.Consumed.Add(candidate);
            plan.Log.Add(candidate);
            switch (nestable)
            {
                case TemporaryUse { Local: var local }:
                    plan.Nested[local] = candidate;
                    plan.Log.Add(local);
                    break;
                case CheckedElement { Element: var element }:
                    plan.Fused[element] = (IrCheck)candidate;
                    plan.Log.Add(element);
                    break;
            }
            frontier = childFrontier;
            siblings.Add(tree);
            own.Add(tree);
        }
        return (frontier, own);
    }

    bool Defines(IrStatement statement, IrLocal local) => statement switch
    {
        IrLet let => let.Local == local && let.Value is not (IrLambda or IrClosure or IrHold),
        IrBlock block => plan.Initialized.TryGetValue(local, out var initialized) && initialized.Block == block,
        _ => false,
    };

    static IrFootprint OwnFootprint(IrStatement statement)
    {
        var footprint = new IrFootprint();
        switch (statement)
        {
            case IrLet let:
                footprint.Add(IrEffects.Of(let.Value));
                footprint.CreatesOwner = CreatesOwner(let.Value);
                break;
            case IrDo run:
                footprint.Add(IrEffects.Of(run.Value));
                footprint.CreatesOwner = CreatesOwner(run.Value);
                break;
            case IrStore or IrCheck:
                footprint.Add(IrEffects.Of(statement));
                break;
        }
        footprint.Reads.AddRange(IrWalk.Reads(statement));
        return footprint;
    }

    /// <summary>Whether the rvalue makes a value of its own whose release may run a destructor.</summary>
    static bool CreatesOwner(IrRvalue value) => value.Type.MayHoldReferences && value switch
    {
        IrUse { Operand: IrRead { Ownership: IrOwnership.Copy } } => true,
        IrUse => false,
        IrConvert { Operand: IrRead { Ownership: not IrOwnership.Copy } } => false,
        IrRef or IrAddr or IrHold or IrData or IrTest => false,
        _ => true,
    };
}
