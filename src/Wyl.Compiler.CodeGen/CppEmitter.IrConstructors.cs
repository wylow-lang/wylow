using System.Text;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;

namespace Wyl.Compiler.CodeGen;

/// <summary>A lowered class constructor's call of its base's or, chaining, another of its class's constructor, which C++ makes the mem-initializer (docs/design/ir.md, §5.11).</summary>
public sealed partial class CppEmitter
{
    static IrCall ConstructorCallOf(IrBlock constructorCall) => (IrCall)((IrDo)constructorCall.Statements[^1]).Value;

    /// <summary>
    /// Whether a class constructor's constructor call has more than one argument computed by statements.
    /// MSVC may evaluate the arguments of a braced mem-initializer in any order, so the constructor
    /// then delegates to one taking them as <c>wyl::Arguments</c>, which a lambda computes in order.
    /// They are released when the base constructor returns, the last first, as a statement's are.
    /// </summary>
    static bool NeedsArgumentsConstructor(IrFunction function) => function.ConstructorCall is { } constructorCall && constructorCall.Statements.Count(s => s is IrDeclare) > 1;

    /// <summary>The constructor a class's constructor delegates to, declared in the class, which calls the base or chained constructor with the arguments it is given.</summary>
    void EmitArgumentsConstructor(TypeSymbol s, int index, IrFunction function)
    {
        var call = ConstructorCallOf(function.ConstructorCall!);
        // A reference argument is stored as the address of its place, which the constructor reads back.
        var arguments = call.Arguments.Select((argument, i) => FunctionArgument(call.Method, i,
            argument is IrRefArgument ? $"(*wyl::argument<{i}>(arguments))" : $"wyl::argument<{i}>(arguments)")).ToList();
        if (call.Loc is { } loc)
            arguments.Add(LocText(loc));
        Line("template <class A>");
        Line($"{OwnName(s)}(wyl::BaseArguments<{index}>, A&& arguments) : {ObjectType(call.Method.Owner!)}{{{string.Join(", ", arguments)}}} {{}}");
    }

    /// <summary>The delegation to <see cref="EmitArgumentsConstructor"/>'s constructor, with a lambda computing the arguments.</summary>
    string ArgumentsConstructorText(IrFunction function, IrBlock constructorCall)
    {
        var call = ConstructorCallOf(constructorCall);
        var s = function.Symbol.ContainingType!;
        var computed = constructorCall.Statements.OfType<IrDeclare>().Select(d => d.Local).ToHashSet();
        var (enclosingOutput, enclosingIndent) = (output, indent);
        output = new StringBuilder();
        indent = 2;
        IrStatements(constructorCall);
        var values = call.Arguments.Select(a => a switch
        {
            // The constructor receives the address and reads the place back, so its 'in' or 'ref'
            // parameter still names what the caller gave rather than a copy of it.
            IrRefArgument reference => $"wyl::address_of({PlaceText(reference.Place)})",
            IrRead { Place: { IsDeref: false, Projections.Count: 0, Local: var local } } when computed.Contains(local) => $"std::move({Name(local)})",
            _ => ArgumentText(a),
        });
        Line($"return wyl::Arguments{{{string.Join(", ", values)}}};");
        var body = output.ToString();
        (output, indent) = (enclosingOutput, enclosingIndent);
        return $"{OwnName(s)}{{wyl::BaseArguments<{s.Constructors.IndexOf(function.Symbol)}>{{}}, [&] {{\n{body}    }}()}}";
    }

    /// <summary>
    /// The constructor call, as the mem-initializer's, whose arguments need no order but for at
    /// most one computed by statements, which runs them in an immediately invoked lambda.
    /// </summary>
    string ConstructorCallText(IrBlock constructorCall)
    {
        var call = ConstructorCallOf(constructorCall);
        var enclosingIndent = indent;
        indent = 1;
        var arguments = ArgumentsText(call.Method, call.Arguments, call.Loc);
        indent = enclosingIndent;
        return $"{ObjectType(call.Method.Owner!)}{{{string.Join(", ", arguments)}}}";
    }

    /// <summary>
    /// The constructor call prints as a mem-initializer. Unless the constructor delegates, at
    /// most one argument is computed by a block, which is printed inside it: as its value when
    /// everything nests into that and the block makes nothing whose release could be observed,
    /// which would then wait for the base constructor, and otherwise in an immediately invoked lambda.
    /// </summary>
    void PlanConstructorCall(IrFunction function, bool nest)
    {
        var constructorCall = function.ConstructorCall!;
        var statements = constructorCall.Statements;
        plan.Consumed.Add(constructorCall);
        if (NeedsArgumentsConstructor(function))
        {
            // The statements run in order in the delegating constructor's lambda, which returns the arguments.
            plan.Consumed.Add(statements[^1]);
            PlanBlock(constructorCall, nest);
            return;
        }
        for (int i = 0; i + 1 < statements.Count; i += 2)
        {
            var declare = (IrDeclare)statements[i];
            var computed = (IrBlock)statements[i + 1];
            var local = declare.Local;
            PlanBlock(computed, nest);
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
