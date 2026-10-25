using Vpl.Compiler.Analysis;
using Vpl.Compiler.CodeGen;
using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Ir;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler;

/// <summary>
/// <see cref="Ir"/> emits the functions that lower through the mid-level IR (docs/design/ir.md), which
/// is verified. <see cref="Nest"/> and <see cref="Elide"/> off keep every temporary declared and
/// every hold and copy lowering made, which must not change what a program does.
/// </summary>
public sealed record CompileOptions(bool Ir = false, bool Nest = true, bool Elide = true);

public sealed record CompilationResult(IReadOnlyList<Diagnostic> Diagnostics, string? Cpp)
{
    public bool Success => Cpp is not null;

    /// <summary>Where each object that is on the stack instead of the heap is made, for tests.</summary>
    public IReadOnlyList<Location> StackObjects { get; init; } = [];

    /// <summary>Where each loop element that refers to its element in place is declared, for tests.</summary>
    public IReadOnlyList<Location> InPlaceElements { get; init; } = [];

    /// <summary>The checked program, whose symbols tests build IR with.</summary>
    public BoundProgram? Program { get; init; }

    /// <summary>The lowered functions, with <see cref="CompileOptions.Ir"/>.</summary>
    public IrProgram? Ir { get; init; }

    /// <summary>The functions emitted from the bound tree because lowering doesn't handle them yet, with <see cref="CompileOptions.Ir"/>.</summary>
    public IReadOnlyList<UnloweredFunction> Unlowered { get; init; } = [];

    /// <summary>The IR of the program's own functions, in declaration order, as <c>vpl ir</c> prints it; null without <see cref="CompileOptions.Ir"/>.</summary>
    public string? IrText()
    {
        if (Ir is null || Program is null)
            return null;
        var lowered = Ir.Functions.Where(f => !f.IsLambda).ToDictionary(f => f.Symbol);
        var reasons = Unlowered.ToDictionary(u => u.Function, u => u.Reason);
        var text = new System.Text.StringBuilder();
        foreach (var function in Program.Functions.Where(f => f.Symbol.Declaration?.Location.Source == Program.Source))
        {
            if (lowered.TryGetValue(function.Symbol, out var ir))
                text.Append(IrPrinter.Print(ir));
            else if (reasons.TryGetValue(function.Symbol, out var reason))
                text.Append($"// {function.Symbol.Signature}: not lowered: {reason}\n\n");
        }
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
        IrProgram? ir = null;
        var unlowered = new List<UnloweredFunction>();
        if (options.Ir)
        {
            ir = IrLowering.Lower(program, analyses, options.Elide, unlowered);
            var errors = IrVerifier.Verify(ir);
            if (errors.Count > 0)
                throw new InvalidOperationException($"the IR verifier rejected the lowered program:\n{string.Join("\n", errors)}");
        }
        return new CompilationResult(diagnostics.Sorted(), new CppEmitter().Emit(program, analyses, ir, options.Nest))
        {
            StackObjects = [.. analyses.StackObjects.Locations],
            InPlaceElements = [.. analyses.Elements.Locations],
            Program = program,
            Ir = ir,
            Unlowered = unlowered,
        };
    }
}
