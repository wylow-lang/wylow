using Wyl.Compiler.Semantics;

namespace Wyl.Generate;

/// <summary>
/// The generator's knobs and the stage-1 types. <c>nint</c>/<c>nuint</c> are platform-sized and the
/// spec reserves them for unsafe code and interop, so no generated program depends on the target's
/// width.
/// </summary>
/// <param name="Size">Statements in main's body.</param>
/// <param name="Depth">Expression construction depth; zero is a literal or a local.</param>
/// <param name="NodeBudget">Constructor nodes in one expression tree.</param>
/// <param name="LoopDepth">Nested range-for loops allowed.</param>
/// <param name="MaxHelpers">Helpers; each is declared before the functions that call it.</param>
/// <param name="PrintLines">Prints in the whole program, so a finding stays triageable.</param>
sealed record GenerateOptions(
    int Stage = 1,
    int Size = 40,
    int Depth = 5,
    int NodeBudget = 64,
    int LoopDepth = 2,
    int MaxHelpers = 3,
    int PrintLines = 150)
{
    public static readonly IReadOnlyList<WylType> Types =
    [
        .. IntegerFacts.Types,
        WylType.Bool,
        WylType.Char,
        WylType.Float,
        WylType.Double,
        WylType.String,
    ];
}
