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

    readonly Dictionary<IrLocal, Storage> statics = [];
    readonly Dictionary<IrStatic, byte> staticStates = [];
    readonly List<IrStatic> initializing = [];

    /// <summary>Standard error's lines so far: the collector's performance warnings, in the order they were reported.</summary>
    readonly List<string> standardError = [];

    long steps;
    int depth;
    bool mainStarted;
    long started = Stopwatch.GetTimestamp();

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
                outcome = Interpretation.Panicked([.. interpreter.output], panic.PanicMessage, panic.Location, interpreter.ErrorText);
            }
            catch (BudgetExceededException)
            {
                outcome = Interpretation.BudgetExceeded([.. interpreter.output], interpreter.ErrorText);
            }
            catch (UnsupportedFeature unsupported)
            {
                outcome = Interpretation.Unsupported([.. interpreter.output], unsupported.Message, interpreter.ErrorText);
            }
            catch (UndefinedBehavior undefined)
            {
                outcome = Interpretation.Undefined([.. interpreter.output], undefined.Message, interpreter.ErrorText);
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

    byte[] Output => [.. output];

    /// <summary>What the program wrote to standard error, as its lines.</summary>
    string ErrorText => standardError.Count == 0 ? "" : string.Join('\n', standardError) + "\n";

    /// <summary>Reports a performance warning, in a debug build, as the runtime's <c>PerformanceWarning</c> does.</summary>
    void PerformanceWarning(string message)
    {
        if (!options.Release)
            standardError.Add("performance warning: " + message);
    }

    Interpretation Execute()
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
        steps += amount;
        if (steps > options.StepBudget)
            throw new BudgetExceededException();
        // A harness's time limit is checked rarely: the clock is far more expensive than a step.
        if (options.TimeLimit is { } limit && (steps & 0xFFFF) == 0 && Stopwatch.GetElapsedTime(started) > limit)
            throw new BudgetExceededException();
    }

    // ---------------------------------------------------------------------------------------------
    // Frames and local storage
    // ---------------------------------------------------------------------------------------------

    /// <summary>One running function: its locals, the type arguments its body runs with, and where it was called.</summary>
    internal sealed class Frame(TypeMap map)
    {
        public TypeMap Map { get; } = map;
        public Dictionary<IrLocal, Storage> Locals { get; } = [];
        public HashSet<IrLocal> Ended { get; } = [];

        /// <summary>The text an <c>@CallerLocation</c> function reports, which its caller passed.</summary>
        public string? CallerLocation { get; set; }

        /// <summary>The object a constructor's frame is making, which its base constructor call runs on.</summary>
        public ObjectRef? Constructing { get; set; }

        /// <summary>The frame whose locals a non-escaping lambda sees.</summary>
        public Frame? Enclosing { get; init; }

        public WylType Type(WylType type) => Map.Apply(type);

        public Storage Find(IrLocal local) =>
            Locals.TryGetValue(local, out var found) ? found
            : Enclosing?.Find(local) ?? throw new InterpreterException($"local '{local.Name}' is not declared");
    }

    internal abstract class Storage;

    /// <summary>A local that owns its value: a variable, parameter, or temporary.</summary>
    internal sealed class OwnedStorage(Cell cell) : Storage
    {
        public Cell Cell { get; } = cell;
    }

    /// <summary>
    /// A hold: a counted copy that pins the object, and for <c>hold.borrow</c>, also borrows it, so
    /// that a <c>Borrowable</c> knows the storage it handed out is in use.
    /// </summary>
    internal sealed class HoldStorage(Cell cell, bool borrows) : Storage
    {
        public Cell Cell { get; } = cell;
        public bool Borrows { get; set; } = borrows;
    }

    /// <summary>A reference, which aliases the cell it refers to, or null for a <c>ref?</c>.</summary>
    internal sealed class RefStorage(Cell? cell) : Storage
    {
        public Cell? Cell { get; set; } = cell;
    }

    /// <summary>
    /// A <c>wyl::StackSlot</c>: it holds the reference an object made by <c>new</c> or a closure
    /// starts with, so the object lives until the slot ends, which runs its destruction.
    /// </summary>
    internal sealed class SlotStorage : Storage
    {
        public HeapObject? Object { get; set; }
    }

    /// <summary>A guard that marks a value in use until the local ends, restoring the state it had. A declared guard holds no value until its <c>let</c> makes it.</summary>
    internal sealed class InUseStorage(Value? value, bool previous) : Storage
    {
        public Value? Value { get; } = value;
        public bool Previous { get; } = previous;
    }

    Cell LocalCell(Frame frame, IrLocal local) => (local.Role == IrLocalRole.Static ? statics[local] : frame.Find(local)) switch
    {
        OwnedStorage owned => owned.Cell,
        HoldStorage hold => hold.Cell,
        RefStorage { Cell: { } cell } => cell,
        RefStorage => throw new InterpreterException($"the null reference '{local.Name}' was used"),
        _ => throw new InterpreterException($"unexpected storage for '{local.Name}'"),
    };

    /// <summary>Releases a value, which runs an object's destructors where its last reference goes.</summary>
    void Release(WylType type, Value value) => Values.Release(type, value, this);

    void Release(Cell cell) => Release(cell.Type, cell.Value);

    /// <summary>Ends a hold: a borrow stops before the count it holds goes, as <c>wyl::Borrow</c> does.</summary>
    void EndHold(HoldStorage hold)
    {
        var value = hold.Cell.Value;
        hold.Cell.Value = MovedValue.Instance;
        if (hold.Borrows && value is ObjectRef obj)
            EndBorrow(obj);
        Release(hold.Cell.Type, value);
    }

    /// <summary>
    /// A callee's frame, whose type arguments are applied to the caller's: generic code may call a
    /// generic method with its own type parameters as the arguments.
    /// </summary>
    Frame CalleeFrame(Frame caller, TypeMap map) => new(caller.Map.Compose(map));

    /// <summary>Declares a local in the block now running, so that its value is released when the block ends.</summary>
    void Declare(Frame frame, IrLocal local, List<IrLocal> declared)
    {
        frame.Ended.Remove(local);
        declared.Add(local);
    }

    void ReleaseLocal(Frame frame, IrLocal local)
    {
        if (frame.Ended.Contains(local))
            return;
        if (frame.Locals.Remove(local, out var storage))
        {
            frame.Ended.Add(local);
            switch (storage)
            {
                case OwnedStorage owned:
                    Release(owned.Cell);
                    break;
                case HoldStorage hold:
                    EndHold(hold);
                    break;
                case SlotStorage slot:
                    EndSlot(slot);
                    break;
                case InUseStorage { Value: { } guarded } guard:
                    SetInUse(guarded, guard.Previous);
                    break;
                case InUseStorage:
                    break;
            }
        }
    }

    /// <summary>Whether a value, or one in its fields, is in use (spec <c>memory.holds</c>).</summary>
    static bool GetInUse(Value value) => value switch
    {
        StructValue structure => structure.InUse,
        EnumValue enumeration => enumeration.InUse,
        OptionalValue nullable => nullable.InUse,
        _ => false,
    };

    static void SetInUse(Value value, bool on)
    {
        switch (value)
        {
            case StructValue structure:
                structure.InUse = on;
                break;
            case EnumValue enumeration:
                enumeration.InUse = on;
                break;
            case OptionalValue nullable:
                nullable.InUse = on;
                break;
        }
    }

    /// <summary>Whether a value, or one in its fields, is in use, which a place operation checks.</summary>
    static bool AnyInUse(WylType type, Value value) => value switch
    {
        StructValue structure => structure.InUse || structure.Fields.Any(f => AnyInUse(f.Type, f.Value)),
        EnumValue enumeration => enumeration.InUse || enumeration.Fields.Any(f => AnyInUse(f.Type, f.Value)),
        OptionalValue { Inner: { } inner } => AnyInUse(inner.Type, inner.Value),
        ArrayValue array => array.Elements.Any(e => AnyInUse(e.Type, e.Value)),
        _ => false,
    };

    void EndBlock(Frame frame, List<IrLocal> declared)
    {
        for (int i = declared.Count - 1; i >= 0; i--)
            ReleaseLocal(frame, declared[i]);
    }

    // ---------------------------------------------------------------------------------------------
    // Statements
    // ---------------------------------------------------------------------------------------------

    void ExecBlock(IrBlock block, Frame frame) => ExecBlock(block, frame, 0);

    void ExecBlock(IrBlock block, Frame frame, int start)
    {
        var declared = new List<IrLocal>();
        var visitor = new StatementVisitor(this, frame, declared);
        try
        {
            for (int i = start; i < block.Statements.Count; i++)
            {
                Step();
                block.Statements[i].Accept(visitor);
            }
        }
        catch (ControlSignal)
        {
            // A break, continue, or return runs the destructors of what the block declared; a
            // panic does not, which is why its exception is not caught here.
            EndBlock(frame, declared);
            throw;
        }
        EndBlock(frame, declared);
    }

    /// <summary>
    /// Runs every statement kind. It is a visitor, so a new kind of statement does not compile
    /// until it has a rule, as the IR's own effects visitor already requires.
    /// </summary>
    sealed class StatementVisitor(Interpreter interpreter, Frame frame, List<IrLocal> declared) : IIrStatementVisitor<object?>
    {
        public object? Block(IrBlock statement) => Run(() => interpreter.ExecBlock(statement, frame));
        public object? Let(IrLet statement) => Run(() => interpreter.ExecLet(statement, frame, declared));
        public object? Declare(IrDeclare statement) => Run(() => interpreter.ExecDeclare(statement.Local, frame, declared));
        public object? Store(IrStore statement) => Run(() => interpreter.Assign(statement.Target, interpreter.EvalOperand(statement.Value, frame), frame));

        public object? Do(IrDo statement) => Run(() =>
        {
            if (interpreter.EvalRvalue(statement.Value, frame) is { } value)
                interpreter.Discard(value, statement.Value.Type, frame);
        });

        public object? Check(IrCheck statement) => Run(() => interpreter.ExecCheck(statement, frame));

        public object? If(IrIf statement) => Run(() =>
        {
            if (Values.Truth(interpreter.EvalOperand(statement.Condition, frame)))
                interpreter.ExecBlock(statement.Then, frame);
            else if (statement.Else is { } otherwise)
                interpreter.ExecBlock(otherwise, frame);
        });

        public object? Loop(IrLoop statement) => Run(() => interpreter.ExecLoop(statement, frame));
        public object? Break(IrBreak statement) => throw new BreakSignal(statement.Loop);
        public object? Continue(IrContinue statement) => throw new ContinueSignal(statement.Loop);
        public object? Return(IrReturn statement) => throw new ReturnSignal(statement.Value is { } value ? interpreter.EvalOperand(value, frame) : null);
        public object? End(IrEnd statement) => Run(() => interpreter.ReleaseLocal(frame, statement.Local));
        public object? Discard(IrDiscard statement) => Run(() => interpreter.ExecDiscard(statement, frame));

        public object? Unreachable(IrUnreachable statement) =>
            throw new WylPanic("reached the end of a non-void function", interpreter.Location(statement.Loc, frame) ?? "<unknown>");

        static object? Run(Action action)
        {
            action();
            return null;
        }
    }

    void Discard(Value value, WylType type, Frame frame) => Release(frame.Type(type), value);

    /// <summary><c>discard x</c> takes the value away, so the parameter's destructor body does not run.</summary>
    void ExecDiscard(IrDiscard discard, Frame frame)
    {
        if (LocalCell(frame, discard.Local).Value is not StructValue value)
            throw new InterpreterException($"discarded '{discard.Local.Name}', which holds no struct value");
        value.Engaged = false;
    }

    void ExecDeclare(IrLocal local, Frame frame, List<IrLocal> declared)
    {
        frame.Locals[local] = local.Kind switch
        {
            IrLocalKind.Value => new OwnedStorage(new Cell(frame.Type(local.Type), Values.Zeroed(frame.Type(local.Type), program.Bound.Stdlib))),
            IrLocalKind.Hold => new HoldStorage(new Cell(frame.Type(local.Type), Values.Zeroed(frame.Type(local.Type), program.Bound.Stdlib)), borrows: false),
            IrLocalKind.Reference => new RefStorage(null),
            IrLocalKind.Borrowed => throw new InterpreterException($"a borrowed local '{local.Name}' is declared without an alias"),
            IrLocalKind.Slot => new SlotStorage(),
            IrLocalKind.InUse => new InUseStorage(null, false),
            _ => throw new InterpreterException($"unexpected local kind {local.Kind}"),
        };
        Declare(frame, local, declared);
    }

    void ExecLet(IrLet let, Frame frame, List<IrLocal> declared)
    {
        var local = let.Local;
        if (local.Kind is IrLocalKind.Reference)
        {
            frame.Locals[local] = new RefStorage(ReferenceCell(let.Value, frame));
            if (!declared.Contains(local))
                Declare(frame, local, declared);
            return;
        }
        if (local.Kind is IrLocalKind.Borrowed)
        {
            // An in-place loop element or a narrowed nullable value's binding: an alias of a place.
            if (let.Value is not IrUse { Operand: IrRead { Ownership: IrOwnership.Borrow, Place: var place } })
                throw new InterpreterException($"a borrowed local '{local.Name}' is defined by something other than a borrow");
            frame.Locals[local] = new RefStorage(PlaceCell(place, frame));
            if (!declared.Contains(local))
                Declare(frame, local, declared);
            return;
        }
        // A slot is declared by `declare`, and filled by the `new` or closure that names it.
        if (local.Kind is IrLocalKind.Slot)
            throw new InterpreterException($"the slot '{local.Name}' is defined by a value");
        if (local.Kind is IrLocalKind.InUse)
        {
            if (let.Value is not IrInUse inUse)
                throw new InterpreterException($"the guard '{local.Name}' is defined by something other than an in-use mark");
            var guarded = PlaceCell(inUse.Place, frame).Value;
            bool declaredBefore = frame.Locals.ContainsKey(local);
            frame.Locals[local] = new InUseStorage(guarded, GetInUse(guarded));
            SetInUse(guarded, true);
            if (!declaredBefore)
                Declare(frame, local, declared);
            return;
        }

        bool borrows = let.Value is IrHold { Borrows: true };
        var value = EvalRvalue(let.Value, frame) ?? throw new InterpreterException($"'{local.Name}' is defined by a value that returned nothing");
        if (frame.Locals.TryGetValue(local, out var existing))
        {
            // A declared local (a hold, or a value out of control flow): this is its assignment.
            AssignStorage(existing, value, borrows);
            return;
        }
        if (local.Kind is IrLocalKind.Hold)
        {
            frame.Locals[local] = new HoldStorage(new Cell(frame.Type(local.Type), value), borrows);
            if (borrows && value is ObjectRef held)
                BeginBorrow(held);
        }
        else
        {
            frame.Locals[local] = new OwnedStorage(new Cell(frame.Type(local.Type), value));
        }
        Declare(frame, local, declared);
    }

    /// <summary>
    /// Stores a declared local's new value. A hold lets go of the old one first, as
    /// <c>wyl::Hold</c> does; any other local stores the new value before releasing the old one
    /// (spec memory.release).
    /// </summary>
    void AssignStorage(Storage storage, Value value, bool borrows)
    {
        if (storage is HoldStorage hold)
        {
            EndHold(hold);
            hold.Cell.Value = value;
            hold.Borrows = borrows;
            if (borrows && value is ObjectRef obj)
                BeginBorrow(obj);
            return;
        }
        var cell = storage switch
        {
            OwnedStorage owned => owned.Cell,
            _ => throw new InterpreterException("assigning a reference local's value"),
        };
        var old = cell.Value;
        cell.Value = value;
        Release(cell.Type, old);
    }

    void ExecCheck(IrCheck check, Frame frame)
    {
        switch (check.Kind)
        {
            case IrCheckKind.Narrowed or IrCheckKind.MayBeNull:
                return;
            case IrCheckKind.Index:
            {
                var index = Integer(EvalOperandInPlace(check.Operands[0], frame));
                var length = Integer(EvalOperandInPlace(check.Operands[1], frame));
                if ((Int128)(uint)(long)index >= (Int128)(uint)(long)length)
                    throw new WylPanic(RuntimeText.IndexMessage(index, length), Location(check.Loc, frame) ?? "<unknown>");
                return;
            }
            case IrCheckKind.NotNull:
                if (IsNull(EvalOperandInPlace(check.Operands[0], frame)))
                    throw new WylPanic(NullUnwrapMessage(check.Operands[0].Type), Location(check.Loc, frame) ?? "<unknown>");
                return;
            case IrCheckKind.HasValue:
                if (IsNull(EvalOperandInPlace(check.Operands[0], frame)))
                    throw new WylPanic("null value unwrapped with '!'", Location(check.Loc, frame) ?? "<unknown>");
                return;
            case IrCheckKind.NoMatch:
                throw new WylPanic("internal compiler error: no switch case matched", Location(check.Loc, frame) ?? "<unknown>");
            default:
                throw new InterpreterException($"unexpected check {check.Kind}");
        }
    }

    /// <summary>An object's null unwrap says "value" and a reference's or pointer's says "reference", as the runtime does.</summary>
    static string NullUnwrapMessage(WylType type) =>
        type.NonNull.IsReference || type.NonNull.IsFunction ? "null value unwrapped with '!'" : "null reference unwrapped with '!'";

    void ExecLoop(IrLoop loop, Frame frame)
    {
        while (true)
        {
            bool stopped = false;
            try
            {
                ExecBlock(loop.Body, frame);
            }
            catch (ContinueSignal jump) when (jump.Loop == loop)
            {
            }
            catch (BreakSignal jump) when (jump.Loop == loop)
            {
                stopped = true;
            }
            if (stopped)
                return;
            if (loop.Continue is { } next)
            {
                try
                {
                    ExecBlock(next, frame);
                }
                catch (ContinueSignal jump) when (jump.Loop == loop)
                {
                }
                catch (BreakSignal jump) when (jump.Loop == loop)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Where a panic at <paramref name="loc"/> is reported; <see cref="IrLoc.Caller"/> names the caller's text.</summary>
    string? Location(IrLoc? loc, Frame frame) => loc is null ? null : loc.Source is { } source ? source.PanicText : frame.CallerLocation;

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
