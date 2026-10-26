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
        /// A local declared without a value, then stored only by the statement defining it: a block
        /// whose temporaries are released after the local is initialized from them, or an <c>if</c>
        /// storing it in both branches. It prints as one declaration of the stored value.
        /// </summary>
        public Dictionary<IrLocal, (IrDeclare Declare, IrStatement Definition)> Initialized { get; } = [];

        /// <summary>
        /// A non-escaping local declared without a value, then stored last by the block after it,
        /// whose temporaries are released after it is initialized. Its C++ type can't be declared
        /// first, so it is initialized by an immediately invoked lambda running the block.
        /// </summary>
        public Dictionary<IrLocal, (IrBlock Block, IrStore Store)> Evaluated { get; } = [];

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
        if (function.BaseCall is not null)
            PlanBaseCall(function, nest);
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
            // The base constructor call is planned already.
            if (plan.Consumed.Contains(statement))
                continue;
            foreach (var inner in InnerBlocks(statement))
                PlanBlock(inner);
            if (statement is IrLet { Value: IrLambda or IrClosure } made)
                PlanBlock(made.Value is IrLambda lambda ? lambda.Function.Body : ((IrClosure)made.Value).Function.Body);
        }
        for (int j = 0; j + 1 < statements.Count; j++)
        {
            if (statements[j] is IrDeclare { Local: { Kind: IrLocalKind.Value } local } && local.Type.IsNonEscapingFunction
                && statements[j + 1] is IrBlock { Statements: [.., IrStore stored] } definition && IsWholeStore(stored, local))
            {
                plan.Evaluated[local] = (definition, stored);
                plan.Consumed.Add(definition);
                plan.Consumed.Add(stored);
            }
        }
        for (int j = 0; j < statements.Count; j++)
        {
            if (statements[j] is not IrDeclare { Local: { Kind: IrLocalKind.Value } local } declare || local.Type.IsNonEscapingFunction)
                continue;
            int m = statements.FindIndex(j + 1, s => IrWalk.Locals(s).Contains(local));
            if (m < 0 || StoresOf(statements[m], local) is not { } stores)
                continue;
            if (IrWalk.Descendants(block).OfType<IrStore>().Count(s => IsWholeStore(s, local)) != stores.Count)
                continue;
            plan.Initialized[local] = (declare, statements[m]);
            plan.Consumed.Add(declare);
            foreach (var store in stores)
                plan.Consumed.Add(store);
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

    static bool IsWholeStore(IrStore store, IrLocal local) => store.Target is { IsDeref: false, Projections.Count: 0 } target && target.Local == local;

    /// <summary>
    /// The stores of a statement that defines a declared local as one value: a block storing it
    /// last, everything before nested into the store, or an <c>if</c> whose branches each do that
    /// and make no value whose release could be observed, since a branch's temporaries are released
    /// when it completes.
    /// </summary>
    List<IrStore>? StoresOf(IrStatement statement, IrLocal local)
    {
        IrStore? Stored(IrBlock block) => TopLevel(block) is [IrStore store] && IsWholeStore(store, local) ? store : null;
        switch (statement)
        {
            case IrBlock block when Stored(block) is { } store:
                return [store];
            case IrIf { Else: { } otherwise } conditional when Stored(conditional.Then) is { } then && Stored(otherwise) is { } other:
                bool owns = IrWalk.Descendants(conditional.Then).Concat(IrWalk.Descendants(otherwise)).OfType<IrLet>().Any(l => CreatesOwner(l.Value) || l.Value is IrHold);
                return owns ? null : [then, other];
            default:
                return null;
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
        if (DefinedBy(statement) is { } defined)
        {
            var (declare, _) = plan.Initialized[defined];
            var (start, footprint) = statement is IrIf conditional ? ProcessOperands(statements, k, last) : (k, new IrFootprint());
            foreach (var inner in InnerBlocks(statement))
                footprint.Add(IrWalk.Footprint(inner));
            footprint.CreatesOwner |= IrWalk.Descendants(statement).OfType<IrLet>().Any(l => CreatesOwner(l.Value));
            // Storing the local is what defines it, which its use comes after.
            footprint.Writes.RemoveAll(w => w.Storage == IrStorage.Frame && w.Local == defined);
            // The declaration is part of the definition, if nothing stays between them.
            int at = statements.IndexOf(declare);
            return (start == at + 1 ? at : start, footprint);
        }
        return ProcessOperands(statements, k, last);
    }

    IrLocal? DefinedBy(IrStatement statement) => plan.Initialized.FirstOrDefault(i => i.Value.Definition == statement).Key;

    (int Frontier, IrFootprint Tree) ProcessOperands(List<IrStatement> statements, int k, bool last)
    {
        var statement = statements[k];
        // A condition is a statement of its own, so its temporaries are released where it ends.
        last |= statement is IrIf;
        var own = OwnFootprint(statement);
        var placeReads = IrWalk.PlaceReads(statement).Select(r => (Locals: IrWalk.PlaceLocals(r.Place).ToHashSet(), Accesses: r.Accesses.ToList())).ToList();
        var nested = new IrFootprint();
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
            // What the statement reads through the candidate it can only read after evaluating it.
            var candidateLocal = nestable switch { TemporaryUse use => use.Local, CheckedElement element => element.Index, _ => null };
            var siblings = new IrFootprint();
            siblings.Add(nested);
            siblings.Reads.AddRange(placeReads.Where(r => !r.Locals.Contains(candidateLocal!)).SelectMany(r => r.Accesses));
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
            nested.Add(tree);
            own.Add(tree);
        }
        return (frontier, own);
    }

    bool Defines(IrStatement statement, IrLocal local) => statement switch
    {
        IrLet let => let.Local == local && let.Value is not IrHold,
        IrBlock or IrIf => plan.Initialized.TryGetValue(local, out var initialized) && initialized.Definition == statement,
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
    static bool CreatesOwner(IrRvalue value) => value.Type.MayOwnObjects && value switch
    {
        IrUse { Operand: IrRead { Ownership: IrOwnership.Copy } } => true,
        IrUse => false,
        IrConvert { Operand: IrRead { Ownership: not IrOwnership.Copy } } => false,
        IrRef or IrAddr or IrHold or IrData or IrTest => false,
        IrCall { Method.Symbol.ReturnRefKind: not RefKind.None } => false,
        _ => true,
    };
}
