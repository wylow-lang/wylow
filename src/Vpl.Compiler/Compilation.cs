using Vpl.Compiler.Analysis;
using Vpl.Compiler.CodeGen;
using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Ir;
using Vpl.Compiler.Lowering;
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
        var text = new System.Text.StringBuilder();
        foreach (var function in Program.Functions.Where(f => f.Symbol.Declaration?.Location.Source == Program.Source))
        {
            if (Ir.Functions.Single(function.Symbol) is { } ir)
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
        // Only a program that binds is lowered, where the flow analysis finds the errors about order and flow.
        if (diagnostics.HasErrors)
            return new CompilationResult(diagnostics.Sorted(), null);

        var ir = IrLowering.Lower(program);
        if (Pipeline.Run(ir, diagnostics, options.Optimize) is not { } optimized)
            return new CompilationResult(diagnostics.Sorted(), null);
        return new CompilationResult(diagnostics.Sorted(), new CppEmitter().Emit(program, ir, optimized.Callees, optimized.Lambdas, options.Nest))
        {
            StackObjects = optimized.StackObjects,
            InPlaceElements = optimized.InPlaceElements,
            Program = program,
            Ir = ir,
        };
    }
}
