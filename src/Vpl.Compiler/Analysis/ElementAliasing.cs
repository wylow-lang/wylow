using Vpl.Compiler.Ir;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Analysis;

/// <summary>
/// Borrows the element of each for-each loop in place whose element variable nothing can store
/// over while it lives, instead of copying it, which saves a reference count increment and
/// decrement per element (docs/design/ir.md §6.3, restricted to loop elements). What a loop
/// iterates is held and borrowed until the loop ends, so the element's object can only be
/// destroyed by storing over the element: by the loop's own code, by what it runs (<see
/// cref="Writes"/>), or by a destructor. The verifier checks the same with the same summaries.
/// </summary>
public sealed class ElementAliasing
{
    readonly IrProgram program;
    readonly Writes writes;
    readonly List<Location> locations = [];

    ElementAliasing(IrProgram program, Writes writes)
    {
        this.program = program;
        this.writes = writes;
    }

    public static ElementAliasing Apply(IrProgram program, Writes writes)
    {
        var result = new ElementAliasing(program, writes);
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
                || !program.LoopElements.TryGetValue(let.Local, out var location))
                continue;
            var element = let.Local;
            if (!element.Type.IsReference || place.Type != element.Type || !Unwritten(element, statements.Skip(i + 1)))
                continue;
            let.Value = new IrUse(IrRead.Borrow(place));
            element.Kind = IrLocalKind.Borrowed;
            element.IsElement = true;
            locations.Add(location);
        }
    }

    /// <summary>Whether nothing in the statements, nor any release, may store over the element or give the variable another value.</summary>
    bool Unwritten(IrLocal element, IEnumerable<IrStatement> statements)
    {
        var type = element.Type.NonNull;
        if (writes.Of([IrRun.Release]).MayStoreOverElement(type))
            return false;
        foreach (var statement in statements)
        {
            if (IrWalk.Descendants(statement).Any(s => Changes(s, element)))
                return false;
            foreach (var inner in IrWalk.Descendants(statement, intoLambdas: false).Where(s => s is not (IrBlock or IrIf or IrLoop)))
            {
                if (writes.MayStoreOverElement(IrEffects.Of(inner), type))
                    return false;
            }
        }
        return true;
    }

    /// <summary>Whether a statement stores to the variable, or refers to it as something that may.</summary>
    static bool Changes(IrStatement statement, IrLocal local)
    {
        bool IsLocal(IrPlace place) => place.Local == local && !place.IsDeref;
        if (statement is not (IrBlock or IrIf or IrLoop) && IrEffects.Of(statement).Writes.Any(w => IsLocal(w.Place)))
            return true;
        return statement is IrLet { Value: var value } && value switch
        {
            IrRef { Kind: RefKind.Ref } reference => IsLocal(reference.Place),
            IrAddr { Kind: RefKind.Ref } address => IsLocal(address.Place),
            _ => false,
        };
    }
}
