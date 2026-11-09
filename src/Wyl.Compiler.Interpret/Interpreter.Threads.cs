using System.Runtime.ExceptionServices;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;

namespace Wyl.Compiler.Interpret;

internal sealed class LogicalThread(WylType resultType)
{
    public Interpreter.ThreadState State { get; } = new();
    internal WylType ResultType { get; } = resultType;
    internal Thread? Os;
    internal ManualResetEventSlim Done { get; } = new(false);
    internal Cell? Result;
}

public sealed partial class Interpreter
{
    sealed class AbortedRun : Exception;

    readonly ManualResetEventSlim aborted = new(false);
    readonly object threadLock = new();
    readonly List<LogicalThread> logicalThreads = [];
    volatile bool aborting;
    Exception? failure;

    Exception? Failure
    {
        get
        {
            lock (threadLock)
                return failure;
        }
    }

    void Abort(Exception exception)
    {
        lock (threadLock)
            failure ??= exception;
        aborting = true;
        aborted.Set();
    }

    LogicalThread SpawnLogical(WylType resultType, Func<Value?> body, string location)
    {
        var thread = new LogicalThread(resultType);
        lock (threadLock)
        {
            if (aborting)
                throw new AbortedRun();
            thread.Os = new Thread(() => RunLogical(thread, body), StackSize);
            try
            {
                thread.Os.Start();
            }
            catch (OutOfMemoryException)
            {
                thread.Done.Dispose();
                throw new WylPanic("the system could not start a thread", location);
            }
            logicalThreads.Add(thread);
        }
        return thread;
    }

    void RunLogical(LogicalThread thread, Func<Value?> body)
    {
        current.Value = thread.State;
        try
        {
            if (body() is { } value)
                thread.Result = new Cell(thread.ResultType, value);
        }
        catch (AbortedRun)
        {
        }
        catch (Exception exception)
        {
            Abort(exception);
        }
        finally
        {
            thread.Done.Set();
        }
    }

    void WaitFor(LogicalThread thread)
    {
        WaitHandle.WaitAny([thread.Done.WaitHandle, aborted.WaitHandle]);
        if (aborting)
            throw new AbortedRun();
    }

    void EndThreads()
    {
        LogicalThread[] threads;
        lock (threadLock)
        {
            aborting = true;
            aborted.Set();
            threads = [.. logicalThreads];
        }
        foreach (var thread in threads)
            thread.Os?.Join();
        foreach (var thread in threads)
            thread.Done.Dispose();
    }

    Value? RunThreadBody(FunctionValue body, WylType bodyType)
    {
        var result = Invoke(body.Function, CallFrame(body));
        Release(bodyType, body);
        Collect(long.MaxValue, "<thread end>");
        return result;
    }

    ThreadValue SpawnThread(IrCall call, Frame caller, string? location)
    {
        var arguments = ArgumentsOf(call, caller);
        if (!mainStarted)
            throw new WylPanic("cannot start a thread before main begins", location ?? "<runtime>");
        var body = arguments[0] as FunctionValue
            ?? throw new InterpreterException("the thread's body is not a function value");
        var bodyType = caller.Map.Apply(call.Method.ParameterType(0));
        var resultType = caller.Map.Apply(call.Method.TypeArguments[0]);
        var thread = SpawnLogical(resultType, () => RunThreadBody(body, bodyType), location ?? "<runtime>");
        return new ThreadValue { Thread = thread };
    }

    Value JoinThread(IrCall call, Frame caller, string? location)
    {
        var arguments = ArgumentsOf(call, caller);
        if (arguments[0] is NullValue)
            throw new WylPanic("cannot join a thread that was never started", location ?? "<runtime>");
        RefuseWaitWhileLocked("cannot wait for a thread while this thread holds a lock", location);
        var thread = (arguments[0] as ThreadValue)?.Thread
            ?? throw new InterpreterException("joined something that is not a thread");
        WaitFor(thread);
        var result = thread.Result ?? throw new InterpreterException("joined a thread that has no result");
        thread.Result = null;
        return result.Value;
    }

    Value? ReleaseThread(IrCall call, Frame caller)
    {
        var arguments = ArgumentsOf(call, caller);
        if (arguments[0] is ThreadValue { Thread: { } thread })
        {
            RefuseWaitWhileLocked("cannot wait for a thread while this thread holds a lock", null);
            WaitFor(thread);
            if (thread.Result is { } result)
            {
                thread.Result = null;
                Release(result);
            }
        }
        return null;
    }

    void ThrowFailure()
    {
        if (Failure is { } raised)
            ExceptionDispatchInfo.Capture(raised).Throw();
        throw new AbortedRun();
    }
}
