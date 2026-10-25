using System.Text;
using Vpl.Compiler.Ir;
using Vpl.Compiler.Semantics;

namespace Vpl.Compiler.CodeGen;

/// <summary>A lowered class constructor's base constructor call, which C++ makes the mem-initializer (docs/design/ir.md, §5.11).</summary>
public sealed partial class CppEmitter
{
    static IrCall BaseCallOf(IrBlock baseCall) => (IrCall)((IrDo)baseCall.Statements[^1]).Value;

    /// <summary>
    /// Whether a class constructor's base call has more than one argument computed by statements.
    /// MSVC may evaluate the arguments of a braced mem-initializer in any order, so the constructor
    /// then delegates to one taking them as <c>vpl::Arguments</c>, which a lambda computes in order.
    /// They are released when the base constructor returns, the last first, as a statement's are.
    /// </summary>
    static bool DelegatesBaseCall(IrFunction function) => function.BaseCall is { } baseCall && baseCall.Statements.Count(s => s is IrDeclare) > 1;

    /// <summary>The constructor a class's constructor delegates to, declared in the class, which calls the base constructor with the arguments it is given.</summary>
    void EmitBaseArgumentsConstructor(TypeSymbol s, int index, IrFunction function)
    {
        var call = BaseCallOf(function.BaseCall!);
        var arguments = call.Arguments.Select((_, i) => FunctionArgument(call.Method, i, $"vpl::argument<{i}>(arguments)")).ToList();
        if (call.Loc is { } loc)
            arguments.Add(LocText(loc));
        Line("template <class A>");
        Line($"{TypeName(s)}(vpl::BaseArguments<{index}>, A&& arguments) : {ObjectType(call.Method.Owner!)}{{{string.Join(", ", arguments)}}} {{}}");
    }

    /// <summary>The delegation to <see cref="EmitBaseArgumentsConstructor"/>'s constructor, with a lambda computing the arguments.</summary>
    string DelegatingText(IrFunction function, IrBlock baseCall)
    {
        var call = BaseCallOf(baseCall);
        var s = function.Symbol.ContainingType!;
        var computed = baseCall.Statements.OfType<IrDeclare>().Select(d => d.Local).ToHashSet();
        var (enclosingOutput, enclosingIndent) = (output, indent);
        output = new StringBuilder();
        indent = 2;
        IrStatements(baseCall);
        var values = call.Arguments.Select(a => a is IrRead { Place: { IsDeref: false, Projections.Count: 0, Local: var local } } && computed.Contains(local)
            ? $"std::move({Name(local)})"
            : OperandText((IrOperand)a));
        Line($"return vpl::Arguments{{{string.Join(", ", values)}}};");
        var body = output.ToString();
        (output, indent) = (enclosingOutput, enclosingIndent);
        return $"{TypeName(s)}{{vpl::BaseArguments<{s.Constructors.IndexOf(function.Symbol)}>{{}}, [&] {{\n{body}    }}()}}";
    }

    /// <summary>
    /// The base constructor call, as the mem-initializer's, whose arguments need no order but for at
    /// most one computed by statements, which runs them in an immediately invoked lambda.
    /// </summary>
    string BaseCallText(IrBlock baseCall)
    {
        var call = BaseCallOf(baseCall);
        var enclosingIndent = indent;
        indent = 1;
        var arguments = ArgumentsText(call.Method, call.Arguments, call.Loc);
        indent = enclosingIndent;
        return $"{ObjectType(call.Method.Owner!)}{{{string.Join(", ", arguments)}}}";
    }

    /// <summary>
    /// The base constructor call prints as a mem-initializer. Unless the constructor delegates, at
    /// most one argument is computed by a block, which is printed inside it: as its value when
    /// everything nests into that and the block makes nothing whose release could be observed,
    /// which would then wait for the base constructor, and otherwise in an immediately invoked lambda.
    /// </summary>
    void PlanBaseCall(IrFunction function, bool nest)
    {
        var baseCall = function.BaseCall!;
        var statements = baseCall.Statements;
        plan.Consumed.Add(baseCall);
        if (DelegatesBaseCall(function))
        {
            // The statements run in order in the delegating constructor's lambda, which returns the arguments.
            plan.Consumed.Add(statements[^1]);
            if (nest)
                PlanBlock(baseCall);
            return;
        }
        for (int i = 0; i + 1 < statements.Count; i += 2)
        {
            var declare = (IrDeclare)statements[i];
            var computed = (IrBlock)statements[i + 1];
            var local = declare.Local;
            if (nest)
                PlanBlock(computed);
            var store = (IrStore)computed.Statements[^1];
            var moved = IrWalk.Descendants(computed).SelectMany(IrWalk.Operands).OfType<IrRead>()
                .Where(r => r is { Ownership: IrOwnership.Move, Place: { IsDeref: false, Projections.Count: 0 } }).Select(r => r.Place.Local).ToHashSet();
            bool owns = IrWalk.Descendants(computed).OfType<IrLet>().Any(l => (CreatesOwner(l.Value) || l.Value is IrHold) && !moved.Contains(l.Local));
            if (nest && TopLevel(computed) is [IrStore only] && only == store && !owns)
                plan.Initialized[local] = (declare, computed);
            else
            {
                plan.Evaluated[local] = (computed, store);
                plan.Consumed.Add(store);
            }
            plan.Nested[local] = computed;
        }
        plan.Consumed.UnionWith(statements);
    }
}
