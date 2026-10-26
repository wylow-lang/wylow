using Vpl.Compiler.Ir;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Lowering;

public sealed partial class IrLowering
{
    /// <summary>The value of an expression as an operand: a constant, a read of a place, or a temporary's value.</summary>
    IrOperand Value(BoundExpression expression)
    {
        switch (expression)
        {
            case BoundLiteral literal:
                return new IrConstant(literal.Type, literal.Value);
            case BoundNullLiteral:
                return new IrConstant(VplType.Null, null);
            case BoundConversion { Operand.Type: var from } conversion when from == VplType.Null:
                return new IrConstant(conversion.Type, null);
            case BoundThis { Type.IsReference: true }:
                return Read(new IrPlace(self!));
            case BoundLocalAccess { AssumesNonNull: true, Local.Type: { IsNullable: true, NonNull.IsReference: true } } narrowed:
                // A narrowed nullable reference is the same object, which only its type changes.
                return IrRead.Move(new IrPlace(Let(new IrConvert(narrowed.Type, Read(new IrPlace(Accessed(narrowed)))))));
            case BoundLocalAccess { Local.Type.IsNullableReference: true, AssumesNonNull: false } reference:
                return Read(new IrPlace(Accessed(reference)));
            case var place when located.ContainsKey(place) || IsPlace(place):
                return Read(Place(place, Use.Read));
            default:
                return IrRead.Move(new IrPlace(Let(Rvalue(expression, Use.Read))));
        }
    }

    /// <summary>The expression as one instruction's value, its operands lowered before it.</summary>
    IrRvalue Rvalue(BoundExpression expression, Use use)
    {
        var enclosing = at;
        at = expression.Location;
        var value = RvalueOf(expression, use);
        at = enclosing;
        valueOrigins[value] = expression.Location;
        return value;
    }

    IrRvalue RvalueOf(BoundExpression expression, Use use)
    {
        switch (expression)
        {
            case BoundUnary unary:
            {
                return new IrUnary(unary.Operator, Value(unary.Operand), Loc(unary.Location));
            }
            case BoundBinary binary:
                return Binary(binary);
            case BoundCall { Method.Symbol.ReturnRefKind: not RefKind.None, Type.IsNullableReference: false }:
                return new IrUse(Value(expression));
            case BoundCall call:
                return Call(call, null);
            case BoundNew creation:
                return new IrNew(creation.Type, creation.Constructor, Arguments(creation.Constructor, creation.Arguments), LocationFor(creation.Constructor, creation))
                {
                    Origin = creation.Location,
                };
            case BoundConstruction construction:
                return new IrConstruct(construction.Type, construction.Constructor, Arguments(construction.Constructor, construction.Arguments),
                    LocationFor(construction.Constructor, construction));
            case BoundVariant variant:
                return new IrVariant(variant.Type, variant.Variant, [.. variant.Arguments.Select(Value)]);
            case BoundArrayLiteral array:
                return new IrArray(array.Type, [.. array.Elements.Select(Value)]);
            case BoundConversion conversion:
                return Conversion(conversion, use);
            case BoundLambda lambda:
                return Lambda(lambda);
            case BoundInvocation invocation:
                return Invocation(invocation);
            case BoundConditional { Type.IsNonEscapingFunction: true } or BoundSwitchExpression { Type.IsNonEscapingFunction: true }:
                return Choice(expression);
            case BoundConditional conditional:
                return Conditional(conditional);
            case BoundCoalesce coalesce:
                return Coalesce(coalesce);
            case BoundConditionalAccess access:
                return ConditionalAccess(access) ?? throw new InvalidOperationException("a 'void' '?.' used as a value");
            case BoundIs test:
                return Is(test);
            case BoundTry attempt:
                return Try(attempt);
            case BoundSwitchExpression switchExpression:
                return SwitchExpression(switchExpression);
            case BoundArrayLength length:
                // The length is the type's, but the operand is still evaluated.
                if (IsPlace(length.Operand))
                    Place(length.Operand, Use.Read);
                else
                    Add(DoOf(Rvalue(length.Operand, Use.Read)));
                return new IrUse(IrConstant.Int(length.Operand.Type.FixedLength));
            case BoundArrayData data:
                // A view isn't read right away, so the object it points into is held while it may be used.
                return new IrData(Place(data.Array, use == Use.Read ? Use.InUse : use));
            case BoundLiteral or BoundNullLiteral or BoundThis or BoundLocalAccess or BoundFieldAccess or BoundIndex or BoundNonNull { Operand.Type.IsNullableReference: true }:
                return new IrUse(Value(expression));
            case BoundNonNull nonNull:
                return Unwrapped(nonNull);
            default:
                throw new InvalidOperationException($"unexpected {expression.GetType().Name}");
        }
    }

    /// <summary><c>x!</c> of a nullable value or object: its value, once it is checked not to be null.</summary>
    IrRvalue Unwrapped(BoundNonNull nonNull)
    {
        var operand = Value(nonNull.Operand);
        var (place, ownership) = operand is IrRead read
            ? (read.Place, read.Ownership == IrOwnership.Move ? IrOwnership.Move : IrOwnership.Copy)
            : (new IrPlace(Let(new IrUse(operand))), IrOwnership.Move);
        bool isValue = nonNull.Operand.Type.IsNullableValue;
        Add(new IrCheck(isValue ? IrCheckKind.HasValue : IrCheckKind.NotNull, [IrRead.Borrow(place)], Loc(nonNull.Location)) { Origin = at });
        return isValue ? new IrUse(new IrRead(ownership, place.Assumed())) : new IrConvert(nonNull.Type, new IrRead(ownership, place));
    }

    IrLoc? LocationFor(MethodReference method, BoundExpression site) => method.Symbol.PassesLocation ? Loc(site.Location) : null;

    IrRvalue Binary(BoundBinary binary)
    {
        if (binary.Operator is BoundBinaryOperator.LogicalAnd or BoundBinaryOperator.LogicalOr)
            return Logical(binary);
        if (binary.Operator is BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual && NullTested(binary) is { } tested)
        {
            var kind = tested.Type.IsNullableValue ? IrTestKind.HasValue : IrTestKind.NotNull;
            var test = new IrTest(kind, Value(tested));
            return binary.Operator == BoundBinaryOperator.NotEqual ? test : new IrUnary(BoundUnaryOperator.LogicalNot, IrRead.Move(new IrPlace(Let(test))));
        }
        var left = Value(binary.Left);
        var right = Value(binary.Right);
        return new IrBinary(binary.Operator, left, right, binary.Type, Loc(binary.Location));
    }

    /// <summary>The side of a comparison with the <c>null</c> literal that may be null, if that is the comparison.</summary>
    static BoundExpression? NullTested(BoundBinary binary) => (binary.Left, binary.Right) switch
    {
        ({ Type: var t } value, BoundNullLiteral or BoundConversion { Operand: BoundNullLiteral }) when t.IsNullable || t.IsNullableReference => value,
        (BoundNullLiteral or BoundConversion { Operand: BoundNullLiteral }, { Type: var t } value) when t.IsNullable || t.IsNullableReference => value,
        _ => null,
    };

    IrRvalue Conversion(BoundConversion conversion, Use use)
    {
        var from = conversion.Operand.Type;
        var to = conversion.Type;
        if (from == VplType.Null)
            return new IrUse(new IrConstant(to, null));
        if (to.IsNullableNonEscapingFunction)
            throw new InvalidOperationException("a nullable non-escaping function is only made from an argument");
        if (to.IsNullableReference)
            throw new InvalidOperationException("a conversion to a nullable reference is only made in a call or declaration");
        var operand = to.IsRefStruct ? ViewOperand(conversion.Operand, use) : Value(conversion.Operand);
        return new IrConvert(to, operand, Loc(conversion.Location));
    }

    /// <summary>A view as an operand, whose holds last as long as <paramref name="use"/> says.</summary>
    IrOperand ViewOperand(BoundExpression view, Use use) =>
        IsPlace(view) ? Read(Place(view, use)) : IrRead.Move(new IrPlace(Let(Rvalue(view, use))));

    IrCall Call(BoundCall call, IrArgument? receiver)
    {
        var method = call.Method;
        if (receiver is null && call.Receiver is not null)
            receiver = Receiver(call);
        var arguments = Arguments(method, call.Arguments);
        return new IrCall(method, receiver, arguments, LocationFor(method, call)) { IsSuperCall = call.IsSuperCall };
    }

    /// <summary>
    /// A call's receiver: an object, held while the call runs, or a struct's place. What a method
    /// returns a reference or view into is borrowed too, if it may be a <c>Borrowable</c>.
    /// </summary>
    IrArgument Receiver(BoundCall call)
    {
        var receiver = call.Receiver!;
        var type = receiver.Type;
        var method = call.Method.Symbol;
        bool returnsInto = method.ReturnRefKind != RefKind.None || call.Type.IsRefStruct;
        if (receiver is BoundThis { Type.IsReference: true })
            return returnsInto && program.MayBeBorrowable(type) ? IrRead.Borrow(new IrPlace(Hold(new IrPlace(self!), borrows: true))) : IrRead.Borrow(new IrPlace(self!));
        if (type.IsReference)
            return IrRead.Borrow(new IrPlace(ObjectLocal(receiver, returnsInto && program.MayBeBorrowable(type))));
        if (type.IsTypeParameter || (type.SharesStorage && returnsInto))
        {
            var place = IsPlace(receiver) ? Place(receiver, Use.InUse) : new IrPlace(Let(Rvalue(receiver, Use.Read)));
            // A generic value may be an object, which the call holds; a struct is used in place.
            var holder = Hold(place, returnsInto && program.MayBeBorrowable(type));
            return method.IsReadOnly || returnsInto ? IrRead.Borrow(new IrPlace(holder)) : new IrRefArgument(RefKind.Ref, IrPlace.Deref(holder));
        }
        var kind = method.IsReadOnly ? RefKind.In : RefKind.Ref;
        return new IrRefArgument(kind, IsPlace(receiver) ? Place(receiver, Use.InUse) : new IrPlace(Let(Rvalue(receiver, Use.Read))));
    }

    /// <summary>A call's arguments, evaluated in order.</summary>
    List<IrArgument> Arguments(MethodReference method, IReadOnlyList<BoundExpression> arguments)
    {
        var lowered = new List<IrArgument>();
        for (int i = 0; i < arguments.Count; i++)
            lowered.Add(Argument(method.ParameterType(i), method.ParameterRefKind(i), arguments[i]));
        for (int i = 0; i < lowered.Count; i++)
        {
            if (lowered[i] is IrOperand operand && method.ParameterRefKind(i) == RefKind.None && !method.ParameterType(i).IsNullableReference)
                lowered[i] = Kept(operand);
        }
        return lowered;
    }

    IrArgument Argument(VplType parameter, RefKind refKind, BoundExpression argument)
    {
        if (argument is BoundFieldDefault field)
            return new IrFieldDefault(field.Field);
        if (parameter.NonNull.IsNonEscapingFunction)
            return FunctionArgument(parameter, argument);
        if (parameter.IsNullableReference)
        {
            var kind = parameter.ReferenceKind;
            return argument switch
            {
                BoundConversion { Operand.Type: var from } when from == VplType.Null => new IrConstant(parameter, null),
                BoundConversion { Operand: var place } when !place.Type.IsNullableReference && IsPlace(place) => new IrAddrArgument(kind, Place(place, Use.InUse)),
                // An `in?` parameter takes a temporary too, which lives until the statement is done.
                BoundConversion { Operand: var value } when !value.Type.IsNullableReference => new IrAddrArgument(kind, new IrPlace(Let(Rvalue(value, Use.Read)))),
                BoundConversion { Operand: var other } => Value(other),
                _ => Value(argument),
            };
        }
        if (refKind != RefKind.None)
            return new IrRefArgument(refKind, IsPlace(argument) ? Place(argument, Use.InUse) : new IrPlace(Let(Rvalue(argument, Use.Read))));
        if (parameter.IsRefStruct)
            return ViewOperand(argument, Use.InUse);
        return Value(argument);
    }

    /// <summary>
    /// A by-value argument is passed as a copy of a value the statement keeps, so that what it owns
    /// is released when the statement completes, whenever the callee's copy is destroyed (spec
    /// 03-memory-model.md, When references are released).
    /// </summary>
    IrOperand Kept(IrOperand operand) => operand switch
    {
        IrRead { Ownership: IrOwnership.Move, Place: { IsDeref: false, Projections.Count: 0, Local.IsTemporary: true } temp } => IrRead.Copy(temp),
        IrRead read => IrRead.Copy(new IrPlace(Let(new IrUse(IrRead.Copy(read.Place))))),
        _ => operand,
    };
}
