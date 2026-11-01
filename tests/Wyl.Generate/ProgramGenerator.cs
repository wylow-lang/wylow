using Wyl.Compiler;
using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Text;

namespace Wyl.Generate;

/// <summary>One generated program and what it exercised, for the coverage self-check.</summary>
sealed record GeneratedProgram(string Text, int Seed, int Index, IReadOnlyList<string> Shapes, IReadOnlyList<WylType> Types);

/// <summary>
/// Builds a whole program: zero to three helpers, each calling only earlier ones, then main. The RNG
/// is derived from the run's seed and the program's index, so one program can be regenerated alone.
/// </summary>
sealed class ProgramGenerator(GenerateOptions options)
{
    public GeneratedProgram Generate(int seed, int index)
    {
        var rng = Rng.Derive(seed, index);
        var shapes = new List<string>();
        var types = new List<WylType>();
        var helpers = new List<Helper>();
        for (int i = 0, count = rng.Next(options.MaxHelpers + 1); i < count; i++)
        {
            var parameters = new List<WylType>();
            for (int p = 0, countParameters = rng.Next(3); p < countParameters; p++)
                parameters.Add(rng.Pick(GenerateOptions.Types));
            helpers.Add(new Helper($"f{i}", rng.Pick(GenerateOptions.Types), parameters));
        }

        var writer = new SourceWriter();
        // Its programs only print, of the standard library's names (spec 06-syntax.md §6.1, Imports).
        writer.Line("import std::print;");
        writer.Line("");
        var budget = new PrintBudget(options.PrintLines);
        for (int i = 0; i < helpers.Count; i++)
        {
            var scope = new Scope();
            for (int p = 0; p < helpers[i].Parameters.Count; p++)
                scope.Declare($"a{p}", helpers[i].Parameters[p]);
            var earlier = helpers[..i];
            var expressions = new ExpressionGenerator(rng, options, scope, earlier, shapes, types);
            var statements = new StatementGenerator(rng, options, writer, budget, earlier);
            statements.WriteHelper(helpers[i], scope, expressions);
            writer.Blank();
        }

        var mainScope = new Scope();
        var mainExpressions = new ExpressionGenerator(rng, options, mainScope, helpers, shapes, types);
        var mainStatements = new StatementGenerator(rng, options, writer, budget, helpers);
        mainStatements.WriteMain(mainScope, mainExpressions);
        return new GeneratedProgram(writer.ToString(), seed, index, shapes, types);
    }

    /// <summary>Why a program would not bind. A non-empty list is a generator bug, never a compiler finding.</summary>
    public IReadOnlyList<(int Line, int Column, string Message)> Validate(string text)
    {
        var lowered = Compilation.Lower(new SourceText("<generate>", text), new CompileOptions(Nest: true, Optimize: false));
        return
        [
            .. lowered.Diagnostics
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic => (diagnostic.Location.LineColumn.Line, diagnostic.Location.LineColumn.Column, diagnostic.Message)),
        ];
    }
}
