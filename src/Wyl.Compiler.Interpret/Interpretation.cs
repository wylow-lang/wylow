using System.Text;

namespace Wyl.Compiler.Interpret;

/// <summary>How interpreting a program ended.</summary>
public enum InterpretationStatus
{
    /// <summary>It ran to the end of <c>main</c>.</summary>
    Completed,
    /// <summary>It panicked, which aborts the process without unwinding.</summary>
    Panicked,
    /// <summary>It never finished within the step or call-depth budget, like a timeout: inconclusive.</summary>
    BudgetExceeded,
    /// <summary>It reached a feature a later step of the interpreter lands.</summary>
    Unsupported,
    /// <summary>
    /// It used memory outside what it owns in <c>unsafe</c> code, which nothing defines, so a
    /// differential comparison has no behavior to expect.
    /// </summary>
    UndefinedBehavior,
    /// <summary>
    /// It was not run, because it calls an extern function that only C++ code provides, and only a
    /// native build runs C++ code (decision 0087).
    /// </summary>
    Native,
}

/// <summary>What interpreting a program produced.</summary>
public sealed record Interpretation(byte[] Output, string Error, int ExitCode, InterpretationStatus Status)
{
    /// <summary>
    /// The exit code of <c>abort()</c> on this machine, which the matrix snapshots record.
    /// </summary>
    public const int AbortExitCode = 3;

    public string OutputText => Encoding.UTF8.GetString(Output);

    /// <summary>
    /// The run finished. <paramref name="before"/> is what the program already wrote to standard
    /// error, such as the collector's performance warnings.
    /// </summary>
    public static Interpretation Completed(byte[] output, int exitCode, string before = "") =>
        new(output, before, exitCode, InterpretationStatus.Completed);

    public static Interpretation Panicked(byte[] output, string message, string location, string before = "") =>
        new(output, $"{before}panic: {message}\n    at {location}\n", AbortExitCode, InterpretationStatus.Panicked);

    public static Interpretation BudgetExceeded(byte[] output, string before = "") =>
        new(output, $"{before}interpreter budget exceeded\n", 0, InterpretationStatus.BudgetExceeded);

    public static Interpretation Unsupported(byte[] output, string what, string before = "") =>
        new(output, $"{before}unsupported: {what}\n", 0, InterpretationStatus.Unsupported);

    public static Interpretation Undefined(byte[] output, string what, string before = "") =>
        new(output, $"{before}undefined behavior: {what}\n", 0, InterpretationStatus.UndefinedBehavior);

    /// <summary>The program was refused, and <paramref name="call"/> says where it calls C++ code.</summary>
    public static Interpretation Native(string call) =>
        new([], call + "\n", 0, InterpretationStatus.Native);
}

/// <summary>
/// How much interpreting may do before it is inconclusive, and whether integer overflow wraps. A
/// time limit is for a harness that must bound a mutant that loops: the suite leaves it null, since
/// one run of a program must give the same answer as another.
/// </summary>
/// <param name="Arguments">The program's arguments, which a <c>main(String[] args)</c> receives.</param>
public sealed record InterpreterOptions(
    bool Release = false, long StepBudget = 50_000_000, int CallDepthBudget = 10_000, TimeSpan? TimeLimit = null,
    IReadOnlyList<string>? Arguments = null);

/// <summary>A compiler bug the interpreter met, which is not the user's program's fault.</summary>
public sealed class InterpreterException(string message) : Exception(message);

/// <summary>An instruction or intrinsic a later step of the interpreter lands.</summary>
public sealed class UnsupportedFeature(string what) : Exception(what);

/// <summary>What <c>unsafe</c> code does when it uses memory it doesn't own, which nothing defines.</summary>
public sealed class UndefinedBehavior(string message) : Exception(message);

/// <summary>A panic: the program aborted, so nothing is released on the way out.</summary>
public sealed class WylPanic(string message, string location) : Exception(message)
{
    public string PanicMessage { get; } = message;
    public string Location { get; } = location;
}

public sealed class BudgetExceededException() : Exception("the interpreter's budget was exceeded");
