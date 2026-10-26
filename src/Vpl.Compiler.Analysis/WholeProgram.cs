using Vpl.Compiler.Semantics;

namespace Vpl.Compiler.Analysis;

/// <summary>
/// What the analyses still on the bound tree share: the program's lambdas and its overrides. Those
/// on the IR see the program through <see cref="Callees"/>, where each function is one unit of code.
/// </summary>
public sealed class WholeProgram
{
    public WholeProgram(BoundProgram program)
    {
        Program = program;
        Lambdas =
        [
            .. program.Functions.SelectMany(f => BoundTree.AllExpressions(f.Body))
                .Concat(program.Types.SelectMany(t => t.Fields).Select(f => f.Initializer).OfType<BoundExpression>().SelectMany(BoundTree.AllExpressions))
                .OfType<BoundLambda>()
                .Distinct(),
        ];
        Overrides = new Overrides(program);
    }

    public BoundProgram Program { get; }

    public Overrides Overrides { get; }

    /// <summary>Every lambda in the program, including those in lambdas and field initializers.</summary>
    public IReadOnlyList<BoundLambda> Lambdas { get; }
}
