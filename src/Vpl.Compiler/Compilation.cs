using Vpl.Compiler.CodeGen;
using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler;

public sealed record CompilationResult(IReadOnlyList<Diagnostic> Diagnostics, string? Cpp)
{
    public bool Success => Cpp is not null;
}

public static class Compilation
{
    public static CompilationResult Compile(SourceText source)
    {
        var diagnostics = new DiagnosticBag();
        var prelude = new Parser(Prelude.Source, diagnostics).ParseCompilationUnit();
        var unit = new Parser(source, diagnostics).ParseCompilationUnit();
        // Binding a broken tree mostly produces noise, so stop at syntax errors.
        if (diagnostics.HasErrors)
            return new CompilationResult(diagnostics.Sorted(), null);

        var program = new Binder(diagnostics).BindProgram(unit with { Declarations = [.. prelude.Declarations, .. unit.Declarations] });
        var flow = new FlowAnalyzer(diagnostics);
        foreach (var function in program.Functions)
            flow.Analyze(function);
        if (diagnostics.HasErrors)
            return new CompilationResult(diagnostics.Sorted(), null);

        return new CompilationResult(diagnostics.Sorted(), new CppEmitter().Emit(program));
    }
}
