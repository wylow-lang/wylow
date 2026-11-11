using Wyl.Compiler;

/// <summary>
/// The options of a program whose diagnostics alone are checked. The optimizations never report a
/// diagnostic, so leaving them off reports the same ones in a fraction of the time.
/// </summary>
static class DiagnosticsOnly
{
    public static readonly CompileOptions Options = new(Optimize: false);
}
