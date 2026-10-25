using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Ir;

public sealed partial class IrLowering
{
    /// <summary>
    /// Lowers what a constructor runs before its body (docs/design/ir.md, §5.11), and returns the
    /// body's statements left to lower. A class constructor calls its base's, then runs the field
    /// initializers; a struct constructor starts from the struct's default value, which runs them.
    /// </summary>
    IReadOnlyList<BoundStatement> ConstructorStart(IReadOnlyList<BoundStatement> statements)
    {
        var owner = symbol.ContainingType!;
        if (owner.Kind == TypeKind.Struct)
        {
            function.This = null;
            self = function.Struct = new IrLocal("self", owner.Type, IrLocalKind.Value);
            Add(new IrLet(self, new IrDefault(owner.Type)));
            return statements;
        }
        if (statements.FirstOrDefault() is BoundSuperCall super)
        {
            BaseCall(super);
            statements = [.. statements.Skip(1)];
        }
        foreach (var field in owner.Fields)
        {
            // A new object's fields start zeroed; a field of a type parameter is assigned by the constructor.
            if (field.Initializer is { } initializer)
            {
                var location = initializer.Location;
                Statement(new BoundAssignment(location, new BoundFieldAccess(location, new BoundThis(location, owner.Type), field, field.Type), initializer));
            }
            else if (HasDefaultCode(field.Type) && !field.Type.IsTypeParameter)
                Add(new IrStore(IrPlace.Deref(self!).Field(field), IrRead.Move(new IrPlace(Let(new IrDefault(field.Type))))));
        }
        return statements;
    }

    /// <summary>Whether the type's default value is more than zeroed storage.</summary>
    static bool HasDefaultCode(VplType type) =>
        type.IsFixedArray ? HasDefaultCode(type.ElementType!)
        : type.IsTypeParameter || (type.IsStruct && !type.Symbol!.IsExtern
            && (type.Symbol.IsGeneric || type.Symbol.Fields.Any(f => f.Initializer is not null || HasDefaultCode(f.Type))));

    /// <summary>
    /// The base constructor call. Each argument is a statement of its own (03-memory-model.md, When
    /// references are released), so that the emitter can compute one needing statements in an
    /// immediately invoked lambda inside the C++ mem-initializer.
    /// </summary>
    void BaseCall(BoundSuperCall super)
    {
        var method = super.Constructor;
        var call = new IrBlock();
        Into(call, () =>
        {
            var arguments = new List<IrArgument>();
            for (int i = 0; i < super.Arguments.Count; i++)
            {
                var parameter = method.ParameterType(i);
                if (method.ParameterRefKind(i) != RefKind.None || parameter.IsNullableReference || parameter.IsRefStruct)
                    throw new NotLoweredException("a reference or view argument to super(...)");
                var computed = new IrBlock();
                IrLocal? value = null;
                IrOperand? direct = null;
                Into(computed, () =>
                {
                    var operand = Settle((IrOperand)Argument(parameter, RefKind.None, super.Arguments[i]));
                    // A value nothing can change needs no statement of its own.
                    if (block.Statements.Count == 0 && operand switch { IrConstant => true, IrRead { Ownership: not IrOwnership.Move } read => IsStable(read.Place), _ => false })
                        direct = operand;
                    else
                    {
                        // A non-escaping function parameter takes the function value before its conversion.
                        value = Temp(operand.Type);
                        Add(new IrStore(new IrPlace(value), operand is IrRead { Ownership: IrOwnership.Borrow } read ? IrRead.Copy(read.Place) : operand));
                    }
                });
                if (direct is not null)
                {
                    arguments.Add(direct);
                    continue;
                }
                Add(new IrDeclare(value!));
                Add(computed);
                arguments.Add(Kept(IrRead.Move(new IrPlace(value!))));
            }
            var loc = method.Symbol.PassesLocation ? new IrLoc(super.Location) : null;
            Add(new IrDo(new IrCall(method, null, arguments, loc) { IsSuperCall = true }));
        });
        function.BaseCall = call;
        Add(call);
    }
}
