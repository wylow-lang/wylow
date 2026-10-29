using System.Runtime.ExceptionServices;
using Vpl.Compiler.Analysis;
using Vpl.Compiler.Ir;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Interpret;

/// <summary>
/// Executes the IR the pipeline produced: the unoptimized program, which is the meaning, or the
/// optimized one, which the emitter receives. It is provenance-blind: a pass changes which fields
/// an instruction carries, not the instruction set, so each instruction has one rule that covers
/// everything it may hold (docs/design/ir.md §9.11).
/// </summary>
public sealed partial class Interpreter
{
    readonly IrProgram program;
    readonly Callees callees;
    readonly InterpreterOptions options;
    readonly List<byte> output = [];

    long steps;
    int depth;

    Interpreter(IrProgram program, Callees callees, InterpreterOptions options)
    {
        this.program = program;
        this.callees = callees;
        this.options = options;
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
            catch (VplPanic panic)
            {
                outcome = Interpretation.Panicked([.. interpreter.output], panic.PanicMessage, panic.Location);
            }
            catch (BudgetExceededException)
            {
                outcome = Interpretation.BudgetExceeded([.. interpreter.output]);
            }
            catch (UnsupportedFeature unsupported)
            {
                outcome = Interpretation.Unsupported([.. interpreter.output], unsupported.Message);
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

    Interpretation Execute()
    {
        var main = program.Bound.Main ?? throw new InterpreterException("the program has no 'main'");
        var function = program.Functions.Single(main) ?? throw new InterpreterException($"no lowered function for '{main.Name}'");
        var frame = new Frame(TypeMap.Empty);
        var result = Invoke(function, frame);
        int exitCode = result is IntValue code ? (int)(long)code.Bits : 0;
        return Interpretation.Completed(Output, exitCode);
    }

    void Step()
    {
        if (++steps > options.StepBudget)
            throw new BudgetExceededException();
    }

    // ---------------------------------------------------------------------------------------------
    // Frames and local storage
    // ---------------------------------------------------------------------------------------------

    /// <summary>One running function: its locals, the type arguments its body runs with, and where it was called.</summary>
    sealed class Frame(TypeMap map)
    {
        public TypeMap Map { get; } = map;
        public Dictionary<IrLocal, Storage> Locals { get; } = [];
        public HashSet<IrLocal> Ended { get; } = [];

        /// <summary>The text an <c>@CallerLocation</c> function reports, which its caller passed.</summary>
        public string? CallerLocation { get; set; }

        /// <summary>The frame whose locals a non-escaping lambda sees, once step 3 lands.</summary>
        public Frame? Enclosing { get; init; }

        public VplType Type(VplType type) => Map.Apply(type);

        public Storage Find(IrLocal local) =>
            Locals.TryGetValue(local, out var found) ? found
            : Enclosing?.Find(local) ?? throw new InterpreterException($"local '{local.Name}' is not declared");
    }

    abstract class Storage;

    /// <summary>A local that owns its value: a variable, parameter, or temporary.</summary>
    sealed class OwnedStorage(Cell cell) : Storage
    {
        public Cell Cell { get; } = cell;
    }

    /// <summary>A reference, which aliases the cell it refers to, or null for a <c>ref?</c>.</summary>
    sealed class RefStorage(Cell? cell) : Storage
    {
        public Cell? Cell { get; set; } = cell;
    }

    Cell LocalCell(Frame frame, IrLocal local) => frame.Find(local) switch
    {
        OwnedStorage owned => owned.Cell,
        RefStorage { Cell: { } cell } => cell,
        RefStorage => throw new InterpreterException($"the null reference '{local.Name}' was used"),
        _ => throw new InterpreterException($"unexpected storage for '{local.Name}'"),
    };

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
            if (storage is OwnedStorage owned)
                Values.Release(owned.Cell.Type, owned.Cell.Value);
        }
    }

    void EndBlock(Frame frame, List<IrLocal> declared)
    {
        for (int i = declared.Count - 1; i >= 0; i--)
            ReleaseLocal(frame, declared[i]);
    }

    // ---------------------------------------------------------------------------------------------
    // Statements
    // ---------------------------------------------------------------------------------------------

    void ExecBlock(IrBlock block, Frame frame)
    {
        var declared = new List<IrLocal>();
        var visitor = new StatementVisitor(this, frame, declared);
        try
        {
            foreach (var statement in block.Statements)
            {
                Step();
                statement.Accept(visitor);
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

        public object? Unreachable(IrUnreachable statement) =>
            throw new VplPanic("reached the end of a non-void function", interpreter.Location(statement.Loc, frame) ?? "<unknown>");

        static object? Run(Action action)
        {
            action();
            return null;
        }
    }

    void Discard(Value value, VplType type, Frame frame) => Values.Release(frame.Type(type), value);

    void ExecDeclare(IrLocal local, Frame frame, List<IrLocal> declared)
    {
        frame.Locals[local] = local.Kind switch
        {
            IrLocalKind.Value or IrLocalKind.Hold => new OwnedStorage(new Cell(frame.Type(local.Type), Values.Zeroed(frame.Type(local.Type)))),
            IrLocalKind.Reference => new RefStorage(null),
            IrLocalKind.Borrowed => throw new InterpreterException($"a borrowed local '{local.Name}' is declared without an alias"),
            IrLocalKind.Slot => throw new UnsupportedFeature("stack slots land in step 5"),
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
        if (local.Kind is IrLocalKind.Slot)
            throw new UnsupportedFeature("stack slots land in step 5");

        var value = EvalRvalue(let.Value, frame) ?? throw new InterpreterException($"'{local.Name}' is defined by a value that returned nothing");
        if (frame.Locals.TryGetValue(local, out var existing))
        {
            // A declared local (a hold, or a value out of control flow): this is its assignment.
            AssignStorage(existing, value);
            return;
        }
        frame.Locals[local] = new OwnedStorage(new Cell(frame.Type(local.Type), value));
        Declare(frame, local, declared);
    }

    static void AssignStorage(Storage storage, Value value)
    {
        var cell = storage switch
        {
            OwnedStorage owned => owned.Cell,
            _ => throw new InterpreterException("assigning a reference local's value"),
        };
        var old = cell.Value;
        cell.Value = value;
        Values.Release(cell.Type, old);
    }

    void ExecCheck(IrCheck check, Frame frame)
    {
        switch (check.Kind)
        {
            case IrCheckKind.Narrowed:
                return;
            case IrCheckKind.Index:
            {
                var index = Integer(EvalOperand(check.Operands[0], frame));
                var length = Integer(EvalOperand(check.Operands[1], frame));
                if ((Int128)(uint)(long)index >= (Int128)(uint)(long)length)
                    throw new VplPanic(RuntimeText.IndexMessage(index, length), Location(check.Loc, frame) ?? "<unknown>");
                return;
            }
            case IrCheckKind.NotNull:
                if (EvalOperand(check.Operands[0], frame) is NullValue or RefValue { Cell: null })
                    throw new VplPanic(NullUnwrapMessage(check.Operands[0].Type), Location(check.Loc, frame) ?? "<unknown>");
                return;
            case IrCheckKind.HasValue:
                if (EvalOperand(check.Operands[0], frame) is not OptionalValue { Inner: not null })
                    throw new VplPanic("null value unwrapped with '!'", Location(check.Loc, frame) ?? "<unknown>");
                return;
            case IrCheckKind.NoMatch:
                throw new VplPanic("internal compiler error: no switch case matched", Location(check.Loc, frame) ?? "<unknown>");
            default:
                throw new InterpreterException($"unexpected check {check.Kind}");
        }
    }

    /// <summary>An object's null unwrap says "value" and a reference's or pointer's says "reference", as the runtime does.</summary>
    static string NullUnwrapMessage(VplType type) =>
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
    // Calls
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs a call. A method, constructor, or extern is found from the IR's one index, and a
    /// dispatched call's implementation from the analysis's answer.
    /// </summary>
    Value? Call(IrCall call, Frame caller)
    {
        var method = call.Method;
        if (method.Symbol.IsBuiltin)
            return CallIntrinsic(call, caller);

        var callee = Resolve(method, call.Receiver);
        var function = program.Functions.Single(callee.Symbol)
            ?? throw new UnsupportedFeature($"no lowered function for '{callee.Symbol.Signature}'");
        var frame = new Frame(callee.Map);
        if (function.HasLocationParameter)
            frame.CallerLocation = Location(call.Loc, caller);
        if (function.This is { } self)
            frame.Locals[self] = ReceiverStorage(call.Receiver, function, caller);
        BindArguments(call.Arguments, function.Parameters, frame, caller);
        return Invoke(function, frame);
    }

    /// <summary>Which method a call runs: the one named, or for a dispatched call the receiver's implementation.</summary>
    MethodReference Resolve(MethodReference method, IrArgument? receiver)
    {
        if (!method.Symbol.IsDispatched || receiver is not IrRead read || !read.Type.NonNull.IsReference)
            return method;
        var runtime = read.Type.NonNull;
        return callees.Overrides.Implementation(method, runtime) ?? method;
    }

    Storage ReceiverStorage(IrArgument? receiver, IrFunction function, Frame caller)
    {
        switch (receiver)
        {
            case IrRefArgument reference:
                return new RefStorage(PlaceCell(reference.Place, caller));
            case IrRead read when read.Type.NonNull.IsReference:
                return new OwnedStorage(new Cell(read.Type, EvalOperand(read, caller)));
            case IrRead read:
                return new OwnedStorage(new Cell(read.Type, EvalOperand(read, caller)));
            case null:
                throw new InterpreterException($"no receiver for '{function.Name}'");
            default:
                throw new InterpreterException($"unexpected receiver {receiver.GetType().Name}");
        }
    }

    /// <summary>Binds a call's arguments in order, skipping the fields a memberwise form defaults.</summary>
    void BindArguments(IReadOnlyList<IrArgument> arguments, IReadOnlyList<IrLocal> parameters, Frame frame, Frame caller)
    {
        var given = arguments.Where(a => a is not IrFieldDefault).ToList();
        if (given.Count != parameters.Count)
            throw new InterpreterException($"a call gave {given.Count} arguments to {parameters.Count} parameters");
        for (int i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            var storage = given[i] switch
            {
                IrRefArgument reference => (Storage)new RefStorage(PlaceCell(reference.Place, caller)),
                IrAddrArgument address => new RefStorage(PlaceCell(address.Place, caller)),
                IrRead { Type.IsNullableReference: true } read => new RefStorage((EvalOperand(read, caller) as RefValue)?.Cell),
                IrConstant { Type.IsNullableReference: true } => new RefStorage(null),
                var operand => new OwnedStorage(new Cell(frame.Type(parameter.Type), EvalOwnedArgument(operand, caller))),
            };
            frame.Locals[parameter] = storage;
        }
    }

    /// <summary>Runs a function's body and releases what it owns when it returns.</summary>
    Value? Invoke(IrFunction function, Frame frame)
    {
        if (++depth > options.CallDepthBudget)
            throw new BudgetExceededException();
        try
        {
            Value? result = null;
            try
            {
                ExecBlock(function.Body, frame);
            }
            catch (ReturnSignal returned)
            {
                result = returned.Value;
            }
            // By-value parameters are released after the body's own locals, the last first.
            for (int i = function.Parameters.Count - 1; i >= 0; i--)
            {
                if (frame.Locals.TryGetValue(function.Parameters[i], out var storage) && storage is OwnedStorage owned)
                    Values.Release(owned.Cell.Type, owned.Cell.Value);
            }
            return result;
        }
        finally
        {
            depth--;
        }
    }

    /// <summary>Makes the type's default value: its <c>make_default</c> where it has code, and zeroed storage otherwise.</summary>
    Value DefaultValue(VplType type, Frame frame)
    {
        type = frame.Type(type);
        if (type.IsNullableValue)
            return new OptionalValue(null);
        // Each element's default is made by its own type's rule, which may run its initializers.
        if (type is FixedArrayType array)
            return new ArrayValue(type, [.. Enumerable.Range(0, array.Length).Select(_ => new Cell(array.Element, DefaultValue(array.Element, frame)))]);
        if (type.IsStruct && type.HasDefaultCode)
        {
            var build = program.Functions.Default(type.Symbol!) ?? throw new InterpreterException($"no make_default for '{type}'");
            var frameFor = new Frame(TypeMap.For(type));
            return Invoke(build, frameFor) ?? throw new InterpreterException($"'{build.Name}' returned nothing");
        }
        if (type.IsEnum && type.Symbol!.Variants.Count > 0)
            return new EnumValue(type, type.Symbol.Variants[0], []);
        return Values.Zeroed(type);
    }

    /// <summary>Makes a struct value with one of its constructors, including its implicit and memberwise forms.</summary>
    Value Construct(IrConstruct construction, Frame caller)
    {
        var type = caller.Type(construction.Type);
        var method = construction.Constructor;
        if (method.Symbol.IsImplicit)
            return DefaultValue(type, caller);
        var arguments = construction.Arguments;
        if (method.Symbol.IsMemberwise)
        {
            var form = arguments.Select(a => a is not IrFieldDefault).ToList();
            var build = program.Functions.Memberwise(method.Symbol, form)
                ?? throw new InterpreterException($"no memberwise form of '{method.Symbol.Name}'");
            var frame = new Frame(TypeMap.For(type));
            BindArguments(arguments, build.Parameters, frame, caller);
            return Invoke(build, frame) ?? throw new InterpreterException($"'{build.Name}' returned nothing");
        }
        var function = program.Functions.Single(method.Symbol) ?? throw new UnsupportedFeature($"no lowered function for '{method.Symbol.Signature}'");
        var body = new Frame(method.Map);
        if (function.HasLocationParameter)
            body.CallerLocation = Location(construction.Loc, caller);
        BindArguments(arguments, function.Parameters, body, caller);
        return Invoke(function, body) ?? throw new InterpreterException($"constructor '{function.Name}' returned nothing");
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
