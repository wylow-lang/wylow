using Vpl.Compiler.Analysis;
using Vpl.Compiler.CodeGen;
using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Ir;
using Vpl.Compiler.Optimize;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler;

/// <summary>
/// <see cref="Nest"/> off declares every temporary instead of nesting it into its use, and
/// <see cref="Optimize"/> off runs none of the optimizations, keeping every hold and copy lowering
/// made and every object on the heap (docs/design/ir.md); neither may change what a program does.
/// </summary>
public sealed record CompileOptions(bool Nest = true, bool Optimize = true);

public sealed record CompilationResult(IReadOnlyList<Diagnostic> Diagnostics, string? Cpp)
{
    public bool Success => Cpp is not null;

    /// <summary>Where each object that is on the stack instead of the heap is made, for tests.</summary>
    public IReadOnlyList<Location> StackObjects { get; init; } = [];

    /// <summary>Where each loop element that refers to its element in place is declared, for tests.</summary>
    public IReadOnlyList<Location> InPlaceElements { get; init; } = [];

    /// <summary>The checked program, whose symbols tests build IR with.</summary>
    public BoundProgram? Program { get; init; }

    /// <summary>The lowered program.</summary>
    public IrProgram? Ir { get; init; }

    /// <summary>The IR of the program's own functions, in declaration order, as <c>vpl ir</c> prints it.</summary>
    public string? IrText()
    {
        if (Ir is null || Program is null)
            return null;
        var lowered = Ir.Functions.Where(f => !f.IsLambda && f.Builds is null).ToDictionary(f => f.Symbol);
        var text = new System.Text.StringBuilder();
        foreach (var function in Program.Functions.Where(f => f.Symbol.Declaration?.Location.Source == Program.Source))
        {
            if (lowered.TryGetValue(function.Symbol, out var ir))
                text.Append(IrPrinter.Print(ir));
        }
        foreach (var built in Ir.Functions.Where(f => f.Builds is not null && f.Symbol.ContainingType!.Declaration.Location.Source == Program.Source))
            text.Append(IrPrinter.Print(built));
        return text.ToString();
    }
}

public static class Compilation
{
    public static CompilationResult Compile(SourceText source, CompileOptions? options = null)
    {
        options ??= new CompileOptions();
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
        var ir = IrLowering.Lower(program, analyses, options.Optimize);
        var writes = Writes.Find(new Callees(ir, analyses.Whole.Overrides));
        Verify(ir, writes, "the lowered program");
        IEnumerable<Location> inPlace = [];
        if (options.Optimize)
        {
            inPlace = InPlaceElements.Apply(ir, writes).Locations;
            Verify(ir, writes, "the program after borrowing loop elements in place");
        }
        return new CompilationResult(diagnostics.Sorted(), new CppEmitter().Emit(program, analyses, ir, options.Nest))
        {
            StackObjects = [.. analyses.StackObjects.Locations],
            InPlaceElements = [.. inPlace],
            Program = program,
            Ir = ir,
        };
    }

    /// <summary>Checks the IR after lowering and after each optimization, so that a failure names the pass that broke it.</summary>
    static void Verify(IrProgram ir, Writes writes, string what)
    {
        var errors = IrVerifier.Verify(ir, writes);
        if (errors.Count == 0)
            return;
        var rejected = ir.Functions.Where(f => !f.IsLambda && errors.Any(e => e.StartsWith(f.Name + ": ")));
        throw new InvalidOperationException($"the IR verifier rejected {what}:\n{string.Join("\n", errors)}\n\n{string.Concat(rejected.Select(IrPrinter.Print))}");
    }
}
