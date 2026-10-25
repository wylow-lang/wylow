using Vpl.Compiler.Semantics;

namespace Vpl.Compiler.Analysis;

/// <summary>What the whole-program analyses find about a checked program, for the emitter.</summary>
public sealed record Analyses(WholeProgram Whole, StackObjects StackObjects, CyclicClasses Cycles)
{
    public static Analyses Run(BoundProgram program)
    {
        var whole = new WholeProgram(program);
        return new Analyses(whole, StackObjects.Find(whole), CyclicClasses.Find(whole));
    }
}
