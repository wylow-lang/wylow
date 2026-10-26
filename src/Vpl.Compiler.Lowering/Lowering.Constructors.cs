using Vpl.Compiler.Ir;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Lowering;

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
            self = function.Struct = new IrLocal("self", owner.Type, IrLocalKind.Value) { IsChangedByLambda = thisCapturedByLambda };
            Add(LetOf(self, new IrDefault(owner.Type)));
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
        self = function.Struct = new IrLocal("self", owner.Type, IrLocalKind.Value) { IsChangedByLambda = thisCapturedByLambda };
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
    /// The base constructor call. Each argument is a statement of its own (03-memory-model.md, When
    /// references are released), so that the emitter can compute one needing statements in an
    /// immediately invoked lambda inside the C++ mem-initializer.
    /// </summary>
    void BaseCall(BoundSuperCall super)
    {
        var method = super.Constructor;
        var call = new IrBlock { Origin = at };
        Into(call, () =>
        {
            var arguments = new List<IrArgument>();
            for (int i = 0; i < super.Arguments.Count; i++)
            {
                var parameter = method.ParameterType(i);
                var computed = new IrBlock { Origin = at };
                // The binder allows only references and views into what the caller holds, which need no statements.
                if (method.ParameterRefKind(i) != RefKind.None || parameter.IsNullableReference || parameter.IsRefStruct)
                {
                    Into(computed, () => arguments.Add(Argument(parameter, method.ParameterRefKind(i), super.Arguments[i])));
                    if (computed.Statements.Count > 0)
                        throw new InvalidOperationException("a reference or view argument to super(...) needs statements");
                    continue;
                }
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
            var loc = method.Symbol.PassesLocation ? Loc(super.Location) : null;
            Add(DoOf(new IrCall(method, null, arguments, loc) { IsSuperCall = true }));
        });
        function.BaseCall = call;
        Add(call);
    }
}
