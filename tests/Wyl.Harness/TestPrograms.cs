namespace Wyl.Harness;

/// <summary>Programs the harnesses write themselves, rather than read from <c>tests/cases/</c>.</summary>
public static class TestPrograms
{
    /// <summary>
    /// A program that sees every name of the standard library and of its modules, as a self-test's
    /// or a matrix cell's program means to (spec 03-programs.md#programs.builtin-names: only the
    /// language's own types are seen without an import). It goes at the start of the first line, so
    /// every line the program's expectations count stays where it is, and <c>*</c> imports give way
    /// to the program's own names.
    /// </summary>
    public static string WithStd(string program) => "import std::*; import std::memory::*; import std::panic::*; import std::thread::*; import std::channel::*; " + program;
}
