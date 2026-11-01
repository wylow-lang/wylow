using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Lowering;

public sealed partial class IrLowering
{
    /// <summary>A name a pattern binds, and the value it is bound to, read from the matched value.</summary>
    sealed record Binding(LocalSymbol Local, Func<IrRvalue> Value);

    /// <summary>
    /// The names an expression's patterns bind, declared before the statement evaluating it: they
    /// are in scope for the whole statement, and assigned where their pattern matched (spec §6, Patterns).
    /// </summary>
    void DeclarePatternLocals(BoundExpression? expression, bool? references = null)
    {
        foreach (var local in BoundTree.PatternLocals(expression).Where(l => references is null || l.IsReferenceBinding == references))
        {
            var declare = new IrDeclare(Declare(local)) { Origin = at };
            if (local.IsReferenceBinding)
                bindings[local] = (block, declare);
            Add(declare);
        }
    }

    /// <summary>Where each <c>is ref</c> name is declared, which its holder is declared just before.</summary>
    readonly Dictionary<LocalSymbol, (IrBlock Block, IrDeclare Declare)> bindings = [];

    /// <summary>
    /// <c>r is ref T name</c>: stores the reference in the name and tests it. The hold of the object
    /// it points into is declared with the name, so that the object stays held while the name is in
    /// scope, and ends where the test fails (03-memory-model.md, Nullable references).
    /// </summary>
    IrRvalue ReferenceTest(BoundExpression operand, LocalSymbol binding)
    {
        var name = locals[binding];
        int start = block.Statements.Count;
        var reference = Let(ReferenceValue(operand, binding.Type));
        var holder = Holder(reference, start);
        if (holder is not null)
        {
            var (declaredIn, declare) = bindings[binding];
            declaredIn.Statements.Insert(declaredIn.Statements.IndexOf(declare), new IrDeclare(holder) { Origin = at });
        }
        Add(new IrStore(new IrPlace(name), IrRead.Copy(new IrPlace(reference))) { Origin = at });
        var found = Let(new IrTest(IrTestKind.NotNull, IrRead.Copy(new IrPlace(name))));
        if (holder is not null)
            Add(new IrIf(IrRead.Copy(new IrPlace(found)), new IrBlock { Origin = at }, new IrBlock([new IrEnd(holder) { Origin = at }]) { Origin = at }) { Origin = at });
        return new IrUse(IrRead.Move(new IrPlace(found)));
    }

    /// <summary>The hold made since <paramref name="start"/> of the object the reference a call returns points into, if it needed one.</summary>
    IrLocal? Holder(IrLocal reference, int start)
    {
        var statements = block.Statements;
        IrRvalue? Definition(IrLocal local) => statements.Skip(start).OfType<IrLet>().LastOrDefault(l => l.Local == local)?.Value;
        var call = Definition(reference) switch
        {
            IrCall made => made,
            IrConvert { Operand: IrRead { Place: { IsDeref: false, Projections.Count: 0, Local: var converted } } } => Definition(converted) as IrCall,
            _ => null,
        };
        var receiver = call?.Receiver switch
        {
            IrRead read => read.Place.Local,
            IrRefArgument argument => argument.Place.Local,
            _ => null,
        };
        return receiver is not null && Definition(receiver) is IrHold ? receiver : null;
    }

    /// <summary>A statement whose condition binds names, in a block of its own that declares them.</summary>
    void WithPatternLocals(BoundExpression condition, Action lower, bool? references = null)
    {
        if (!BoundTree.PatternLocals(condition).Any(l => references is null || l.IsReferenceBinding == references))
        {
            lower();
            return;
        }
        var scope = new IrBlock { Origin = at };
        Into(scope, () =>
        {
            DeclarePatternLocals(condition, references);
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
            case BoundNullPattern:
                tests.Add(() => IsNull(value));
                break;
            case BoundTypePattern { Type.IsNullableValue: true } test:
                tests.Add(() => Tested(Present(value)));
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
                    tests.Add(() => Tested(Present(value)));
                if (test.Binding is { } local)
                    bindings.Add(new Binding(local, () => test.Type == test.Target ? new IrUse(IrRead.Copy(value)) : new IrConvert(test.Target, IrRead.Copy(value))));
                break;
            }
            case BoundConstantPattern { Value.Value: bool expected }:
                tests.Add(() => expected ? IrRead.Copy(value) : Tested(new IrUnary(BoundUnaryOperator.LogicalNot, IrRead.Copy(value))));
                break;
            case BoundConstantPattern constant:
                tests.Add(() => Tested(new IrBinary(BoundBinaryOperator.Equal, IrRead.Copy(value),
                    new IrConstant(constant.Value.Type, constant.Value.Value), WylType.Bool)));
                break;
            case BoundStructPattern structPattern:
            {
                var fields = value;
                if (structPattern.Type.IsNullableValue)
                {
                    tests.Add(() => Tested(Present(value)));
                    fields = value.Assumed();
                }
                for (int i = 0; i < structPattern.Fields.Count; i++)
                    Tests(structPattern.Fields[i], fields.Field(structPattern.Symbols[i]), tests, bindings);
                break;
            }
            case BoundVariantPattern variant:
                tests.Add(() => Tested(new IrTest(IrTestKind.Tag, IrRead.Borrow(value)) { Variant = variant.Variant }));
                for (int i = 0; i < variant.Fields.Count; i++)
                    Tests(variant.Fields[i], value.VariantField(variant.Variant, i), tests, bindings);
                break;
            default:
                throw new InvalidOperationException($"unexpected pattern {pattern.GetType().Name}");
        }
    }

    IrOperand Tested(IrRvalue test) => IrRead.Move(new IrPlace(Let(test)));

    /// <summary>Whether a nullable value or object isn't null.</summary>
    static IrTest Present(IrPlace value) => new(value.Type.IsNullableValue ? IrTestKind.HasValue : IrTestKind.NotNull, IrRead.Borrow(value));

    IrOperand IsNull(IrPlace value) => Tested(new IrUnary(BoundUnaryOperator.LogicalNot, Tested(Present(value))));

    /// <summary>
    /// A nullable value's value, once it is known not to be null. An object's is the same reference,
    /// which a binding converts to its non-null type (<see cref="BoundValue"/>).
    /// </summary>
    static IrPlace NonNull(IrPlace value) => value.Type.IsNullableValue ? value.Assumed() : value;

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
        return OperandOf(Chosen(WylType.Bool, tests[from], () => Conjunction(tests, from + 1, matched), () => IrConstant.Bool(false)));
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
        if (test.Pattern is BoundReferencePattern reference)
            return ReferenceTest(test.Operand, reference.Binding);
        var matched = Matched(test.Operand);
        var tests = new List<Func<IrOperand>>();
        var bindings = new List<Binding>();
        Tests(test.Pattern, matched, tests, bindings);
        if (bindings.Count == 0)
            return new IrUse(Conjunction(tests, 0, null));
        return new IrUse(Conjunction(tests, 0, () =>
        {
            var copies = bindings.Skip(1).Select(b => (b.Local, Copy: Let(BoundValue(b)))).ToList();
            Add(new IrStore(new IrPlace(locals[bindings[0].Local]), OperandOf(BoundValue(bindings[0]))) { Origin = at });
            foreach (var (local, copy) in copies)
                Add(new IrStore(new IrPlace(locals[local]), IrRead.Move(new IrPlace(copy))) { Origin = at });
            return IrConstant.Bool(true);
        }));
    }

    /// <summary>
    /// Whether a case matches, or null if it always does, and the names it binds; only a case
    /// with one pattern binds any. Over a nullable value, every pattern but <c>null</c>, and
    /// <c>default</c>, first tests that the value isn't null; each case tests it itself, as it does
    /// the rest of the value.
    /// </summary>
    IrOperand? CaseCondition<T>(BoundSwitchCase<T> c, IrPlace matched, bool nullable, List<Binding> bindings)
    {
        var alternatives = new List<List<Func<IrOperand>>>();
        foreach (var pattern in c.Patterns)
        {
            var tests = new List<Func<IrOperand>>();
            if (pattern is BoundNullPattern)
                tests.Add(() => CaseNull(matched, pattern.Location));
            else if (nullable)
            {
                tests.Add(() => Tested(Present(matched)));
                Tests(pattern, NonNull(matched), tests, bindings);
            }
            else
                Tests(pattern, matched, tests, bindings);
            if (tests.Count == 0)
                return null;
            alternatives.Add(tests);
        }
        if (c.IsDefault)
        {
            if (!nullable)
                return null;
            alternatives.Add([() => Tested(Present(matched))]);
        }
        IrOperand Any(int from) => from == alternatives.Count - 1
            ? Conjunction(alternatives[from], 0, null)
            : OperandOf(Chosen(WylType.Bool, () => Conjunction(alternatives[from], 0, null), () => IrConstant.Bool(true), () => Any(from + 1)));
        return Any(0);
    }

    /// <summary>
    /// <c>case null</c>'s test. On a source local it is also what the flow analysis checks the
    /// local may be, since otherwise the case could never match.
    /// </summary>
    IrOperand CaseNull(IrPlace matched, Location location)
    {
        if (matched is { IsDeref: false, Projections.Count: 0, Local: { IsTemporary: false, Symbol: not null } })
            Add(new IrCheck(IrCheckKind.MayBeNull, [IrRead.Borrow(matched)], null) { Origin = location });
        return IsNull(matched);
    }

    /// <summary>
    /// Tries the cases in order, as an <c>if</c> chain whose last <c>else</c> panics. A case's names
    /// are fresh locals copied from the matched value before its body runs.
    /// </summary>
    void Cases<T>(IReadOnlyList<BoundSwitchCase<T>> cases, IrPlace matched, bool nullable, Location location, Action<T> body)
    {
        bool completes = false;
        void From(int i)
        {
            if (i == cases.Count)
            {
                var loc = Loc(location);
                Add(new IrCheck(IrCheckKind.NoMatch, [], loc) { Origin = at });
                Add(new IrUnreachable(loc) { Origin = at });
                return;
            }
            var bindings = new List<Binding>();
            var condition = CaseCondition(cases[i], matched, nullable, bindings);
            var then = new IrBlock { Origin = at };
            reachable = true;
            Into(then, () =>
            {
                foreach (var binding in bindings)
                    Add(LetOf(Declare(binding.Local), BoundValue(binding)));
                body(cases[i].Body);
            });
            completes |= reachable;
            if (condition is null)
            {
                Add(then);
                return;
            }
            var otherwise = new IrBlock { Origin = at };
            Into(otherwise, () => From(i + 1));
            Add(new IrIf(condition, then, otherwise) { Origin = at });
        }
        From(0);
        reachable = completes;
    }

    /// <summary>The value is evaluated once, and a temporary one lives until the switch ends (spec §6, switch).</summary>
    void Switch(BoundSwitch switchStatement)
    {
        var scope = new IrBlock { Origin = at };
        Into(scope, () =>
        {
            DeclarePatternLocals(switchStatement.Value);
            var matched = Matched(switchStatement.Value);
            Cases(switchStatement.Cases, matched, switchStatement.Value.Type.IsNullable, switchStatement.Location, statement =>
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
        SwitchExpression(switchExpression, switchExpression.Type, value => OperandOf(Rvalue(value, Use.Read)));

    /// <summary>A switch expression's cases, each storing what <paramref name="caseValue"/> makes of its value, as a <paramref name="type"/>.</summary>
    IrRvalue SwitchExpression(BoundSwitchExpression switchExpression, WylType type, Func<BoundExpression, IrOperand> caseValue)
    {
        var result = Temp(type);
        Add(new IrDeclare(result) { Origin = at });
        var scope = new IrBlock { Origin = at, IsStatement = true };
        Into(scope, () =>
        {
            var matched = Matched(switchExpression.Value);
            Cases(switchExpression.Cases, matched, switchExpression.Value.Type.IsNullable, switchExpression.Location, value =>
            {
                DeclarePatternLocals(value);
                if (value.Type == WylType.Void)
                {
                    Add(DoOf(Rvalue(value, Use.Read)));
                    Unreachable(value.Location);
                }
                else
                {
                    Add(new IrStore(new IrPlace(result), caseValue(value)) { Origin = at });
                }
            });
        });
        Add(scope);
        reachable = true;
        return new IrUse(IrRead.Move(new IrPlace(result)));
    }
}
