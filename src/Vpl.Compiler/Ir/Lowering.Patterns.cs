using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Ir;

public sealed partial class IrLowering
{
    /// <summary>A name a pattern binds, and the value it is bound to, read from the matched value.</summary>
    sealed record Binding(LocalSymbol Local, Func<IrRvalue> Value);

    /// <summary>
    /// The names an expression's patterns bind, declared before the statement evaluating it: they
    /// are in scope for the whole statement, and assigned where their pattern matched (spec §6, Patterns).
    /// </summary>
    void DeclarePatternLocals(BoundExpression? expression)
    {
        foreach (var local in BoundTree.PatternLocals(expression))
            Add(new IrDeclare(Declare(local)) { IsBinding = true });
    }

    /// <summary>A statement whose condition binds names, in a block of its own that declares them.</summary>
    void WithPatternLocals(BoundExpression condition, Action lower)
    {
        if (!BoundTree.PatternLocals(condition).Any())
        {
            lower();
            return;
        }
        var scope = new IrBlock();
        Into(scope, () =>
        {
            DeclarePatternLocals(condition);
            lower();
        });
        Add(scope);
    }

    /// <summary>
    /// The value being matched, evaluated once. Matching runs no code, so a place is matched where
    /// it is; any other value is a temporary of the enclosing block.
    /// </summary>
    IrPlace Matched(BoundExpression value) => value switch
    {
        BoundThis { Type.IsReference: true } => new IrPlace(self!),
        _ when IsPlace(value) => Place(value, Use.Read),
        _ => new IrPlace(Let(Rvalue(value, Use.Read))),
    };

    /// <summary>
    /// The tests matching a pattern makes, in order, and what it binds. A test reads a projection
    /// only an earlier test makes valid, such as a variant's field, so each runs only if those
    /// before it passed (see <see cref="Conjunction"/>).
    /// </summary>
    void Tests(BoundPattern pattern, IrPlace value, List<Func<IrOperand>> tests, List<Binding> bindings)
    {
        switch (pattern)
        {
            case BoundDiscardPattern:
                break;
            case BoundBindingPattern binding:
                bindings.Add(new Binding(binding.Local, () => new IrUse(IrRead.Copy(value))));
                break;
            case BoundTypePattern { Type.IsNullableValue: true } test:
                tests.Add(() => Tested(new IrTest(IrTestKind.HasValue, IrRead.Borrow(value))));
                if (test.Binding is { } valueBinding)
                    bindings.Add(new Binding(valueBinding, () => new IrUse(IrRead.Copy(value.Assumed()))));
                break;
            case BoundTypePattern test:
            {
                // A value whose type is already a `Target` needs only to be non-null.
                bool upcast = Conversions.IsSubtype(test.Type.NonNull, test.Target);
                if (!upcast)
                    tests.Add(() => Tested(new IrTest(IrTestKind.Class, IrRead.Borrow(value)) { Target = test.Target }));
                else if (test.Type.IsNullable)
                    tests.Add(() => Tested(new IrTest(IrTestKind.NotNull, IrRead.Borrow(value))));
                if (test.Binding is { } local)
                    bindings.Add(new Binding(local, () => test.Type == test.Target ? new IrUse(IrRead.Copy(value)) : new IrConvert(test.Target, IrRead.Copy(value))));
                break;
            }
            case BoundConstantPattern { Value.Value: bool expected }:
                tests.Add(() => expected ? IrRead.Copy(value) : Tested(new IrUnary(BoundUnaryOperator.LogicalNot, IrRead.Copy(value))));
                break;
            case BoundConstantPattern constant:
                tests.Add(() => Tested(new IrBinary(BoundBinaryOperator.Equal, IrRead.Copy(value),
                    new IrConstant(constant.Value.Type, constant.Value.Value), VplType.Bool)));
                break;
            case BoundVariantPattern variant:
                tests.Add(() => Tested(new IrTest(IrTestKind.Tag, IrRead.Borrow(value)) { Variant = variant.Variant }));
                for (int i = 0; i < variant.Fields.Count; i++)
                    Tests(variant.Fields[i], value.VariantField(variant.Variant, i), tests, bindings);
                break;
            case BoundReferencePattern:
                throw new NotLoweredException("an 'is ref' pattern");
            default:
                throw new InvalidOperationException($"unexpected pattern {pattern.GetType().Name}");
        }
    }

    IrOperand Tested(IrRvalue test) => IrRead.Move(new IrPlace(Let(test)));

    /// <summary>
    /// Whether every test passes, each made only if those before it passed, and then
    /// <paramref name="matched"/>'s value. A binary <c>and</c> would evaluate all its operands first.
    /// </summary>
    IrOperand Conjunction(List<Func<IrOperand>> tests, int from, Func<IrOperand>? matched)
    {
        if (from == tests.Count)
            return matched?.Invoke() ?? IrConstant.Bool(true);
        if (from == tests.Count - 1 && matched is null)
            return tests[from]();
        return OperandOf(Chosen(VplType.Bool, tests[from], () => Conjunction(tests, from + 1, matched), () => IrConstant.Bool(false)));
    }

    /// <summary>A binding's value as its local's type.</summary>
    IrRvalue BoundValue(Binding binding)
    {
        var value = binding.Value();
        return value.Type == binding.Local.Type ? value : new IrConvert(binding.Local.Type, OperandOf(value));
    }

    /// <summary>
    /// <c>x is pattern</c>. Binding stores to existing locals, releasing their old values, which may
    /// run a destructor that changes the matched value, so every bound value is copied first. The
    /// first store needs no copy of its own: it reads its value before releasing anything.
    /// </summary>
    IrRvalue Is(BoundIs test)
    {
        var matched = Matched(test.Operand);
        var tests = new List<Func<IrOperand>>();
        var bindings = new List<Binding>();
        Tests(test.Pattern, matched, tests, bindings);
        if (bindings.Count == 0)
            return new IrUse(Conjunction(tests, 0, null));
        return new IrUse(Conjunction(tests, 0, () =>
        {
            var copies = bindings.Skip(1).Select(b => (b.Local, Copy: Let(BoundValue(b)))).ToList();
            Add(new IrStore(new IrPlace(locals[bindings[0].Local]), OperandOf(BoundValue(bindings[0]))));
            foreach (var (local, copy) in copies)
                Add(new IrStore(new IrPlace(locals[local]), IrRead.Move(new IrPlace(copy))));
            return IrConstant.Bool(true);
        }));
    }

    /// <summary>
    /// Whether a case matches, or null if it always does, and the names it binds; only a case
    /// with one pattern binds any.
    /// </summary>
    IrOperand? CaseCondition(IReadOnlyList<BoundPattern> patterns, IrPlace matched, List<Binding> bindings)
    {
        if (patterns.Count == 0)
            return null;
        var alternatives = new List<List<Func<IrOperand>>>();
        foreach (var pattern in patterns)
        {
            var tests = new List<Func<IrOperand>>();
            Tests(pattern, matched, tests, bindings);
            if (tests.Count == 0)
                return null;
            alternatives.Add(tests);
        }
        IrOperand Any(int from) => from == alternatives.Count - 1
            ? Conjunction(alternatives[from], 0, null)
            : OperandOf(Chosen(VplType.Bool, () => Conjunction(alternatives[from], 0, null), () => IrConstant.Bool(true), () => Any(from + 1)));
        return Any(0);
    }

    /// <summary>
    /// Tries the cases in order, as an <c>if</c> chain whose last <c>else</c> panics. A case's names
    /// are fresh locals copied from the matched value before its body runs.
    /// </summary>
    void Cases<T>(IReadOnlyList<BoundSwitchCase<T>> cases, IrPlace matched, Location location, Action<T> body)
    {
        bool completes = false;
        void From(int i)
        {
            if (i == cases.Count)
            {
                var loc = Loc(location);
                Add(new IrCheck(IrCheckKind.NoMatch, [], loc));
                Add(new IrUnreachable(loc));
                return;
            }
            var bindings = new List<Binding>();
            var condition = CaseCondition(cases[i].Patterns, matched, bindings);
            var then = new IrBlock();
            reachable = true;
            Into(then, () =>
            {
                foreach (var binding in bindings)
                    Add(new IrLet(Declare(binding.Local), BoundValue(binding)));
                body(cases[i].Body);
            });
            completes |= reachable;
            if (condition is null)
            {
                Add(then);
                return;
            }
            var otherwise = new IrBlock();
            Into(otherwise, () => From(i + 1));
            Add(new IrIf(condition, then, otherwise));
        }
        From(0);
        reachable = completes;
    }

    /// <summary>The value is evaluated once, and a temporary one lives until the switch ends (spec §6, switch).</summary>
    void Switch(BoundSwitch switchStatement)
    {
        var scope = new IrBlock();
        Into(scope, () =>
        {
            DeclarePatternLocals(switchStatement.Value);
            var matched = Matched(switchStatement.Value);
            Cases(switchStatement.Cases, matched, switchStatement.Location, statement =>
            {
                foreach (var inner in statement is BoundBlock block ? block.Statements : [statement])
                {
                    if (!reachable)
                        break;
                    Statement(inner);
                }
            });
        });
        Add(scope);
    }

    /// <summary>
    /// A switch expression's value, stored by each case that completes. A temporary matched value
    /// lives until the value is produced, and each case's temporaries until the case completes.
    /// </summary>
    IrRvalue SwitchExpression(BoundSwitchExpression switchExpression) =>
        SwitchExpression(switchExpression, switchExpression.Type, value => Settle(OperandOf(Rvalue(value, Use.Read))));

    /// <summary>A switch expression's cases, each storing what <paramref name="caseValue"/> makes of its value, as a <paramref name="type"/>.</summary>
    IrRvalue SwitchExpression(BoundSwitchExpression switchExpression, VplType type, Func<BoundExpression, IrOperand> caseValue)
    {
        var result = Temp(type);
        Add(new IrDeclare(result));
        var scope = new IrBlock();
        Into(scope, () =>
        {
            var matched = Matched(switchExpression.Value);
            Cases(switchExpression.Cases, matched, switchExpression.Location, value =>
            {
                DeclarePatternLocals(value);
                if (value.Type == VplType.Void)
                {
                    Add(new IrDo(Rvalue(value, Use.Read)));
                    Unreachable(value.Location);
                }
                else
                {
                    Add(new IrStore(new IrPlace(result), caseValue(value)));
                }
            });
        });
        Splice(scope);
        reachable = true;
        return new IrUse(IrRead.Move(new IrPlace(result)));
    }
}
