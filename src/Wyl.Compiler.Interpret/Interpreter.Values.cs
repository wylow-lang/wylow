using System.Text;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;

namespace Wyl.Compiler.Interpret;

public sealed partial class Interpreter
{
    // ---------------------------------------------------------------------------------------------
    // Operands
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The value of an operand. A copy is a new value, a move takes the cell's value and leaves
    /// it moved-from, and a borrow is the value in place (an alias), which only a use that doesn't
    /// take ownership may read.
    /// </summary>
    Value EvalOperand(IrOperand operand, Frame frame)
    {
        Step();
        return operand switch
        {
            IrConstant constant => ConstantValue(constant, frame),
            IrRead read => EvalRead(read, frame),
            _ => throw new InterpreterException($"unexpected operand {operand.GetType().Name}"),
        };
    }

    Value EvalRead(IrRead read, Frame frame) => EvalRead(read, frame, read.Place.Type);

    /// <summary>
    /// A read copied as <paramref name="type"/> holds it, which is the parameter's type when the
    /// value is an argument: a function value that an escaping type holds is a count only there,
    /// so a non-escaping parameter refers to it without one, as <c>wyl::FunctionRef</c> does.
    /// </summary>
    Value EvalRead(IrRead read, Frame frame, WylType type)
    {
        var place = read.Place;
        // A reference local's own value is the reference it holds, not what it refers to.
        if (IsReferenceLocal(place))
            return ReferenceValue(place, frame);
        var cell = PlaceCell(place, frame);
        // The type the copy holds is applied: which form of a function value counts is asked of
        // the concrete type, not of a type parameter standing for it.
        type = frame.Type(type);
        return read.Ownership switch
        {
            IrOwnership.Copy => Values.Copy(type, cell.Value),
            IrOwnership.Move => Take(cell),
            _ => cell.Value,
        };
    }

    static bool IsReferenceLocal(IrPlace place) =>
        !place.IsDeref && place.Projections.Count == 0 && place.Local.Kind == IrLocalKind.Reference;

    Value ReferenceValue(IrPlace place, Frame frame) =>
        new RefValue(frame.Find(place.Local) is RefStorage storage ? storage.Cell : null);

    static Value Take(Cell cell)
    {
        var value = cell.Value;
        cell.Value = MovedValue.Instance;
        return value;
    }

    /// <summary>
    /// A literal. <c>null</c> of a nullable is what the type it is used as says: a nullable value
    /// holds nothing, while a nullable reference is a null reference and a pointer is a null
    /// pointer. A type parameter's <c>T?</c> may be either, so the frame's type arguments decide.
    /// </summary>
    static Value ConstantValue(IrConstant constant, Frame frame)
    {
        var type = frame.Type(constant.Type);
        if (constant.Value is null)
        {
            if (type.IsNullableValue)
                return new OptionalValue(null);
            if (type.IsNullableReference)
                return new RefValue(null);
            return NullValue.Instance;
        }
        return constant.Value switch
        {
            sbyte v => new IntValue(v),
            byte v => new IntValue(v),
            short v => new IntValue(v),
            ushort v => new IntValue(v),
            int v => new IntValue(v),
            uint v => new IntValue(v),
            long v => new IntValue(v),
            ulong v => new IntValue(v),
            nint v => new IntValue((long)v),
            nuint v => new IntValue((ulong)v),
            Rune rune => new CharValue(rune.Value),
            float f => new FloatValue(f),
            double d => new FloatValue(d),
            bool b => new BoolValue(b),
            string s => StringValue.Of(s),
            _ => throw new InterpreterException($"unexpected literal {constant.Value.GetType().Name}"),
        };
    }

    /// <summary>
    /// An argument the callee owns, copied as the parameter's type holds it: what a function value
    /// costs to pass is the parameter's form, not the form of the place it came from. A borrow of a
    /// place is copied, so the callee can't alias it.
    /// </summary>
    Value EvalArgument(IrArgument argument, WylType type, Frame frame) => argument switch
    {
        IrRead { Ownership: IrOwnership.Borrow } read => CopyInPlace(read, frame, type),
        IrRead read => EvalRead(read, frame, type),
        IrOperand operand => EvalOperand(operand, frame),
        _ => throw new InterpreterException($"unexpected argument {argument.GetType().Name}"),
    };

    Value CopyInPlace(IrRead read, Frame frame, WylType type)
    {
        var cell = PlaceCell(read.Place, frame);
        return Values.Copy(frame.Type(type), cell.Value);
    }

    static Int128 Integer(Value value) => value is IntValue number ? number.Bits : throw new InterpreterException($"expected an integer, got {value.GetType().Name}");

    // ---------------------------------------------------------------------------------------------
    // Places
    // ---------------------------------------------------------------------------------------------

    /// <summary>The cell a place names: a local's storage, what a reference refers to, then projections.</summary>
    Cell PlaceCell(IrPlace place, Frame frame)
    {
        Cell cell;
        int start = 0;
        // `*p[i]`: the pointer's target indexed, which the emitter prints as `p[i]`, so the base
        // cell alone (which is the target) is not what the first projection indexes.
        if (place.IsDeref && place.Local.Deref == IrDeref.Pointee && place.Projections is [IrElement first, ..])
        {
            cell = PointerTargetCell(Pointer(frame, place.Local), first, frame);
            start = 1;
        }
        else
        {
            cell = BaseCell(place, frame);
        }
        for (int i = start; i < place.Projections.Count; i++)
            cell = Project(cell, place.Projections[i], frame);
        return cell;
    }

    Cell BaseCell(IrPlace place, Frame frame)
    {
        var local = place.Local;
        if (!place.IsDeref)
            return LocalCell(frame, local);
        return local.Deref switch
        {
            IrDeref.Referenced => frame.Find(local) is RefStorage { Cell: { } referenced } ? referenced : throw new InterpreterException($"dereferenced the null reference '{local.Name}'"),
            // The object itself is the value in the local; its fields are projections into the object.
            // A hold of a type parameter is the object it holds, or its own copy of a struct.
            IrDeref.Object or IrDeref.Generic => LocalCell(frame, local),
            IrDeref.Pointee => PointerTargetCell(Pointer(frame, local), null, frame),
            _ => throw new InterpreterException($"unexpected dereference of '{local.Name}'"),
        };
    }

    /// <summary>The pointer a local of pointer type holds; a null one is undefined behavior to dereference.</summary>
    PointerValue Pointer(Frame frame, IrLocal local) => Pointer(LocalCell(frame, local), local.Name);

    static PointerValue Pointer(Cell cell, string name) => cell.Value switch
    {
        PointerValue pointer => pointer,
        NullValue => throw new UndefinedBehavior($"dereferenced the null pointer '{name}'"),
        var other => throw new InterpreterException($"'{name}' holds a {other.GetType().Name}, not a pointer"),
    };

    /// <summary>
    /// The cell at a pointer's target, or the one <paramref name="element"/> indexes from it, which
    /// is uninitialized storage until a value is written there.
    /// </summary>
    Cell PointerTargetCell(PointerValue pointer, IrElement? element, Frame frame) =>
        ElementCell(pointer, element is null ? 0 : (int)(long)Integer(EvalOperand(element.Index, frame)));

    static Cell ElementCell(PointerValue pointer, int index) =>
        pointer.Buffer.Read(pointer.Index + index) ?? throw new UndefinedBehavior("read storage that was never initialized");

    Cell Project(Cell cell, IrProjection projection, Frame frame) => projection switch
    {
        IrField field => FieldCell(cell, field.Field),
        IrElement element => ElementCell(cell, element, frame),
        IrVariantField variant => VariantFieldCell(cell, variant),
        IrAssumed => AssumedCell(cell),
        _ => throw new InterpreterException($"unexpected projection {projection.GetType().Name}"),
    };

    static Cell FieldCell(Cell cell, FieldSymbol field) => cell.Value switch
    {
        StructValue value => value.Fields[value.Type.Symbol!.Fields.IndexOf(field)],
        ObjectRef obj => obj.FieldCell(field),
        _ => throw new InterpreterException($"a field of a {cell.Value.GetType().Name}"),
    };

    Cell ElementCell(Cell cell, IrElement element, Frame frame)
    {
        var index = (int)(long)Integer(EvalOperand(element.Index, frame));
        return cell.Value switch
        {
            ArrayValue array => array.Elements[index],
            PointerValue pointer => ElementCell(pointer, index),
            _ => throw new InterpreterException($"an element of a {cell.Value.GetType().Name}"),
        };
    }

    static Cell VariantFieldCell(Cell cell, IrVariantField projection)
    {
        var value = cell.Value as EnumValue ?? throw new InterpreterException("a variant field of something that is not an enum");
        int index = 0;
        while (index < value.Variant.Fields.Count && value.Variant.Fields[index] != projection.Field)
            index++;
        return value.Fields[index];
    }

    /// <summary>
    /// <c>!</c>: the value inside a nullable value, or for a nullable reference, the reference
    /// itself, which is what <c>wyl::AssumeValue</c> hands back.
    /// </summary>
    static Cell AssumedCell(Cell cell) => cell.Value switch
    {
        OptionalValue { Inner: { } inner } => inner,
        OptionalValue or NullValue => throw new InterpreterException("a null nullable value was used as narrowed"),
        _ => cell,
    };

    /// <summary>Stores a new value, then releases the old one (spec memory.release).</summary>
    void Assign(IrPlace place, Value value, Frame frame)
    {
        // Assigning a ref? or in? local gives the local a new reference, not the place a new value.
        if (!place.IsDeref && place.Projections.Count == 0 && place.Local.Kind == IrLocalKind.Reference && place.Local.Type.IsNullableReference)
        {
            if (frame.Find(place.Local) is not RefStorage storage)
                throw new InterpreterException($"'{place.Local.Name}' is not a reference local");
            storage.Cell = (value as RefValue)?.Cell;
            return;
        }
        var cell = PlaceCell(place, frame);
        var old = cell.Value;
        cell.Value = value;
        Release(cell.Type, old);
    }

    /// <summary>The cell a reference local is bound to, from <c>ref</c>, <c>addr</c>, or a reference a call returns.</summary>
    Cell? ReferenceCell(IrRvalue value, Frame frame) => EvalRvalue(value, frame) switch
    {
        RefValue reference => reference.Cell,
        null when value.Type.IsNullableReference => null,
        var other => throw new InterpreterException($"a reference local is defined by a {other?.GetType().Name ?? "nothing"}"),
    };

    // ---------------------------------------------------------------------------------------------
    // Rvalues
    // ---------------------------------------------------------------------------------------------

    Value? EvalRvalue(IrRvalue value, Frame frame)
    {
        Step();
        return value.Accept(new RvalueVisitor(this, frame));
    }

    /// <summary>
    /// Evaluates every rvalue kind. It is a visitor, so a new kind of rvalue does not compile until
    /// it has a rule (docs/design/ir.md §9.11).
    /// </summary>
    sealed class RvalueVisitor(Interpreter interpreter, Frame frame) : IIrRvalueVisitor<Value?>
    {
        public Value? Use(IrUse value) => interpreter.EvalOperand(value.Operand, frame);
        public Value? Unary(IrUnary value) => interpreter.Unary(value, frame);
        public Value? Binary(IrBinary value) => interpreter.Binary(value, frame);
        public Value? Call(IrCall value) => interpreter.Call(value, frame);
        public Value? Construct(IrConstruct value) => interpreter.Construct(value, frame);
        public Value? Variant(IrVariant value) => interpreter.Variant(value, frame);
        public Value? Array(IrArray value) => interpreter.Array(value, frame);
        public Value? Default(IrDefault value) => interpreter.DefaultValue(value.Type, frame);
        public Value? Zeroed(IrZeroed value) => Values.Zeroed(frame.Type(value.Type), interpreter.program.Bound.Stdlib);
        public Value? Ref(IrRef value) => new RefValue(interpreter.PlaceCell(value.Place, frame));
        public Value? Addr(IrAddr value) => new RefValue(interpreter.PlaceCell(value.Place, frame));
        public Value? Test(IrTest value) => interpreter.Test(value, frame);
        public Value? Convert(IrConvert value) => interpreter.Convert(value, frame);
        public Value? Hold(IrHold value) => interpreter.Hold(value, frame);
        public Value? InUse(IrInUse value) => throw new InterpreterException("an in-use mark is made by the guard it defines");
        public Value? Invoke(IrInvoke value) => interpreter.CallFunctionValue(value, frame);
        public Value? New(IrNew value) => interpreter.New(value, frame);
        public Value? Closure(IrClosure value) => interpreter.Closure(value, frame);
        public Value? Lambda(IrLambda value) => interpreter.Lambda(value, frame);
        public Value? Choose(IrChoose value) => interpreter.Choose(value, frame);
        public Value? Data(IrData value) => interpreter.Data(value, frame);
    }

    /// <summary>
    /// An operand an operation only reads: the value where it is, which no copy the IR names makes,
    /// as C++ reads an operand an operation takes no ownership of. A move here leaves its local
    /// owning the value, which releases it when its block ends.
    /// </summary>
    Value EvalOperandInPlace(IrOperand operand, Frame frame) =>
        operand is IrRead read ? ReadInPlace(read, frame) : EvalOperand(operand, frame);

    /// <summary>The value in a place, without the copy or move its read names.</summary>
    Value ReadInPlace(IrRead read, Frame frame) =>
        IsReferenceLocal(read.Place) ? ReferenceValue(read.Place, frame) : PlaceCell(read.Place, frame).Value;

    Value Unary(IrUnary unary, Frame frame)
    {
        var operand = EvalOperandInPlace(unary.Operand, frame);
        var type = frame.Type(unary.Operand.Type);
        return unary.Operator switch
        {
            BoundUnaryOperator.Negate when unary.Loc is { } loc => RuntimeArithmetic.Negate(type, Integer(operand), options.Release, Required(loc, frame)),
            BoundUnaryOperator.Negate => new FloatValue(-((FloatValue)operand).Number),
            BoundUnaryOperator.LogicalNot => new BoolValue(!Values.Truth(operand)),
            BoundUnaryOperator.BitwiseNot => RuntimeArithmetic.Complement(type, Integer(operand)),
            _ => throw new InterpreterException($"unexpected unary operator {unary.Operator}"),
        };
    }

    Value Binary(IrBinary binary, Frame frame)
    {
        var left = EvalOperandInPlace(binary.Left, frame);
        var right = EvalOperandInPlace(binary.Right, frame);
        var operandType = frame.Type(binary.Left.Type);
        var op = binary.Operator;
        // A reference or null compares by identity.
        if (operandType.IsReference || operandType == WylType.Null)
        {
            bool same = SameReference(left, right);
            return new BoolValue(op == BoundBinaryOperator.Equal ? same : !same);
        }
        if (operandType.IsPointer)
            return PointerBinary(op, left, right);
        if (binary.Loc is { } panicAt)
        {
            var location = Required(panicAt, frame);
            var a = Integer(left);
            var b = Integer(right);
            return op switch
            {
                BoundBinaryOperator.Add => RuntimeArithmetic.Add(operandType, a, b, options.Release, location),
                BoundBinaryOperator.Subtract => RuntimeArithmetic.Subtract(operandType, a, b, options.Release, location),
                BoundBinaryOperator.Multiply => RuntimeArithmetic.Multiply(operandType, a, b, options.Release, location),
                BoundBinaryOperator.Divide => RuntimeArithmetic.Divide(operandType, a, b, location),
                BoundBinaryOperator.Remainder => RuntimeArithmetic.Remainder(operandType, a, b, location),
                _ => throw new InterpreterException($"unexpected checked operator {op}"),
            };
        }
        switch (op)
        {
            case BoundBinaryOperator.Equal:
                return new BoolValue(Values.Equal(operandType, left, right));
            case BoundBinaryOperator.NotEqual:
                return new BoolValue(!Values.Equal(operandType, left, right));
            case BoundBinaryOperator.Less:
                return new BoolValue(RuntimeArithmetic.Less(operandType, left, right));
            case BoundBinaryOperator.LessOrEqual:
                return new BoolValue(RuntimeArithmetic.LessOrEqual(operandType, left, right));
            case BoundBinaryOperator.Greater:
                return new BoolValue(RuntimeArithmetic.Greater(operandType, left, right));
            case BoundBinaryOperator.GreaterOrEqual:
                return new BoolValue(RuntimeArithmetic.GreaterOrEqual(operandType, left, right));
        }
        if (operandType.IsInteger)
        {
            var a = Integer(left);
            var b = Integer(right);
            return op switch
            {
                BoundBinaryOperator.ShiftLeft => RuntimeArithmetic.ShiftLeft(operandType, a, b),
                BoundBinaryOperator.ShiftRight => RuntimeArithmetic.ShiftRight(operandType, a, b),
                _ => RuntimeArithmetic.Bitwise(operandType, op, a, b),
            };
        }
        if (operandType == WylType.Bool)
        {
            bool a = Values.Truth(left), b = Values.Truth(right);
            return new BoolValue(op switch
            {
                BoundBinaryOperator.BitwiseAnd => a & b,
                BoundBinaryOperator.BitwiseOr => a | b,
                BoundBinaryOperator.BitwiseXor => a ^ b,
                _ => throw new InterpreterException($"unexpected bool operator {op}"),
            });
        }
        if (operandType.IsFloatingPoint)
        {
            return RuntimeArithmetic.Floating(operandType, op, ((FloatValue)left).Number, ((FloatValue)right).Number);
        }
        throw new InterpreterException($"unexpected binary operator {op} on '{operandType}'");
    }

    static bool SameReference(Value left, Value right) => (left, right) switch
    {
        (NullValue, NullValue) => true,
        (ObjectRef a, ObjectRef b) => ReferenceEquals(a, b),
        (RefValue a, RefValue b) => ReferenceEquals(a.Cell, b.Cell),
        (PointerValue a, PointerValue b) => Values.SameAddress(a, b),
        _ => false,
    };

    /// <summary>Pointers compare by address, and move by a number of elements, which is unchecked.</summary>
    static Value PointerBinary(BoundBinaryOperator op, Value left, Value right)
    {
        if (op is BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual)
        {
            bool same = SameReference(left, right);
            return new BoolValue(op == BoundBinaryOperator.Equal ? same : !same);
        }
        var pointer = left as PointerValue ?? throw new UndefinedBehavior("pointer arithmetic on a null pointer");
        int offset = (int)(long)Integer(right);
        return new PointerValue(pointer.Buffer, pointer.Index + (op == BoundBinaryOperator.Add ? offset : -offset));
    }

    Value Variant(IrVariant variant, Frame frame)
    {
        var type = frame.Type(variant.Type);
        var map = TypeMap.For(type);
        var fields = new Cell[variant.Fields.Count];
        for (int i = 0; i < fields.Length; i++)
            fields[i] = new Cell(map.Apply(variant.Variant.Fields[i].Type), EvalOperand(variant.Fields[i], frame));
        return new EnumValue(type, variant.Variant, fields);
    }

    Value Array(IrArray array, Frame frame)
    {
        var type = frame.Type(array.Type);
        var element = ((FixedArrayType)type).Element;
        return new ArrayValue(type, [.. array.Elements.Select(e => new Cell(element, EvalOperand(e, frame)))]);
    }

    /// <summary>
    /// <c>data P</c>: a pointer to a fixed array's first element, into the array's own cells, so a
    /// write through the pointer is a write to the array.
    /// </summary>
    Value Data(IrData data, Frame frame)
    {
        var cell = PlaceCell(data.Place, frame);
        return cell.Value is ArrayValue array
            ? new PointerValue(array.View, 0)
            : throw new InterpreterException($"the data of a {cell.Value.GetType().Name}");
    }

    Value Test(IrTest test, Frame frame)
    {
        var operand = EvalOperandInPlace(test.Operand, frame);
        return test.Kind switch
        {
            // A `T?` is a nullable value or a nullable reference, whichever `T` stands for, so the
            // two null tests ask the same question of the value, which knows its own form.
            IrTestKind.NotNull or IrTestKind.HasValue => new BoolValue(!IsNull(operand)),
            IrTestKind.Tag => new BoolValue(operand is EnumValue value && value.Variant == test.Variant),
            IrTestKind.Class => new BoolValue(operand is ObjectRef obj && Conversions.IsSubtype(obj.Active, test.Target!.NonNull)),
            _ => throw new InterpreterException($"unexpected test {test.Kind}"),
        };
    }

    /// <summary>Whether a nullable value holds nothing, a reference refers to nothing, or a pointer or function is null.</summary>
    static bool IsNull(Value value) => value switch
    {
        NullValue => true,
        RefValue reference => reference.Cell is null,
        OptionalValue optional => optional.Inner is null,
        _ => false,
    };

    Value Convert(IrConvert conversion, Frame frame)
    {
        var from = frame.Type(conversion.Operand.Type);
        var to = frame.Type(conversion.Type);
        var operand = EvalOperand(conversion.Operand, frame);
        // A weak reference watches the object but does not keep it: the conversion takes no count,
        // and lets go of the strong one its operand held, as the runtime's temporary does.
        if (program.Bound.Stdlib.IsWeak(to))
        {
            var weak = WeakValue.Of(operand switch
            {
                ObjectRef obj => obj,
                RefValue { Cell: { Value: ObjectRef inner } } => inner,
                NullValue or RefValue => null,
                _ => throw new InterpreterException($"cannot convert a {operand.GetType().Name} to '{to}'"),
            });
            // A borrow took no count of its own, so there is nothing to let go of.
            if (conversion.Operand is not IrRead { Ownership: IrOwnership.Borrow })
                Release(from, operand);
            return weak;
        }
        return RuntimeArithmetic.Convert(from, to, operand, conversion.Loc is { } loc ? Required(loc, frame) : null);
    }

    Value Hold(IrHold hold, Frame frame)
    {
        var cell = PlaceCell(hold.Place, frame);
        // An object's hold is a counted reference; a string's copies share its bytes and a struct's
        // value is held by value, so neither makes a count. A borrow starts where the hold is stored.
        return Values.Copy(cell.Type, cell.Value);
    }

    string Required(IrLoc loc, Frame frame) =>
        Location(loc, frame) ?? throw new InterpreterException("a panicking instruction has no location");
}
