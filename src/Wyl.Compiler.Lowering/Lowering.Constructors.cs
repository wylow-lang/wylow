using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.Lowering;

public sealed partial class IrLowering
{
    /// <summary>
    /// Lowers what a constructor runs before its body (docs/design/ir.md, §5.11), and returns the
    /// body's statements left to lower. A class constructor calls its base's, then runs the field
    /// initializers; a struct constructor starts from the struct's default value, which runs them.
    /// A constructor that chains starts from the constructor it calls, which runs them instead.
    /// </summary>
    IReadOnlyList<BoundStatement> ConstructorStart(IReadOnlyList<BoundStatement> statements)
    {
        var owner = symbol.ContainingType!;
        var call = statements.FirstOrDefault() as BoundConstructorCall;
        if (call is not null)
            statements = [.. statements.Skip(1)];
        if (owner.Kind == TypeKind.Struct)
        {
            function.This = null;
            self = function.Struct = new IrLocal("self", owner.Type, IrLocalKind.Value, IrLocalRole.Variable);
            if (call is null)
                Add(LetOf(self, new IrDefault(owner.Type)));
            else
            {
                var enclosing = at;
                at = call.Location;
                Define(self, new BoundConstruction(call.Location, owner.Type, call.Constructor, call.Arguments));
                at = enclosing;
            }
            return statements;
        }
        if (call is not null)
            LowerConstructorCall(call);
        if (call is { Chains: true })
            return statements;
        foreach (var field in owner.Fields)
        {
            // A new object's fields start zeroed; a field of a type parameter is assigned by the constructor.
            if (field.Initializer is { } initializer)
            {
                var location = initializer.Location;
                Statement(new BoundAssignment(location, new BoundFieldAccess(location, new BoundThis(location, owner.Type), field, field.Type), initializer));
            }
            else if (field.Type.HasDefaultCode && !field.Type.IsTypeParameter)
                Add(new IrStore(IrPlace.Deref(self!).Field(field), IrRead.Move(new IrPlace(Let(new IrDefault(field.Type))))) { Origin = at });
        }
        return statements;
    }

    /// <summary>
    /// A struct's <c>make_default</c>, or one form of its memberwise constructor. Each field in
    /// order is set to its argument if the form passes it (the constructor's body assigns each
    /// field its parameter), or else to its initializer, or its type's default value unless that is
    /// zeroed storage.
    /// </summary>
    IrFunction Built(BoundFunction? memberwise, IReadOnlyList<bool> form)
    {
        var owner = symbol.ContainingType!;
        function.This = null;
        function.Builds = form;
        List<IrLocal> passed = [.. function.Parameters.Where((_, i) => form[i])];
        function.Parameters.Clear();
        function.Parameters.AddRange(passed);
        self = function.Struct = new IrLocal("self", owner.Type, IrLocalKind.Value, IrLocalRole.Variable);
        Add(LetOf(self, new IrZeroed(owner.Type)));
        for (int i = 0; i < owner.Fields.Count; i++)
        {
            var field = owner.Fields[i];
            if (form[i])
                Statement(memberwise!.Body.Statements[i]);
            else if (field.Initializer is { } initializer)
            {
                var location = initializer.Location;
                Statement(new BoundAssignment(location, new BoundFieldAccess(location, new BoundThis(location, owner.Type), field, field.Type), initializer));
            }
            else if (field.Type.HasDefaultCode)
                Add(new IrStore(new IrPlace(self).Field(field), IrRead.Move(new IrPlace(Let(new IrDefault(field.Type))))) { Origin = at });
        }
        Add(new IrReturn(IrRead.Copy(new IrPlace(self))) { Origin = at });
        return function;
    }

    /// <summary>Which fields each memberwise constructor call passes, as one form per combination, each built by a function of its own.</summary>
    static Dictionary<FunctionSymbol, List<IReadOnlyList<bool>>> MemberwiseForms(BoundProgram program)
    {
        var expressions = program.Functions.SelectMany(f => BoundTree.AllExpressions(f.Body))
            .Concat(program.Types.SelectMany(s => s.Fields).Select(f => f.Initializer).OfType<BoundExpression>().SelectMany(BoundTree.AllExpressions));
        return expressions
            .OfType<BoundConstruction>()
            .Where(c => c.Constructor.Symbol.IsMemberwise)
            .GroupBy(c => c.Constructor.Symbol)
            .ToDictionary(g => g.Key, g => g
                .Select(c => (IReadOnlyList<bool>)[.. c.Arguments.Select(a => a is not BoundFieldDefault)])
                .DistinctBy(FormKey)
                .OrderBy(FormKey, StringComparer.Ordinal)
                .ToList());
    }

    static string FormKey(IReadOnlyList<bool> form) => string.Concat(form.Select(given => given ? '1' : '0'));

    /// <summary>
    /// A class constructor's call of its base's or, chaining, another of its class's. Each argument is a statement of its own (spec memory.release.temporaries), so that the emitter can compute one needing statements in an
    /// immediately invoked lambda inside the C++ mem-initializer.
    /// </summary>
    void LowerConstructorCall(BoundConstructorCall call)
    {
        var method = call.Constructor;
        var lowered = new IrBlock { Origin = at };
        Into(lowered, () =>
        {
            var arguments = new List<IrArgument>();
            for (int i = 0; i < call.Arguments.Count; i++)
            {
                var parameter = method.ParameterType(i);
                var computed = new IrBlock { Origin = at };
                if (method.ParameterRefKind(i) != RefKind.None || parameter.IsNullableReference)
                {
                    Into(computed, () => arguments.Add(CallerHeld(parameter, method.ParameterRefKind(i), call.Arguments[i])));
                    // CallerHeld adds no statements, and a C++ mem-initializer has nowhere to put one.
                    if (computed.Statements.Count > 0)
                        throw new InvalidOperationException("a reference argument to a constructor call needs statements");
                    continue;
                }
                IrLocal? value = null;
                IrOperand? direct = null;
                Into(computed, () =>
                {
                    var operand = (IrOperand)Argument(parameter, RefKind.None, call.Arguments[i]);
                    if (operand is IrConstant)
                        direct = operand;
                    else
                    {
                        // A non-escaping function parameter takes the function value before its conversion.
                        value = Temp(operand.Type);
                        Add(new IrStore(new IrPlace(value), operand is IrRead { Ownership: IrOwnership.Borrow } read ? IrRead.Copy(read.Place) : operand) { Origin = at });
                    }
                });
                if (direct is not null)
                {
                    arguments.Add(direct);
                    continue;
                }
                Add(new IrDeclare(value!) { Origin = at });
                Add(computed);
                arguments.Add(Kept(IrRead.Move(new IrPlace(value!))));
            }
            var loc = method.Symbol.PassesLocation ? Loc(call.Location) : null;
            Add(DoOf(new IrCall(method, null, arguments, loc) { IsSuperCall = true }));
        });
        function.ConstructorCall = lowered;
        Add(lowered);
    }

    /// <summary>
    /// A reference argument to <c>super(...)</c> or <c>this(...)</c>, which the binder allows only to be a parameter's
    /// place or null. It needs no statements: it is read at the call, since a <c>ref?</c> parameter
    /// can't be <c>mut</c>, so nothing before the call can make it refer elsewhere.
    /// </summary>
    IrArgument CallerHeld(WylType parameter, RefKind refKind, BoundExpression argument) => argument switch
    {
        BoundConversion { Operand.Type: var from } when from == WylType.Null => new IrConstant(parameter, null),
        _ when !parameter.IsNullableReference => new IrRefArgument(refKind, Place(argument, Use.InUse)),
        BoundConversion { Operand: var place } when !place.Type.IsNullableReference => new IrAddrArgument(((NullableReferenceType)parameter).Kind, Place(place, Use.InUse)),
        BoundConversion { Operand: var other } => IrRead.Copy(Place(other, Use.Read)),
        _ => IrRead.Copy(Place(argument, Use.Read)),
    };
}
