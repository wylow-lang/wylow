using Vpl.Compiler.Analysis;
using Vpl.Compiler.Ir;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Optimize;

/// <summary>
/// Puts on the stack the objects made by <c>new</c>, and the closures made by escaping lambdas,
/// that no reference can outlive the scope making them (03-memory-model.md, Stack allocation):
/// those held by a local, until the local is given another value or its block ends, and a
/// statement's temporaries, until it completes. Each gets a slot, declared where its last
/// reference would have been released, so no program can tell: before the local's declaration,
/// or at the start of the statement's block. An object escapes if a reference to it may be stored
/// anywhere but a local declared within that scope, returned, captured by a closure that escapes,
/// or given to a function that may do any of these with it (<see cref="Escapes"/>).
/// </summary>
/// <remarks>
/// A local's object is looked for only where the statement making it is in the block declaring the
/// local, so that it runs at most once in each run of the block and one slot is enough. The local
/// must not be given another value where no slot could be ended there: by a lambda, in a loop's
/// update, or through a <c>ref</c>. Any other store to it ends the slots of the objects it may have
/// held. So the object's other references must be gone by the local's next store: a local it is
/// copied to must have a scope that no such store is in, and that store must not give the local
/// the object back.
/// </remarks>
sealed class StackSlots
{
    readonly IrProgram program;
    readonly Escapes escapes;
    readonly List<(IrBlock Block, IrStatement Before, IrRvalue Creation)> locals = [];
    readonly List<(IrBlock Block, IrStatement Before, IrRvalue Creation)> temporaries = [];
    readonly Dictionary<IrStore, (List<IrStatement> In, List<IrRvalue> Ended)> ends = new(ReferenceEqualityComparer.Instance);

    StackSlots(IrProgram program, Escapes escapes)
    {
        this.program = program;
        this.escapes = escapes;
    }

    public static StackSlots Apply(IrProgram program, Escapes escapes)
    {
        var result = new StackSlots(program, escapes);
        foreach (var function in program.Functions)
        {
            // C++ computes a base constructor's arguments before the constructor's frame exists, in
            // code of their own, so a slot there would end before the base constructor runs.
            var baseCall = function.BaseCall is { } call ? IrWalk.Descendants(call).ToHashSet() : [];
            foreach (var block in IrWalk.Descendants(function.Body, intoLambdas: false).OfType<IrBlock>().Where(b => !baseCall.Contains(b)).ToList())
                result.FindIn(function, block);
        }
        result.Rewrite();
        return result;
    }

    /// <summary>Where each object on the stack is made.</summary>
    public IEnumerable<Location> Locations =>
        locals.Select(l => l.Creation).Concat(temporaries.Select(t => t.Creation)).Select(c => program.Creations[c]);

    void FindIn(IrFunction function, IrBlock block)
    {
        var statements = block.Statements;
        var held = new Dictionary<IrLocal, List<IrRvalue>>();
        var replacements = new Dictionary<IrLocal, List<Replacement>?>();
        var kept = new HashSet<IrRvalue>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < statements.Count; i++)
        {
            foreach (var (holder, declaredAt, creation) in Holding(statements, i))
            {
                if (!replacements.TryGetValue(holder, out var replaced))
                    replacements[holder] = replaced = MayHold(holder) ? Replacements(statements, declaredAt, holder) : null;
                if (replaced is null || !HoldsAlone(function, statements, i, declaredAt, holder, creation, replaced))
                    continue;
                locals.Add((block, statements[declaredAt], creation));
                kept.Add(creation);
                if (!held.TryGetValue(holder, out var creations))
                    held[holder] = creations = [];
                creations.Add(creation);
            }

            // A temporary's object is released where its block ends, which a slot declared just
            // before it is too, after everything declared later, as the temporary would be. An
            // object no temporary holds is released when its statement completes, so its slot
            // needs a block of its own.
            var made = statements[i] switch
            {
                IrLet { Local.IsTemporary: true, Value: var value } when IsCreation(value) => value,
                IrDo { Value: var value } when IsCreation(value) => value,
                _ => null,
            };
            if (made is null || kept.Contains(made))
                continue;
            var flow = escapes.Flow(function);
            int last = statements[i] is IrDo ? i : statements.Count - 1;
            for (int j = i; j <= last; j++)
                flow.Statement(statements[j]);
            if (!flow.Escapes(made) && (statements[i] is IrDo || ReleasedLast(statements, i, flow.Reachable(made))))
                temporaries.Add((block, statements[i], made));
        }

        foreach (var (holder, creations) in held)
        {
            foreach (var replacement in replacements[holder]!)
                ends[replacement.Store] = (replacement.In, [.. creations.Where(c => !ReferenceEquals(c, Created(replacement.Store.Value, replacement.In)))]);
        }
    }

    /// <summary>
    /// Statements making objects for locals of the block: the local, the index of its declaration
    /// among the statements, and the object. The statement defines the local with it, or stores
    /// it, itself or in a block or branch inside it, which runs at most once each time the block does.
    /// </summary>
    static IEnumerable<(IrLocal Holder, int Declared, IrRvalue Creation)> Holding(List<IrStatement> statements, int index)
    {
        if (statements[index] is IrLet { Local: { Symbol: not null } local } let && Created(let.Value, statements) is { } made)
            yield return (local, index, made);
        foreach (var (store, list) in Stores(statements[index], statements))
        {
            if (store.Target is { IsDeref: false, Projections.Count: 0, Local: var target } && Created(store.Value, list) is { } creation && Declaration(target) is { } declared)
                yield return (target, declared, creation);
        }

        int? Declaration(IrLocal local)
        {
            for (int i = 0; i < index; i++)
            {
                if (statements[i] is IrLet { Local: var l } && l == local || statements[i] is IrDeclare { Local: var d } && d == local)
                    return i;
            }
            return null;
        }
    }

    /// <summary>The stores a statement makes outside loops and lambdas, each with the statements it is among.</summary>
    static IEnumerable<(IrStore Store, List<IrStatement> In)> Stores(IrStatement statement, List<IrStatement> list) => statement switch
    {
        IrStore store => [(store, list)],
        IrBlock block => block.Statements.SelectMany(s => Stores(s, block.Statements)),
        IrIf conditional => [.. Stores(conditional.Then, list), .. conditional.Else is { } otherwise ? Stores(otherwise, list) : []],
        _ => [],
    };

    /// <summary>
    /// Whether the slot declared before the statement at <paramref name="from"/> ends right where
    /// the object's last reference would be released. Locals are released in reverse order, so that
    /// is the release of the first local of the block from there on holding the object to the end,
    /// one it isn't moved out of; nothing declared between the two may run code when released.
    /// </summary>
    static bool ReleasedLast(List<IrStatement> statements, int from, IEnumerable<object> reached)
    {
        var holders = reached.OfType<IrLocal>().Where(Owns).ToHashSet();
        // Only a move into another local hands the object to a later holder; any other use of a
        // value moved out of a temporary is within its statement.
        var moved = statements.Skip(from).SelectMany(s => IrWalk.Descendants(s)).Select(s => s switch
            {
                IrLet { Value: IrUse { Operand: IrRead read } } => read,
                IrStore { Value: IrRead read } => read,
                _ => null,
            })
            .Where(r => r is { Ownership: IrOwnership.Move, Place: { IsDeref: false, Projections.Count: 0 } }).Select(r => r!.Place.Local).ToHashSet();
        int last = Enumerable.Range(from, statements.Count - from).FirstOrDefault(i => Declared(statements[i]).Any(l => holders.Contains(l) && !moved.Contains(l)), -1);
        return last >= 0 && Enumerable.Range(from + 1, Math.Max(0, last - from - 1))
            .All(i => !Declared(statements[i]).Any(l => !holders.Contains(l) && IrEffects.ReleaseMayRunCode(l)));
    }

    /// <summary>The object or closure a value is, if that is all it is: made, possibly converted, and moved or copied out of a temporary of the statements.</summary>
    static IrRvalue? Created(IrRvalue value, List<IrStatement> statements) => value switch
    {
        _ when IsCreation(value) => value,
        IrConvert conversion when EscapeFlow.HoldsReference(conversion.Type) => Created(conversion.Operand, statements),
        IrUse use => Created(use.Operand, statements),
        _ => null,
    };

    static IrRvalue? Created(IrOperand operand, List<IrStatement> statements) =>
        operand is IrRead { Ownership: not IrOwnership.Borrow, Place: { IsDeref: false, Projections.Count: 0, Local: { IsTemporary: true } temporary } }
        && statements.OfType<IrLet>().FirstOrDefault(l => l.Local == temporary) is { } let
            ? Created(let.Value, statements)
            : null;

    /// <summary>Whether an rvalue allocates: a <c>new</c>, or an escaping lambda that captures something, whose closure holds the copies.</summary>
    static bool IsCreation(IrRvalue value) => value is IrNew || value is IrClosure { Captures.Count: > 0 };

    /// <summary>Whether the local is a plain variable, which a slot can be declared with, that no lambda assigns.</summary>
    static bool MayHold(IrLocal local) =>
        local is { Kind: IrLocalKind.Value, RefKind: RefKind.None, IsChangedByLambda: false } && !local.Type.IsRefStruct;

    /// <summary>A store to a local, the statements it is among, and the index of the statement of the local's block it is in.</summary>
    readonly record struct Replacement(IrStore Store, List<IrStatement> In, int Index);

    /// <summary>
    /// The stores to a local in the statements from its declaration on, or null if it may be given
    /// another value where no slot could be ended: in a loop's update, or through a <c>ref</c>.
    /// Lambdas that assign it are ruled out by <see cref="MayHold"/>.
    /// </summary>
    static List<Replacement>? Replacements(List<IrStatement> statements, int declared, IrLocal local)
    {
        bool IsLocal(IrPlace place) => place is { IsDeref: false, Projections.Count: 0 } && place.Local == local;
        var found = new List<Replacement>();
        // The statements walked are those of the list, or at the top, one statement of the local's block.
        bool Walk(List<IrStatement> list, int index, bool inUpdate, IrStatement? only = null)
        {
            foreach (var statement in only is null ? list : [only])
            {
                // A store's target is among its operands as a reference, which the store itself is not.
                var operands = statement is IrStore stored ? [stored.Value] : IrWalk.Operands(statement);
                if (operands.Any(a => a is IrRefArgument { Kind: RefKind.Ref } r && IsLocal(r.Place) || a is IrAddrArgument { Kind: RefKind.Ref } p && IsLocal(p.Place)))
                    return false;
                switch (statement)
                {
                    case IrStore store when IsLocal(store.Target):
                        if (inUpdate)
                            return false;
                        found.Add(new Replacement(store, list, index));
                        break;
                    case IrLet { Value: IrRef { Kind: RefKind.Ref } reference } when IsLocal(reference.Place):
                    case IrLet { Value: IrAddr { Kind: RefKind.Ref } address } when IsLocal(address.Place):
                        return false;
                    case IrLet { Value: IrLambda lambda } when !Walk(lambda.Function.Body.Statements, index, inUpdate):
                    case IrLet { Value: IrClosure closure } when !Walk(closure.Function.Body.Statements, index, inUpdate):
                        return false;
                    case IrBlock block when !Walk(block.Statements, index, inUpdate):
                    case IrIf conditional when !Walk(conditional.Then.Statements, index, inUpdate) || conditional.Else is { } otherwise && !Walk(otherwise.Statements, index, inUpdate):
                    case IrLoop loop when !Walk(loop.Body.Statements, index, inUpdate) || loop.Continue is { } next && !Walk(next.Statements, index, inUpdate: true):
                        return false;
                }
            }
            return true;
        }
        for (int index = declared; index < statements.Count; index++)
        {
            if (!Walk(statements, index, inUpdate: false, statements[index]))
                return null;
        }
        return found;
    }

    /// <summary>
    /// Whether the object a statement gives a local can't be referred to once its slot ends, which
    /// is where the local's block ends or the local is next stored. The code that can use it
    /// before then runs up to the first store that is a statement of the block itself, which
    /// replaces it on every path.
    /// </summary>
    bool HoldsAlone(IrFunction function, List<IrStatement> statements, int index, int declared, IrLocal holder, IrRvalue creation, List<Replacement> replacements)
    {
        int end = replacements.Where(r => r.Index > index && IsStatementOf(statements[r.Index], r.Store))
            .Select(r => r.Index).DefaultIfEmpty(statements.Count - 1).Min();
        var flow = escapes.Flow(function);
        flow.Declare(holder);
        for (int i = index; i <= end; i++)
            flow.Statement(statements[i]);
        if (flow.Escapes(creation))
            return false;

        if (!ReleasedLast(statements, declared, flow.Reachable(creation)))
            return false;
        var reached = flow.Reachable(creation).ToHashSet(ReferenceEqualityComparer.Instance);
        var later = replacements.Where(r => r.Index > index && r.Index <= end).ToList();
        if (later.Any(r => flow.Assigned(r.Store).Any(reached.Contains)))
            return false;
        reached.Remove(holder);
        for (int i = index + 1; i <= end; i++)
        {
            if (later.Any(r => r.Index == i) && HeldIn(statements[i]).Any(reached.Contains))
                return false;
            if (replacements.Any(r => r.Index >= i) && BlockScoped(statements[i]).Any(reached.Contains))
                return false;
        }
        return true;
    }

    static bool IsStatementOf(IrStatement statement, IrStore store) =>
        ReferenceEquals(statement, store) || statement is IrBlock { IsStatement: true } scope && scope.Statements.Contains(store);

    /// <summary>The locals a statement declares that hold a reference of their own, outside lambdas, whose own locals are gone once they return.</summary>
    static IEnumerable<IrLocal> HeldIn(IrStatement statement) =>
        IrWalk.Descendants(statement, intoLambdas: false).SelectMany(Declared).Where(Owns);

    /// <summary>The locals a statement declares for the rest of its block, rather than for itself.</summary>
    static IEnumerable<IrLocal> BlockScoped(IrStatement statement) => Declared(statement).Where(Owns);

    static IEnumerable<IrLocal> Declared(IrStatement statement) => statement switch
    {
        IrLet let => [let.Local],
        IrDeclare declare => [declare.Local],
        _ => [],
    };

    static bool Owns(IrLocal local) => local.Kind is IrLocalKind.Value or IrLocalKind.Hold;

    /// <summary>
    /// Declares each slot, and ends a local's slots where it is stored another value, which releases
    /// the object it held there: those made before the store, since a slot is filled at most once.
    /// </summary>
    void Rewrite()
    {
        var slots = new Dictionary<IrRvalue, IrLocal>(ReferenceEqualityComparer.Instance);
        IrLocal Slot(IrRvalue creation)
        {
            var slot = new IrLocal("s", creation.Type, IrLocalKind.Slot) { IsTemporary = true };
            switch (creation)
            {
                case IrNew made:
                    made.Slot = slot;
                    break;
                case IrClosure closure:
                    closure.Slot = slot;
                    break;
            }
            return slots[creation] = slot;
        }
        foreach (var (block, before, creation) in locals.Concat(temporaries))
        {
            var declare = new IrDeclare(Slot(creation));
            int at = block.Statements.IndexOf(before);
            if (before is IrDo)
                block.Statements[at] = new IrBlock([declare, before]) { IsStatement = true };
            else
                block.Statements.Insert(at, declare);
        }

        // Where each creation and store is, as the statements enclosing it from the function's body.
        var paths = new Dictionary<object, List<IrStatement>>(ReferenceEqualityComparer.Instance);
        void Walk(IrStatement statement, List<IrStatement> path)
        {
            List<IrStatement> here = [.. path, statement];
            paths[statement] = here;
            if (statement is IrLet { Value: var value } && IsCreation(value))
                paths[value] = here;
            foreach (var child in statement switch
            {
                IrBlock block => block.Statements,
                IrIf conditional => conditional.Else is null ? [conditional.Then] : [conditional.Then, conditional.Else],
                IrLoop loop => loop.Continue is null ? [loop.Body] : [loop.Body, loop.Continue],
                _ => [],
            })
                Walk(child, here);
        }
        foreach (var function in program.Functions)
            Walk(function.Body, []);
        foreach (var (store, (list, ended)) in ends)
        {
            var filled = ended.Where(c => MayRunBefore(paths[c], paths[store])).Select(c => (IrStatement)new IrEnd(slots[c]));
            list.InsertRange(list.IndexOf(store) + 1, filled);
        }
    }

    /// <summary>Whether the statement at the first path may have run before the one at the second: it comes earlier, and not in the other branch of an <c>if</c>.</summary>
    static bool MayRunBefore(List<IrStatement> first, List<IrStatement> second)
    {
        int common = 0;
        while (common < first.Count && common < second.Count && ReferenceEquals(first[common], second[common]))
            common++;
        if (common == 0 || common == first.Count || common == second.Count)
            return false;
        var parent = first[common - 1];
        if (parent is IrIf)
            return false;
        var siblings = parent switch
        {
            IrBlock block => block.Statements,
            IrLoop loop => loop.Continue is null ? [loop.Body] : [loop.Body, loop.Continue],
            _ => [],
        };
        return siblings.IndexOf(first[common]) < siblings.IndexOf(second[common]);
    }
}
