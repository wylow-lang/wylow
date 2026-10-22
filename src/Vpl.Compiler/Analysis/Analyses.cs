using Vpl.Compiler.Semantics;

namespace Vpl.Compiler.Analysis;

/// <summary>What the whole-program analyses find about a checked program, for the emitter.</summary>
public sealed record Analyses(WholeProgram Whole, ElementAliasing Elements, StackObjects StackObjects, CyclicClasses Cycles)
{
    public static Analyses Run(BoundProgram program)
    {
        var whole = new WholeProgram(program);
        var elements = ElementAliasing.Find(whole);
        return new Analyses(whole, elements, StackObjects.Find(whole, elements), CyclicClasses.Find(whole));
    }
}
