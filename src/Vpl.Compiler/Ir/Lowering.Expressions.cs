using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Ir;

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
                return IrRead.Move(new IrPlace(Let(new IrConvert(narrowed.Type, Settle(Read(new IrPlace(locals[narrowed.Local])))))));
            case BoundLocalAccess { Local.Type.IsNullableReference: true, AssumesNonNull: false } reference:
                return Read(new IrPlace(locals[reference.Local]));
            case var place when located.ContainsKey(place) || IsPlace(place):
                return Read(Place(place, Use.Read));
            default:
                return IrRead.Move(new IrPlace(Let(Rvalue(expression, Use.Read))));
        }
    }

    /// <summary>The expression as one instruction's value, its operands lowered before it.</summary>
    IrRvalue Rvalue(BoundExpression expression, Use use)
    {
        switch (expression)
        {
            case BoundUnary unary:
            {
                var operand = Settle(Value(unary.Operand));
                bool panics = unary.Operator == BoundUnaryOperator.Negate && unary.Type.IsInteger;
                return new IrUnary(unary.Operator, operand, panics ? new IrLoc(unary.Location) : null);
            }
            case BoundBinary binary:
                return Binary(binary);
            case BoundCall { Method.Symbol.ReturnRefKind: not RefKind.None, Type.IsNullableReference: false }:
                return new IrUse(Settle(Value(expression)));
            case BoundCall call:
                return Call(call, call.Receiver is null ? null : Receiver(call));
            case BoundNew creation:
                filled.Add(creation);
                return new IrNew(creation.Type, creation.Constructor, Arguments(creation.Constructor, creation.Arguments), LocationFor(creation.Constructor, creation))
                {
                    Slot = slots.GetValueOrDefault(creation),
                };
            case BoundConstruction construction:
                return new IrConstruct(construction.Type, construction.Constructor, Arguments(construction.Constructor, construction.Arguments),
                    LocationFor(construction.Constructor, construction));
            case BoundVariant variant:
                return new IrVariant(variant.Type, variant.Variant, [.. Settle(variant.Arguments.Select(Value).ToList()).Cast<IrOperand>()]);
            case BoundArrayLiteral array:
                return new IrArray(array.Type, [.. Settle(array.Elements.Select(Value).ToList()).Cast<IrOperand>()]);
            case BoundConversion conversion:
                return Conversion(conversion, use);
            case BoundConditional conditional:
                return Conditional(conditional);
            case BoundCoalesce coalesce:
                return Coalesce(coalesce);
            case BoundConditionalAccess access:
                return ConditionalAccess(access) ?? throw new InvalidOperationException("a 'void' '?.' used as a value");
            case BoundIs test:
                return Is(test);
            case BoundSwitchExpression switchExpression:
                return SwitchExpression(switchExpression);
            case BoundArrayLength length:
                // The length is the type's, but the operand is still evaluated.
                if (IsPlace(length.Operand))
                    Place(length.Operand, Use.Read);
                else
                    Add(new IrDo(Rvalue(length.Operand, Use.Read)));
                return new IrUse(IrConstant.Int(length.Operand.Type.FixedLength));
            case BoundArrayData data:
                // A view isn't read right away, so the object it points into is held while it may be used.
                return new IrData(Place(data.Array, use == Use.Read ? Use.InUse : use));
            case BoundLiteral or BoundNullLiteral or BoundThis or BoundLocalAccess or BoundFieldAccess or BoundIndex or BoundNonNull { Operand.Type.IsNullableReference: true }:
                return new IrUse(Settle(Value(expression)));
            case BoundNonNull nonNull:
                return Unwrapped(nonNull);
            default:
                throw new NotLoweredException($"a {expression.GetType().Name}");
        }
    }

    /// <summary><c>x!</c> of a nullable value or object: its value, once it is checked not to be null.</summary>
    IrRvalue Unwrapped(BoundNonNull nonNull)
    {
        var operand = Settle(Value(nonNull.Operand));
        var (place, ownership) = operand is IrRead read
            ? (read.Place, read.Ownership == IrOwnership.Move ? IrOwnership.Move : IrOwnership.Copy)
            : (new IrPlace(Let(new IrUse(operand))), IrOwnership.Move);
        bool isValue = nonNull.Operand.Type.IsNullableValue;
        Add(new IrCheck(isValue ? IrCheckKind.HasValue : IrCheckKind.NotNull, [IrRead.Borrow(place)], new IrLoc(nonNull.Location)));
        return isValue ? new IrUse(new IrRead(ownership, place.Assumed())) : new IrConvert(nonNull.Type, new IrRead(ownership, place));
    }

    static IrLoc? LocationFor(MethodReference method, BoundExpression site) => method.Symbol.PassesLocation ? new IrLoc(site.Location) : null;

    IrRvalue Binary(BoundBinary binary)
    {
        if (binary.Operator is BoundBinaryOperator.LogicalAnd or BoundBinaryOperator.LogicalOr)
            return Logical(binary);
        if (binary.Operator is BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual && NullTested(binary) is { } tested)
        {
            var kind = tested.Type.IsNullableValue ? IrTestKind.HasValue : IrTestKind.NotNull;
            var test = new IrTest(kind, Settle(Value(tested)));
            return binary.Operator == BoundBinaryOperator.NotEqual ? test : new IrUnary(BoundUnaryOperator.LogicalNot, IrRead.Move(new IrPlace(Let(test))));
        }
        var left = Value(binary.Left);
        var right = Value(binary.Right);
        var operands = Settle([left, right]);
        var loc = IrBinary.IsChecked(binary.Operator, binary.Left.Type) ? new IrLoc(binary.Location) : null;
        return new IrBinary(binary.Operator, (IrOperand)operands[0], (IrOperand)operands[1], binary.Type, loc);
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
        if (to.IsFunction || (to.IsNullable && to.NonNull.IsFunction))
        {
            if (!to.NonNull.IsEscaping)
                throw new NotLoweredException("a non-escaping function value");
        }
        if (to.IsNullableReference)
            throw new NotLoweredException("a conversion to a nullable reference outside a call or declaration");
        var operand = to.IsRefStruct ? Settle(ViewOperand(conversion.Operand, use)) : Settle(Value(conversion.Operand));
        bool panics = to.IsEnum && !from.IsEnum || (from.IsInteger && to == VplType.Char);
        return new IrConvert(to, operand, panics ? new IrLoc(conversion.Location) : null);
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
    /// A call's receiver: an object, held while the call runs unless nothing can replace it, or a
    /// struct's place. What a method returns a reference or view into is borrowed too, if it may be
    /// a <c>Borrowable</c>.
    /// </summary>
    IrArgument Receiver(BoundCall call)
    {
        var receiver = call.Receiver!;
        var type = receiver.Type;
        var method = call.Method.Symbol;
        bool returnsInto = method.ReturnRefKind != RefKind.None || call.Type.IsRefStruct;
        if (receiver is BoundThis { Type.IsReference: true })
            return returnsInto && MayBeBorrowable(type) ? IrRead.Borrow(new IrPlace(Hold(new IrPlace(self!), borrows: true))) : IrRead.Borrow(new IrPlace(self!));
        if (type.IsReference)
            return IrRead.Borrow(new IrPlace(ObjectLocal(receiver, Use.InUse, returnsInto && MayBeBorrowable(type))));
        if (type.IsTypeParameter || (type == VplType.String && returnsInto))
        {
            var place = IsPlace(receiver) ? Place(receiver, Use.InUse) : new IrPlace(Let(Rvalue(receiver, Use.Read)));
            // A generic value may be an object, which the call holds; a struct is used in place.
            if (place is { IsDeref: false, Projections.Count: 0, Local: var local } && !local.IsAssignedByLambda && !returnsInto)
                return method.IsReadOnly ? IrRead.Borrow(place) : new IrRefArgument(RefKind.Ref, place);
            var holder = Hold(place, returnsInto && MayBeBorrowable(type));
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
        var settled = Settle(lowered);
        for (int i = 0; i < settled.Count; i++)
        {
            if (settled[i] is IrOperand operand && method.ParameterRefKind(i) == RefKind.None && !method.ParameterType(i).IsNullableReference)
                settled[i] = Kept(operand);
        }
        return settled;
    }

    IrArgument Argument(VplType parameter, RefKind refKind, BoundExpression argument)
    {
        if (argument is BoundFieldDefault field)
            return new IrFieldDefault(field.Field);
        if (parameter.NonNull.IsFunction && !parameter.NonNull.IsEscaping)
            throw new NotLoweredException("a non-escaping function argument");
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
    /// A by-value argument that may own objects is passed as a copy of a value the statement
    /// keeps, so that it is released when the statement completes, whenever the callee's copy is
    /// destroyed. A value nothing else can replace keeps it alive already.
    /// </summary>
    IrOperand Kept(IrOperand operand)
    {
        if (!operand.Type.MayHoldReferences)
            return operand;
        return operand switch
        {
            IrRead { Ownership: IrOwnership.Move, Place: { IsDeref: false, Projections.Count: 0 } temp } => IrRead.Copy(temp),
            IrRead { Place: var place } when !IsStable(place) => IrRead.Copy(new IrPlace(Let(new IrUse(IrRead.Copy(place))))),
            _ => operand,
        };
    }

    /// <summary>
    /// Whether nothing but the function itself can replace the value while an operation uses it:
    /// <c>this</c>, a local no lambda assigns, or a field that isn't <c>mut</c> of an object held that way.
    /// </summary>
    bool IsStable(IrPlace place)
    {
        var local = place.Local;
        bool stableLocal = (local.IsThis || !local.IsTemporary || local.Kind == IrLocalKind.Borrowed && aliases.TryGetValue(local, out var aliased) && IsStable(aliased))
            && !local.IsAssignedByLambda && local.Kind != IrLocalKind.Reference;
        if (!place.IsDeref)
            return place.Projections.Count == 0 && stableLocal;
        return stableLocal && place.Projections is [IrField { Field.IsMutable: false }];
    }
}
