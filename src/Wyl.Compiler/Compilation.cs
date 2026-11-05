using Wyl.Compiler.Analysis;
using Wyl.Compiler.CodeGen;
using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Interpret;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Lowering;
using Wyl.Compiler.Optimize;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler;

/// <summary>
/// <see cref="Nest"/> off declares every temporary instead of nesting it into its use, and
/// <see cref="Optimize"/> off runs none of the optimizations, keeping every hold and copy lowering
/// made and every object on the heap (docs/design/ir.md); neither may change what a program does.
/// </summary>
public sealed record CompileOptions(bool Nest = true, bool Optimize = true);

/// <summary>
/// A program lowered and run through the pipeline: its diagnostics, and the bound program, IR, and
/// facts the pipeline produced, each null if the program failed before making it.
/// </summary>
public sealed record LowerResult(IReadOnlyList<Diagnostic> Diagnostics, BoundProgram? Program, IrProgram? Ir, Optimized? Optimized)
{
    public bool Success => Optimized is not null;
}

/// <summary>What interpreting a program produced: its diagnostics, and what it did if the pipeline accepted it.</summary>
public sealed record InterpretResult(IReadOnlyList<Diagnostic> Diagnostics, Interpretation? Interpretation)
{
    public bool Success => Interpretation is not null;
}

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

    /// <summary>The IR of the program's own functions, in declaration order, as <c>wyl ir</c> prints it.</summary>
    public string? IrText()
    {
        if (Ir is null || Program is null)
            return null;
        var text = new System.Text.StringBuilder();
        foreach (var function in Program.Functions.Where(f => f.Symbol.Declaration?.Location.Source.Module.Package == Program.Package))
        {
            if (Ir.Functions.Single(function.Symbol) is { } ir)
                text.Append(IrPrinter.Print(ir));
        }
        foreach (var built in Ir.Functions.Where(f => f.Builds is not null && f.Symbol.ContainingType!.Declaration.Location.Source.Module.Package == Program.Package))
            text.Append(IrPrinter.Print(built));
        foreach (var statik in Ir.Statics.Where(s => s.Field.Owner.Declaration.Location.Source.Module.Package == Program.Package))
            text.Append(IrPrinter.Print(statik.Initializer));
        return text.ToString();
    }
}

public static class Compilation
{
    /// <summary>
    /// Lowers a program and runs the pipeline on it: the mandatory passes, the verifier, and the
    /// optimizations unless <paramref name="options"/> turns them off. The IR is the program's
    /// meaning when they are off, which the oracle interprets (docs/design/ir.md §9.11).
    /// </summary>
    public static LowerResult Lower(PackageSources sources, CompileOptions? options = null)
    {
        options ??= new CompileOptions();
        var diagnostics = new DiagnosticBag();
        diagnostics.AddRange(sources.Diagnostics);
        // Binding a broken tree mostly produces noise, so stop at syntax errors and a broken package.
        if (diagnostics.HasErrors)
            return new LowerResult(diagnostics.Sorted(), null, null, null);
        var units = Stdlib.Sources.Concat(sources.Files).Select(s => new Parser(s, diagnostics).ParseCompilationUnit()).ToList();
        if (diagnostics.HasErrors)
            return new LowerResult(diagnostics.Sorted(), null, null, null);

        var program = new Binder(diagnostics).BindProgram(sources.Package, units);
        // Only a program that binds is lowered, where the flow analysis finds the errors about order and flow.
        if (diagnostics.HasErrors)
            return new LowerResult(diagnostics.Sorted(), program, null, null);

        var ir = IrLowering.Lower(program);
        var optimized = Pipeline.Run(ir, diagnostics, options.Optimize);
        return new LowerResult(diagnostics.Sorted(), program, ir, optimized);
    }

    public static LowerResult Lower(SourceText source, CompileOptions? options = null) => Lower(PackageSources.Of(source), options);

    public static InterpretResult Interpret(SourceText source, CompileOptions? options = null, InterpreterOptions? interpreterOptions = null) =>
        Interpret(PackageSources.Of(source), options, interpreterOptions);

    /// <summary>
    /// Interprets the IR <see cref="Lower(PackageSources, CompileOptions?)"/> produces: the program's
    /// meaning when <see cref="CompileOptions.Optimize"/> is off, and the IR the emitter would get
    /// otherwise. A program the pipeline rejects runs nothing, and its diagnostics are all there is.
    /// </summary>
    public static InterpretResult Interpret(PackageSources sources, CompileOptions? options = null, InterpreterOptions? interpreterOptions = null)
    {
        options ??= new CompileOptions();
        var lowered = Lower(sources, options);
        if (lowered is not { Ir: { } ir, Optimized: { } optimized })
            return new InterpretResult(lowered.Diagnostics, null);
        return new InterpretResult(lowered.Diagnostics, Interpreter.Run(ir, optimized.Callees, interpreterOptions));
    }

    /// <summary>
    /// The C++ of a program the pipeline accepted, from the <see cref="Lower"/> result that produced
    /// it, so a caller that needs both the IR and the C++ lowers once.
    /// </summary>
    public static CompilationResult Emit(LowerResult lowered, bool nest = true)
    {
        if (lowered is not { Program: { } program, Ir: { } ir, Optimized: { } optimized })
            return new CompilationResult(lowered.Diagnostics, null);
        return new CompilationResult(lowered.Diagnostics, new CppEmitter().Emit(program, ir, optimized.Callees, optimized.Lambdas, nest))
        {
            StackObjects = optimized.StackObjects,
            InPlaceElements = optimized.InPlaceElements,
            Program = program,
            Ir = ir,
        };
    }

    public static CompilationResult Compile(SourceText source, CompileOptions? options = null) => Compile(PackageSources.Of(source), options);

    public static CompilationResult Compile(PackageSources sources, CompileOptions? options = null)
    {
        options ??= new CompileOptions();
        return Emit(Lower(sources, options), options.Nest);
    }
}
