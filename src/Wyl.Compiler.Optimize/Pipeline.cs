using Wyl.Compiler.Analysis;
using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Lowering;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Optimize;

/// <summary>
/// Where the passes put loop elements in place and objects on the stack, for tests, and the facts
/// of the program as optimized, for the emitter.
/// </summary>
public sealed record Optimized(IReadOnlyList<Location> InPlaceElements, IReadOnlyList<Location> StackObjects, Callees Callees, LambdaWrites Lambdas);

/// <summary>
/// The only code that runs passes (docs/design/architecture.md): lowering's mandatory passes
/// always, then the optimizations, which are internal to this project. Each optimization is given
/// facts found from the program as it gets it, and the program's generation changes after it, so
/// facts kept from before throw when asked. The verifier checks the program after each.
/// </summary>
public static class Pipeline
{
    /// <summary>
    /// Runs the mandatory passes and verifies the program they leave, the program's meaning, whose
    /// violations are the user's errors, then optimizes it unless <paramref name="optimize"/> is
    /// off. Returns null if there were errors.
    /// </summary>
    public static Optimized? Run(IrProgram ir, DiagnosticBag diagnostics, bool optimize)
    {
        // A program with the user's errors in it may break other rules too, so the verifier speaks
        // only once they have all run, and each must accept any lowered program.
        foreach (var pass in IrLowering.Passes)
        {
            pass.Apply(ir);
            ir.Changed();
        }
        Verify(ir, $"the program after {string.Join(", ", IrLowering.Passes.Select(p => p.What))}", diagnostics);
        if (diagnostics.HasErrors)
            return null;
        if (!optimize)
            return Emitted(ir, [], []);
        Pass(ir, "dropping post-initialization ensures", (callees, _) => StaticEnsures.Apply(ir, InitializerReach.Find(callees)));
        Pass(ir, "reading in place", (_, writes) => ReadsInPlace.Apply(ir, writes));
        Pass(ir, "eliding holds", (_, writes) => HoldElision.Apply(ir, writes));
        var inPlace = Pass(ir, "borrowing loop elements in place", (_, writes) => InPlaceElements.Apply(ir, writes)).Locations;
        Pass(ir, "borrowing locals", (_, writes) => BorrowedLocals.Apply(ir, writes));
        Pass(ir, "moving last uses", (callees, writes) => LastUseMoves.Apply(ir, callees, writes.Lambdas));
        var onStack = Pass(ir, "putting objects on the stack", (callees, writes) => StackSlots.Apply(ir, Escapes.Find(callees), writes.Lambdas)).Locations;
        return Emitted(ir, [.. inPlace], [.. onStack]);
    }

    /// <summary>
    /// The program as the emitter gets it. A narrowed or may-be-null check runs nothing: it is what
    /// the source relies on, which every verification has checked, so it is erased once none is left to run.
    /// </summary>
    static Optimized Emitted(IrProgram ir, IReadOnlyList<Location> inPlace, IReadOnlyList<Location> onStack)
    {
        foreach (var block in ir.Functions.SelectMany(f => IrWalk.Descendants(f.Body, intoLambdas: false)).OfType<IrBlock>().ToList())
            block.Statements.RemoveAll(s => s is IrCheck { NeverRuns: true });
        ir.Changed();
        var callees = new Callees(ir);
        return new Optimized(inPlace, onStack, callees, LambdaWrites.Find(callees));
    }

    static void Pass(IrProgram ir, string what, Action<Callees, Writes> pass) =>
        Pass(ir, what, (callees, writes) => { pass(callees, writes); return 0; });

    static T Pass<T>(IrProgram ir, string what, Func<Callees, Writes, T> pass)
    {
        var callees = new Callees(ir);
        var result = pass(callees, Writes.Find(callees));
        ir.Changed();
        Verify(ir, $"the program after {what}");
        return result;
    }

    /// <summary>
    /// Checks the program after lowering and after each pass, so that a failure names the pass that
    /// broke it. Lowering a program with the user's errors in it may break other rules too.
    /// </summary>
    static void Verify(IrProgram ir, string what, DiagnosticBag? diagnostics = null)
    {
        var errors = IrVerifier.Verify(ir, Writes.Find(new Callees(ir)), diagnostics);
        if (errors.Count == 0 || diagnostics?.HasErrors == true)
            return;
        var rejected = ir.Functions.Where(f => !f.IsLambda && errors.Any(e => e.StartsWith(f.Name + ": ")));
        throw new InvalidOperationException($"the IR verifier rejected {what}:\n{string.Join("\n", errors)}\n\n{string.Concat(rejected.Select(IrPrinter.Print))}");
    }
}
