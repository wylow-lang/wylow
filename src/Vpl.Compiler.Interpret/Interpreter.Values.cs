using System.Text;
using Vpl.Compiler.Ir;
using Vpl.Compiler.Semantics;

namespace Vpl.Compiler.Interpret;

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
            IrConstant constant => ConstantValue(constant),
            IrRead read => EvalRead(read, frame),
            _ => throw new InterpreterException($"unexpected operand {operand.GetType().Name}"),
        };
    }

    Value EvalRead(IrRead read, Frame frame)
    {
        var place = read.Place;
        // A reference local's own value is the reference it holds, not what it refers to.
        if (!place.IsDeref && place.Projections.Count == 0 && place.Local.Kind == IrLocalKind.Reference)
            return new RefValue(frame.Find(place.Local) is RefStorage storage ? storage.Cell : null);
        var cell = PlaceCell(place, frame);
        return read.Ownership switch
        {
            IrOwnership.Copy => Values.Copy(cell.Type, cell.Value),
            IrOwnership.Move => Take(cell),
            _ => cell.Value,
        };
    }

    static Value Take(Cell cell)
    {
        var value = cell.Value;
        cell.Value = MovedValue.Instance;
        return value;
    }

    static Value ConstantValue(IrConstant constant)
    {
        if (constant.Value is null)
        {
            if (constant.Type.IsNullableValue)
                return new OptionalValue(null);
            if (constant.Type.IsNullableReference)
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

    /// <summary>An argument the callee owns; a borrow of a place is copied so the callee can't alias it.</summary>
    Value EvalOwnedArgument(IrArgument argument, Frame frame) => argument switch
    {
        IrRead { Ownership: IrOwnership.Borrow } read => Values.Copy(read.Type, EvalOperand(read, frame)),
        IrOperand operand => EvalOperand(operand, frame),
        _ => throw new InterpreterException($"unexpected argument {argument.GetType().Name}"),
    };

    static Int128 Integer(Value value) => value is IntValue number ? number.Bits : throw new InterpreterException($"expected an integer, got {value.GetType().Name}");

    // ---------------------------------------------------------------------------------------------
    // Places
    // ---------------------------------------------------------------------------------------------

    /// <summary>The cell a place names: a local's storage, what a reference refers to, then projections.</summary>
    Cell PlaceCell(IrPlace place, Frame frame)
    {
        var cell = BaseCell(place, frame);
        foreach (var projection in place.Projections)
            cell = Project(cell, projection, frame);
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
            IrDeref.Pointee => throw new UnsupportedFeature("pointers land in step 4"),
            _ => throw new InterpreterException($"unexpected dereference of '{local.Name}'"),
        };
    }

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
            _ => throw new UnsupportedFeature("pointer elements land in step 4"),
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

    static Cell AssumedCell(Cell cell) =>
        cell.Value is OptionalValue { Inner: { } inner } ? inner : throw new InterpreterException("a null nullable value was used as narrowed");

    /// <summary>Stores a new value, then releases the old one (spec 03-memory-model.md §3.2).</summary>
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
        public Value? Zeroed(IrZeroed value) => Values.Zeroed(frame.Type(value.Type));
        public Value? Ref(IrRef value) => new RefValue(interpreter.PlaceCell(value.Place, frame));
        public Value? Addr(IrAddr value) => new RefValue(interpreter.PlaceCell(value.Place, frame));
        public Value? Test(IrTest value) => interpreter.Test(value, frame);
        public Value? Convert(IrConvert value) => interpreter.Convert(value, frame);
        public Value? Hold(IrHold value) => interpreter.Hold(value, frame);
        public Value? Invoke(IrInvoke value) => throw new UnsupportedFeature("function values land in step 3");
        public Value? New(IrNew value) => interpreter.New(value, frame);
        public Value? Closure(IrClosure value) => throw new UnsupportedFeature("closures land in step 3");
        public Value? Lambda(IrLambda value) => throw new UnsupportedFeature("function values land in step 3");
        public Value? Choose(IrChoose value) => throw new UnsupportedFeature("function values land in step 3");
        public Value? Data(IrData value) => throw new UnsupportedFeature("spans and pointers land in step 4");
    }

    /// <summary>
    /// An operand an operation only reads. A move here leaves its local owning the value, which
    /// releases it when its block ends, as C++ does when the operation binds a constant reference.
    /// </summary>
    Value EvalOperandInPlace(IrOperand operand, Frame frame) =>
        operand is IrRead { Ownership: IrOwnership.Move } read ? PlaceCell(read.Place, frame).Value : EvalOperand(operand, frame);

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
        if (operandType.IsReference || operandType == VplType.Null)
        {
            bool same = SameReference(left, right);
            return new BoolValue(op == BoundBinaryOperator.Equal ? same : !same);
        }
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
        if (operandType == VplType.Bool)
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
        _ => false,
    };

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

    Value Test(IrTest test, Frame frame)
    {
        var operand = EvalOperandInPlace(test.Operand, frame);
        return test.Kind switch
        {
            IrTestKind.NotNull => new BoolValue(operand switch
            {
                NullValue => false,
                RefValue reference => reference.Cell is not null,
                ObjectRef => true,
                _ => throw new InterpreterException($"a not-null test of a {operand.GetType().Name}"),
            }),
            IrTestKind.HasValue => new BoolValue(operand is OptionalValue { Inner: not null }),
            IrTestKind.Tag => new BoolValue(operand is EnumValue value && value.Variant == test.Variant),
            IrTestKind.Class => new BoolValue(operand is ObjectRef obj && Conversions.IsSubtype(obj.Active, test.Target!.NonNull)),
            _ => throw new InterpreterException($"unexpected test {test.Kind}"),
        };
    }

    Value Convert(IrConvert conversion, Frame frame)
    {
        var from = frame.Type(conversion.Operand.Type);
        var to = frame.Type(conversion.Type);
        if (program.Bound.Stdlib.IsWeak(to) || program.Bound.Stdlib.IsWeak(from))
            throw new UnsupportedFeature("weak references land in step 5");
        var operand = EvalOperand(conversion.Operand, frame);
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
