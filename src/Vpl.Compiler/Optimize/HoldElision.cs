using Vpl.Compiler.Analysis;
using Vpl.Compiler.Ir;

namespace Vpl.Compiler.Optimize;

/// <summary>
/// Elides the holds that nothing in their extent needs (docs/design/ir.md §6.2): a borrow if no
/// code runs that could change the object's length, and the pin too, making the hold an alias of
/// its place, if nothing may replace the object where it is: a <c>mut</c> field counts as replaced
/// only where code runs that may write that field (<see cref="Writes"/>). The extent runs to the last use of the
/// hold or of anything pointing into it, including that use when it uses it while code runs, as a
/// call's receiver does. Inner blocks are done first, so that an outer hold's extent sees the
/// aliases its dependents have become.
/// </summary>
public sealed class HoldElision(Writes writes)
{
    readonly Aliasing aliasing = new(writes);

    /// <summary>Holds declared apart from where they are made, which an <c>end</c> lets go of early (an <c>is ref</c> name's).</summary>
    readonly HashSet<IrLocal> declared = [];

    public static void Apply(IrProgram program, Writes writes)
    {
        var elision = new HoldElision(writes);
        foreach (var function in program.Functions)
        {
            foreach (var statement in IrWalk.Descendants(function.Body, intoLambdas: false))
            {
                if (statement is IrDeclare declare)
                    elision.declared.Add(declare.Local);
            }
            elision.aliasing.Learn(function);
            elision.Close(function.Body);
        }
    }

    void Close(IrStatement statement)
    {
        switch (statement)
        {
            case IrBlock block:
                for (int i = 0; i < block.Statements.Count; i++)
                {
                    Close(block.Statements[i]);
                    i = Flatten(block.Statements, i);
                }
                CloseBlock(block);
                break;
            case IrIf conditional:
                Close(conditional.Then);
                if (conditional.Else is { } otherwise)
                    Close(otherwise);
                break;
            case IrLoop loop:
                Close(loop.Body);
                if (loop.Continue is { } update)
                    Close(update);
                break;
        }
    }

    /// <summary>
    /// Undoes the scope lowering gave a statement's temporaries because releasing one could be
    /// observed, now that none of them can: it joins the enclosing block, and a local it stored
    /// is a <c>let</c> again. Returns the index of the last statement it became.
    /// </summary>
    static int Flatten(List<IrStatement> statements, int at)
    {
        if (statements[at] is not IrBlock { IsStatement: true } scope)
            return at;
        var inner = scope.Statements;
        var stored = at > 0 && statements[at - 1] is IrDeclare { IsBinding: false, Local: var local }
            && inner is [.., IrStore { Target: { IsDeref: false, Projections.Count: 0 } target } store] && target.Local == local
            ? store : null;
        // Lowering made a temporary of a value that wasn't an operand already.
        var made = stored?.Value is IrRead { Ownership: IrOwnership.Move, Place: { IsDeref: false, Projections.Count: 0, Local.IsTemporary: true } moved }
            && inner is [.., IrLet let, _] && let.Local == moved.Local
            ? let : null;
        if (scope.DeclaresOwner(stored?.Target.Local, made?.Local))
            return at;
        if (stored is not null)
        {
            inner.RemoveAt(inner.Count - 1);
            if (made is not null)
                inner.RemoveAt(inner.Count - 1);
            inner.Add(new IrLet(stored.Target.Local, made?.Value ?? new IrUse(stored.Value)));
            statements.RemoveAt(--at);
        }
        statements.RemoveAt(at);
        statements.InsertRange(at, inner);
        return at + inner.Count - 1;
    }

    void CloseBlock(IrBlock finished)
    {
        var statements = finished.Statements;
        for (int h = 0; h < statements.Count; h++)
        {
            if (statements[h] is not IrLet { Value: IrHold hold } let || declared.Contains(let.Local))
                continue;
            var (extent, referenced, named) = Extent(statements, h);
            // A variable's or loop's borrow lasts for its scope (spec §3.4, Borrow counters on
            // containers), which the object's own fields can show without any code running.
            if (hold.Borrows && !referenced.RunsCode && !named)
                hold.Borrows = false;
            // A generic value's hold refers to a struct in place, so it can't become an alias.
            if (hold.Borrows || aliasing.MayChange(hold.Place, extent) || (let.Local.Type.IsTypeParameter && IsDereferenced(statements, h, let.Local)))
                continue;
            let.Value = new IrUse(IrRead.Borrow(hold.Place));
            let.Local.Kind = IrLocalKind.Borrowed;
            let.Local.Type = hold.Place.Type;
            aliasing.Add(let.Local, hold.Place);
        }
    }

    static bool IsDereferenced(List<IrStatement> statements, int from, IrLocal local) =>
        statements.Skip(from + 1).SelectMany(s => IrWalk.Descendants(s)).SelectMany(IrWalk.Operands).SelectMany(IrWalk.Places)
            .Any(p => p.Local == local && p.IsDeref);

    /// <summary>
    /// What may happen while a hold, or anything pointing into what it holds, is in use; what may
    /// happen once something points into it, which is all a temporary reference's borrow must last
    /// for; and whether a source local points into it.
    /// </summary>
    static (IrFootprint Extent, IrFootprint Referenced, bool Named) Extent(List<IrStatement> statements, int hold)
    {
        var dependents = new HashSet<IrLocal> { ((IrLet)statements[hold]).Local };
        int last = -1;
        int firstReference = -1;
        bool lastInUse = false;
        for (int i = hold + 1; i < statements.Count; i++)
        {
            foreach (var let in IrWalk.Descendants(statements[i]).OfType<IrLet>())
            {
                // A lambda's body runs where it is called, which is where it uses what it refers to.
                if (let.Value is IrLambda lambda && IrWalk.Locals(lambda.Function.Body).Any(dependents.Contains))
                    dependents.Add(let.Local);
                else if (Refers(let.Local) && IrWalk.Operands(let.Value).SelectMany(IrWalk.Places).SelectMany(IrWalk.PlaceLocals).Any(dependents.Contains))
                {
                    dependents.Add(let.Local);
                    // Inside another statement, it may be made before more of that statement runs.
                    if (firstReference < 0)
                        firstReference = ReferenceEquals(let, statements[i]) ? i : i - 1;
                }
            }
            if (IrWalk.Locals(statements[i]).Any(dependents.Contains))
            {
                last = i;
                lastInUse = InUse(statements[i], dependents);
            }
        }
        var footprint = new IrFootprint();
        var referenced = new IrFootprint();
        for (int i = hold + 1; i < last || (i == last && lastInUse); i++)
        {
            var inner = IrWalk.Footprint(statements[i]);
            footprint.Add(inner);
            // The statement making the first reference runs before the reference exists.
            if (firstReference >= 0 && i > firstReference)
                referenced.Add(inner);
        }
        return (footprint, referenced, dependents.Any(d => d.Symbol is not null));
    }

    /// <summary>Whether a local of this kind refers to storage it doesn't own.</summary>
    static bool Refers(IrLocal local) =>
        local.Kind is IrLocalKind.Reference or IrLocalKind.Borrowed || local.Type.IsRefStruct || local.Type.IsPointer || local.Type.NonNull is { IsFunction: true, IsEscaping: false };

    /// <summary>Whether the statement uses one of the locals while it runs code, rather than only reading a value out of it first.</summary>
    static bool InUse(IrStatement statement, HashSet<IrLocal> locals)
    {
        if (statement is IrBlock or IrIf or IrLoop)
            return true;
        var value = statement switch
        {
            IrLet let => let.Value,
            IrDo run => run.Value,
            _ => null,
        };
        if (value is not (IrCall or IrInvoke or IrNew or IrConstruct))
            return false;
        return IrWalk.Operands(value).Any(argument => argument switch
        {
            IrRefArgument reference => IrWalk.PlaceLocals(reference.Place).Any(locals.Contains),
            IrAddrArgument address => IrWalk.PlaceLocals(address.Place).Any(locals.Contains),
            IrRead { Ownership: IrOwnership.Borrow } read => IrWalk.PlaceLocals(read.Place).Any(locals.Contains),
            IrRead read => locals.Contains(read.Place.Local) && Refers(read.Place.Local) && read.Place is { IsDeref: false, Projections.Count: 0 },
            _ => false,
        });
    }
}
