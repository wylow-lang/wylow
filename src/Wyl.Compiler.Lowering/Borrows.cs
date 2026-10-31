using Wyl.Compiler.Ir;

namespace Wyl.Compiler.Lowering;

/// <summary>
/// A pass that decides what the spec says a program does where lowering can't on its own, so it
/// runs under <c>--no-opt</c> too (docs/design/architecture.md). Only lowering makes one, and it
/// uses only what the language defines: this project can't reach a whole-program analysis.
/// </summary>
public sealed class MandatoryPass
{
    internal MandatoryPass(string what, Action<IrProgram> apply) => (What, Apply) = (what, apply);

    /// <summary>What the pass does, for the verifier's report of a program it broke.</summary>
    public string What { get; }

    public Action<IrProgram> Apply { get; }
}

/// <summary>
/// The spec's rule for how long a borrow lasts (03-memory-model.md, Borrow counters on
/// containers). Lowering borrows every object that may be <c>Borrowable</c> where a place goes
/// through what it hands out; this drops the borrow where no code runs from the first reference
/// into it to its last use, since nothing can then change the object's length, unless a source
/// variable or loop refers into it, whose borrow lasts for its scope.
/// </summary>
static class Borrows
{
    public static void Apply(IrProgram program)
    {
        foreach (var function in program.Functions)
        {
            var statements = IrWalk.Descendants(function.Body, intoLambdas: false).ToList();
            // An `is ref` name's hold is declared apart from where it is made, and named.
            var declared = statements.OfType<IrDeclare>().Select(d => d.Local).ToHashSet();
            foreach (var block in statements.OfType<IrBlock>())
            {
                for (int h = 0; h < block.Statements.Count; h++)
                {
                    if (block.Statements[h] is IrLet { Value: IrHold { Borrows: true } hold } let && !declared.Contains(let.Local)
                        && IrExtent.Of(block.Statements, h) is { Referenced.RunsCode: false, Named: false })
                        hold.Borrows = false;
                }
            }
        }
    }
}
