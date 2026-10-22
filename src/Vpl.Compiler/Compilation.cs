using Vpl.Compiler.Analysis;
using Vpl.Compiler.CodeGen;
using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler;

public sealed record CompilationResult(IReadOnlyList<Diagnostic> Diagnostics, string? Cpp)
{
    public bool Success => Cpp is not null;

    /// <summary>Where each object that is on the stack instead of the heap is made, for tests.</summary>
    public IReadOnlyList<Location> StackObjects { get; init; } = [];
}

public static class Compilation
{
    public static CompilationResult Compile(SourceText source)
    {
        var diagnostics = new DiagnosticBag();
        var stdlib = Stdlib.Sources.SelectMany(s => new Parser(s, diagnostics).ParseCompilationUnit().Declarations).ToList();
        var unit = new Parser(source, diagnostics).ParseCompilationUnit();
        // Binding a broken tree mostly produces noise, so stop at syntax errors.
        if (diagnostics.HasErrors)
            return new CompilationResult(diagnostics.Sorted(), null);

        var program = new Binder(diagnostics).BindProgram(unit with { Declarations = [.. stdlib, .. unit.Declarations] });
        var flow = new FlowAnalyzer(diagnostics);
        foreach (var function in program.Functions)
            flow.Analyze(function);
        foreach (var field in program.Types.SelectMany(t => t.Fields))
        {
            if (field.Initializer is { } initializer)
                flow.Analyze(initializer);
        }
        if (diagnostics.HasErrors)
            return new CompilationResult(diagnostics.Sorted(), null);

        var analyses = Analyses.Run(program);
        return new CompilationResult(diagnostics.Sorted(), new CppEmitter().Emit(program, analyses))
        {
            StackObjects = [.. analyses.StackObjects.Locations],
        };
    }
}
