using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Wyl.Compiler.Analysis;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.Interpret;

/// <summary>
/// Executes the IR the pipeline produced: the unoptimized program, which is the meaning, or the
/// optimized one, which the emitter receives. It is provenance-blind: a pass changes which fields
/// an instruction carries, not the instruction set, so each instruction has one rule that covers
/// everything it may hold (docs/design/ir.md §9.11).
/// </summary>
public sealed partial class Interpreter : IObjectLifetime
{
    readonly IrProgram program;
    readonly Callees callees;
    readonly StdlibSymbols stdlib;
    readonly InterpreterOptions options;
    readonly List<byte> output = [];
    readonly object outputLock = new();

    readonly Dictionary<IrLocal, Storage> statics = [];
    readonly Dictionary<IrStatic, byte> staticStates = [];
    readonly List<IrStatic> initializing = [];

    /// <summary>Standard error's lines so far: the collector's performance warnings, in the order they were reported.</summary>
    readonly List<string> standardError = [];

    long steps;
    volatile bool mainStarted;
    long started = Stopwatch.GetTimestamp();

    readonly ThreadLocal<ThreadState> current = new();

    Interpreter(IrProgram program, Callees callees, InterpreterOptions options)
    {
        this.program = program;
        this.callees = callees;
        this.options = options;
        stdlib = program.Bound.Stdlib;
    }

    /// <summary>
    /// Runs the program's <c>main</c> and returns what it did. A program that panics is aborted:
    /// no destructor runs, and the exit code is the platform's abort code. The evaluation runs on
    /// a thread with a large stack, so that a program near the call-depth budget can't overflow it.
    /// </summary>
    public static Interpretation Run(IrProgram program, Callees callees, InterpreterOptions? options = null)
    {
        if (NativeCall(program) is { } native)
            return Interpretation.Native(native);
        var interpreter = new Interpreter(program, callees, options ?? new InterpreterOptions());
        Interpretation? outcome = null;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                outcome = interpreter.Execute();
            }
            catch (WylPanic panic)
            {
                outcome = Interpretation.Panicked(interpreter.Output, panic.PanicMessage, panic.Location, interpreter.ErrorText);
            }
            catch (BudgetExceededException)
            {
                outcome = Interpretation.BudgetExceeded(interpreter.Output, interpreter.ErrorText);
            }
            catch (UnsupportedFeature unsupported)
            {
                outcome = Interpretation.Unsupported(interpreter.Output, unsupported.Message, interpreter.ErrorText);
            }
            catch (UndefinedBehavior undefined)
            {
                outcome = Interpretation.Undefined(interpreter.Output, undefined.Message, interpreter.ErrorText);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }, StackSize);
        worker.Start();
        worker.Join();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return outcome!;
    }

    const int StackSize = 64 * 1024 * 1024;

    /// <summary>
    /// Where the program first calls an extern function that the interpreter does not implement, as
    /// a message at the call, or null if it calls none. The interpreter implements the standard
    /// library's extern functions, and every other extern function is C++ code (decision 0087).
    /// </summary>
    static string? NativeCall(IrProgram program)
    {
        foreach (var function in program.Functions)
        {
            foreach (var statement in IrWalk.Descendants(function.Body))
            {
                var call = statement switch
                {
                    IrLet { Value: IrCall value } => value,
                    IrDo { Value: IrCall value } => value,
                    _ => null,
                };
                if (call?.Method.Symbol is { IsExtern: true } symbol && !Handles(RuntimeKey(symbol)))
                {
                    var at = statement.Origin?.PanicText ?? function.Symbol.Name;
                    return $"{at}: cannot interpret a call of '{symbol.Name}', which C++ code provides";
                }
            }
        }
        return null;
    }

    byte[] Output
    {
        get
        {
            lock (outputLock)
                return [.. output];
        }
    }

    /// <summary>What the program wrote to standard error, as its lines.</summary>
    string ErrorText
    {
        get
        {
            lock (outputLock)
                return standardError.Count == 0 ? "" : string.Join('\n', standardError) + "\n";
        }
    }

    /// <summary>Reports a performance warning, in a debug build, as the runtime's <c>PerformanceWarning</c> does.</summary>
    void PerformanceWarning(string message)
    {
        if (options.Release)
            return;
        lock (outputLock)
            standardError.Add("performance warning: " + message);
    }

    Interpretation Execute()
    {
        current.Value = new ThreadState();
        try
        {
            return RunMain();
        }
        catch (AbortedRun)
        {
            ThrowFailure();
            throw;
        }
        finally
        {
            EndThreads();
        }
    }

    Interpretation RunMain()
    {
        foreach (var statik in program.Statics)
        {
            statics[statik.Storage] = new OwnedStorage(new Cell(statik.Field.Type, MovedValue.Instance));
            staticStates[statik] = 0;
        }
        foreach (var statik in program.Statics)
            EnsureStatic(statik, "");
        mainStarted = true;
        var main = program.Bound.Main ?? throw new InterpreterException("the program has no 'main'");
        var function = program.Functions.Single(main) ?? throw new InterpreterException($"no lowered function for '{main.Name}'");
        var frame = new Frame(TypeMap.Empty);
        // A main(String[] args) gets the array the standard library builds, as the emitted C++ main gives it.
        if (function.Parameters is [var parameter])
        {
            var symbol = stdlib.ProgramArguments ?? throw new InterpreterException("the standard library has no 'programArguments'");
            var build = program.Functions.Single(symbol) ?? throw new InterpreterException("no lowered function for 'programArguments'");
            var arguments = Invoke(build, new Frame(TypeMap.Empty)) ?? throw new InterpreterException("'programArguments' returned nothing");
            frame.Locals[parameter] = new OwnedStorage(new Cell(parameter.Type, arguments));
        }
        var result = Invoke(function, frame);
        // The collector is destroyed at exit, which reports the cycles it never freed.
        ReportUncollected();
        int exitCode = result is IntValue code ? (int)(long)code.Bits : 0;
        return Interpretation.Completed(Output, exitCode, ErrorText);
    }

    void EnsureStatic(IrStatic statik, string location)
    {
        if (staticStates[statik] == 2)
            return;
        if (staticStates[statik] == 1)
        {
            var chain = string.Join(" -> ", initializing.Select(s => s.Name).Append(statik.Name));
            throw new WylPanic($"reading static '{statik.Name}' while its initializer is still running ({chain})", location);
        }
        staticStates[statik] = 1;
        initializing.Add(statik);
        var value = Invoke(statik.Initializer, new Frame(TypeMap.Empty))
            ?? throw new InterpreterException($"the initializer of static '{statik.Name}' returned nothing");
        initializing.RemoveAt(initializing.Count - 1);
        ((OwnedStorage)statics[statik.Storage]).Cell.Value = value;
        staticStates[statik] = 2;
    }

    void Step() => Charge(1);

    /// <summary>
    /// Spends work the budget bounds: one instruction, or one cell of storage the program asked
    /// for. A count no run could afford then ends as a budget overrun, like a timeout, rather than
    /// as the host running out of memory, which would make the answer depend on the machine.
    /// </summary>
    void Charge(long amount)
    {
        long spent = Interlocked.Add(ref steps, amount);
        if (aborting)
            throw new AbortedRun();
        if (spent > options.StepBudget)
        {
            Abort(new BudgetExceededException());
            throw new BudgetExceededException();
        }
        // A harness's time limit is checked rarely: the clock is far more expensive than a step.
        if (options.TimeLimit is { } limit && (spent & 0xFFFF) == 0 && Stopwatch.GetElapsedTime(started) > limit)
        {
            Abort(new BudgetExceededException());
            throw new BudgetExceededException();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Signals
    // ---------------------------------------------------------------------------------------------

    abstract class ControlSignal : Exception;

    sealed class BreakSignal(IrLoop loop) : ControlSignal
    {
        public IrLoop Loop { get; } = loop;
    }

    sealed class ContinueSignal(IrLoop loop) : ControlSignal
    {
        public IrLoop Loop { get; } = loop;
    }

    sealed class ReturnSignal(Value? value) : ControlSignal
    {
        public Value? Value { get; } = value;
    }
}
