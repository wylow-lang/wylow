using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Wyl.Compiler.Analysis;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.Interpret;

public sealed partial class Interpreter
{
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
}
