using Vpl.Compiler.Analysis;
using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Ir;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Optimize;

/// <summary>
/// Where the passes put loop elements in place and objects on the stack, for tests, and what the
/// lambdas of the program as optimized change, for the emitter.
/// </summary>
public sealed record Optimized(IReadOnlyList<Location> InPlaceElements, IReadOnlyList<Location> StackObjects, LambdaWrites Lambdas);

/// <summary>
/// The only code that runs passes (docs/design/architecture.md). Each pass is given facts found
/// from the program as the pass gets it, and the program's generation changes after it, so facts
/// kept from before throw when asked. The verifier checks the program after each.
/// </summary>
public static class Pipeline
{
    /// <summary>
    /// Verifies the lowered program, whose violations are the user's errors, then optimizes it
    /// unless <paramref name="optimize"/> is off. Returns null if there were errors.
    /// </summary>
    public static Optimized? Run(IrProgram ir, Analyses analyses, DiagnosticBag diagnostics, bool optimize)
    {
        Verify(ir, analyses, "the lowered program", diagnostics);
        if (diagnostics.HasErrors)
            return null;
        if (!optimize)
            return Emitted(ir, analyses, [], []);
        Pass(ir, analyses, "eliding holds", (_, writes) => HoldElision.Apply(ir, writes));
        var inPlace = Pass(ir, analyses, "borrowing loop elements in place", (_, writes) => InPlaceElements.Apply(ir, writes)).Locations;
        Pass(ir, analyses, "borrowing locals", (_, writes) => BorrowedLocals.Apply(ir, writes));
        Pass(ir, analyses, "moving last uses", (callees, writes) => LastUseMoves.Apply(ir, callees, analyses.Cycles, writes.Lambdas));
        var onStack = Pass(ir, analyses, "putting objects on the stack", (callees, writes) => StackSlots.Apply(ir, Escapes.Find(callees), writes.Lambdas)).Locations;
        return Emitted(ir, analyses, [.. inPlace], [.. onStack]);
    }

    /// <summary>
    /// The program as the emitter gets it. A narrowed check runs nothing: it is what the source
    /// relies on, which every verification has proved, so it is erased once none is left to run.
    /// </summary>
    static Optimized Emitted(IrProgram ir, Analyses analyses, IReadOnlyList<Location> inPlace, IReadOnlyList<Location> onStack)
    {
        foreach (var block in ir.Functions.SelectMany(f => IrWalk.Descendants(f.Body, intoLambdas: false)).OfType<IrBlock>().ToList())
            block.Statements.RemoveAll(s => s is IrCheck { Kind: IrCheckKind.Narrowed });
        ir.Changed();
        return new Optimized(inPlace, onStack, LambdaWrites.Find(Callees(ir, analyses)));
    }

    static void Pass(IrProgram ir, Analyses analyses, string what, Action<Callees, Writes> pass) =>
        Pass(ir, analyses, what, (callees, writes) => { pass(callees, writes); return 0; });

    static T Pass<T>(IrProgram ir, Analyses analyses, string what, Func<Callees, Writes, T> pass)
    {
        var callees = Callees(ir, analyses);
        var result = pass(callees, Writes.Find(callees));
        ir.Changed();
        Verify(ir, analyses, $"the program after {what}");
        return result;
    }

    static Callees Callees(IrProgram ir, Analyses analyses) => new(ir, analyses.Whole.Overrides, analyses.Cycles);

    /// <summary>
    /// Checks the program after lowering and after each pass, so that a failure names the pass that
    /// broke it. Lowering a program with the user's errors in it may break other rules too.
    /// </summary>
    static void Verify(IrProgram ir, Analyses analyses, string what, DiagnosticBag? diagnostics = null)
    {
        var errors = IrVerifier.Verify(ir, Writes.Find(Callees(ir, analyses)), diagnostics);
        if (errors.Count == 0 || diagnostics?.HasErrors == true)
            return;
        var rejected = ir.Functions.Where(f => !f.IsLambda && errors.Any(e => e.StartsWith(f.Name + ": ")));
        throw new InvalidOperationException($"the IR verifier rejected {what}:\n{string.Join("\n", errors)}\n\n{string.Concat(rejected.Select(IrPrinter.Print))}");
    }
}
