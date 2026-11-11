namespace Wyl.Harness;

/// <summary>Programs the harnesses write themselves, rather than read from <c>tests/cases/</c>.</summary>
public static class TestPrograms
{
    /// <summary>
    /// A program that sees every name of the standard library and of its modules, as a self-test's
    /// or a matrix cell's program means to (spec 03-programs.md#programs.builtin-names: only the
    /// language's own types are seen without an import). It goes at the start of the first line, so
    /// every line the program's expectations count stays where it is, and <c>*</c> imports give way
    /// to the program's own names. The programs exercise the compiler rather than show how code is
    /// written, so they call the imported functions by their bare names without the warning.
    /// </summary>
    public static string WithStd(string program) =>
        "@SuppressWarnings(\"unqualified-call\") import std::*; @SuppressWarnings(\"unqualified-call\") import std::memory::*; "
        + "@SuppressWarnings(\"unqualified-call\") import std::panic::*; @SuppressWarnings(\"unqualified-call\") import std::threading::*; "
        + "@SuppressWarnings(\"unqualified-call\") import std::iteration::*; @SuppressWarnings(\"unqualified-call\") import std::collections::*; " + program;
}
