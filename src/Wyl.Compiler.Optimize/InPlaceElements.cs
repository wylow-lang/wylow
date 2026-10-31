using Wyl.Compiler.Analysis;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Optimize;

/// <summary>
/// Borrows the element of each for-each loop in place whose element variable nothing can write
/// while it lives, instead of copying it, which saves a reference count increment and decrement,
/// or a struct's copy, per element (docs/design/ir.md §6.3, restricted to loop elements). What a
/// loop iterates is held and borrowed until the loop ends, so the element can only change by a
/// write to an element of its type or to a part of one: by the loop's own code, by what it runs
/// (<see cref="Writes"/>), or by a destructor something it releases may run. Releases at the end of the
/// element's own block come after its last use. The verifier checks the same with the same summaries.
/// </summary>
sealed class InPlaceElements
{
    readonly IrProgram program;
    readonly Writes writes;
    readonly List<Location> locations = [];

    InPlaceElements(IrProgram program, Writes writes)
    {
        this.program = program;
        this.writes = writes;
    }

    public static InPlaceElements Apply(IrProgram program, Writes writes)
    {
        var result = new InPlaceElements(program, writes);
        foreach (var function in program.Functions)
        {
            foreach (var block in IrWalk.Descendants(function.Body, intoLambdas: false).OfType<IrBlock>())
                result.FindIn(block);
        }
        return result;
    }

    /// <summary>Where each loop element that refers to its element in place is declared.</summary>
    public IEnumerable<Location> Locations => locations;

    void FindIn(IrBlock block)
    {
        var statements = block.Statements;
        for (int i = 0; i < statements.Count; i++)
        {
            if (statements[i] is not IrLet { Value: IrUse { Operand: IrRead { Ownership: IrOwnership.Copy, Place: var place } } } let
                || let.Local.LoopElement is not { } location)
                continue;
            var element = let.Local;
            if (!element.Type.BorrowSavesCopy || place.Type != element.Type || !Unwritten(element, statements.Skip(i + 1)))
                continue;
            let.Value = new IrUse(IrRead.BorrowElement(place));
            element.Kind = IrLocalKind.Borrowed;
            locations.Add(location);
        }
    }

    /// <summary>Whether nothing in the statements, nor any release, may write the element or give the variable another value.</summary>
    bool Unwritten(IrLocal element, IEnumerable<IrStatement> statements)
    {
        var type = element.Type.NonNull;
        foreach (var statement in statements)
        {
            if (IrWalk.Descendants(statement).Any(s => IrWalk.Rebinds(s, element, moves: true)))
                return false;
            if (IrWalk.Descendants(statement, intoLambdas: false).Any(s => writes.MayWrite(IrEffects.Itself(s), IrAccess.Element(type))))
                return false;
        }
        return true;
    }
}
