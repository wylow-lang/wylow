using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Wyl.Compiler.Analysis;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.Interpret;

public sealed partial class Interpreter
{
    /// <summary>One running function: its locals, the type arguments its body runs with, and where it was called.</summary>
    internal sealed class Frame(TypeMap map)
    {
        public TypeMap Map { get; } = map;
        public Dictionary<IrLocal, Storage> Locals { get; } = [];
        public HashSet<IrLocal> Ended { get; } = [];

        /// <summary>The text an <c>@CallerLocation</c> function reports, which its caller passed.</summary>
        public string? CallerLocation { get; set; }

        /// <summary>The caller's holder, which a method returning a hold gives what its returned reference points into.</summary>
        public HolderStorage? Holder { get; set; }

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

    /// <summary>
    /// A <c>wyl::Holder</c>: the object that a returned reference points into, a counted copy, and
    /// borrowed if the hold the callee gave it borrows. It is empty until a callee gives it a hold.
    /// </summary>
    internal sealed class HolderStorage : Storage
    {
        public Cell? Cell { get; set; }
        public bool Borrows { get; set; }
    }

    /// <summary>
    /// Gives the holder what a hold holds, if the holder is empty and the hold holds an object, as
    /// <c>wyl::Holder::Take</c> does: a counted copy, borrowed again if the hold borrows. A holder's
    /// contents move, since the function that owns it is returning. A hold of a struct in generic
    /// code is the struct's place, which the receiver holds.
    /// </summary>
    void Give(HolderStorage holder, Storage hold)
    {
        if (holder.Cell is not null)
            return;
        switch (hold)
        {
            case HolderStorage { Cell: { } given } other:
                (holder.Cell, holder.Borrows) = (given, other.Borrows);
                (other.Cell, other.Borrows) = (null, false);
                break;
            case HoldStorage { Cell: { Value: ObjectRef obj } cell } held:
                holder.Cell = new Cell(cell.Type, Values.Copy(cell.Type, obj));
                if (held.Borrows)
                {
                    BeginBorrow(obj);
                    holder.Borrows = true;
                }
                break;
            // An elided hold refers to the object's place, or is named by the local it aliased.
            case RefStorage { Cell: { Value: ObjectRef obj } cell }:
                holder.Cell = new Cell(cell.Type, Values.Copy(cell.Type, obj));
                break;
            case OwnedStorage { Cell: { Value: ObjectRef obj } cell }:
                holder.Cell = new Cell(cell.Type, Values.Copy(cell.Type, obj));
                break;
        }
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
                case HolderStorage { Cell: { } held } holder:
                    holder.Cell = null;
                    if (holder.Borrows && held.Value is ObjectRef borrowed)
                        EndBorrow(borrowed);
                    Release(held);
                    break;
                case HolderStorage:
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
            case StructValue structure when structure.Type.Symbol!.RecordsInUse:
                structure.InUse = on;
                break;
            case EnumValue enumeration when enumeration.Type.Symbol!.RecordsInUse:
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
}
