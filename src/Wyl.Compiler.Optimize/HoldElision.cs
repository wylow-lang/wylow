using Wyl.Compiler.Analysis;
using Wyl.Compiler.Ir;

namespace Wyl.Compiler.Optimize;

/// <summary>
/// Turns a hold that doesn't borrow into an alias of its place (docs/design/ir.md §6.2) where
/// nothing in its extent (<see cref="IrExtent"/>) may replace the object where it is: a <c>mut</c>
/// field counts as replaced only where code runs that may write that field (<see cref="Writes"/>).
/// Which holds borrow is the language's rule, decided before any optimization. Inner blocks are
/// done first, so that an outer hold's extent sees the aliases its dependents have become.
/// </summary>
sealed class HoldElision(Writes writes)
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
        var stored = at > 0 && statements[at - 1] is IrDeclare { Local: var local }
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
            inner.Add(new IrLet(stored.Target.Local, made?.Value ?? new IrUse(stored.Value)) { Origin = made?.Origin ?? stored.Origin });
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
            // A generic value's hold refers to a struct in place, so it can't become an alias.
            if (hold.Borrows || aliasing.MayChange(hold.Place, IrExtent.Of(statements, h).Extent) || (let.Local.Type.IsTypeParameter && IsDereferenced(statements, h, let.Local)))
                continue;
            // An alias of a whole local is the local itself.
            if (hold.Place is { IsDeref: false, Projections.Count: 0, Local: { Kind: IrLocalKind.Value } aliased })
            {
                foreach (var place in statements.Skip(h + 1).SelectMany(IrWalk.AllPlaces).Where(p => p.Local == let.Local))
                    place.Local = aliased;
                statements.RemoveAt(h--);
                continue;
            }
            let.Value = new IrUse(IrRead.Borrow(hold.Place));
            let.Local.Kind = IrLocalKind.Borrowed;
            let.Local.Type = hold.Place.Type;
            aliasing.Add(let.Local, hold.Place);
        }
    }

    static bool IsDereferenced(List<IrStatement> statements, int from, IrLocal local) =>
        statements.Skip(from + 1).SelectMany(s => IrWalk.Descendants(s)).SelectMany(IrWalk.Operands).SelectMany(IrWalk.Places)
            .Any(p => p.Local == local && p.IsDeref);
}
