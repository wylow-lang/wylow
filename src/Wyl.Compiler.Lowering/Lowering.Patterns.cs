using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Lowering;

public sealed partial class IrLowering
{
    /// <summary>
    /// A name a pattern binds, the place it is bound to, whether the binding moves what it binds, and
    /// the kind of reference it is when it binds a reference.
    /// </summary>
    sealed record Binding(LocalSymbol Local, Func<IrPlace> Place, bool Moves, RefKind Reference = RefKind.None);

    /// <summary>
    /// The names an expression's patterns bind, declared before the statement evaluating it: they
    /// are in scope for the whole statement, and assigned where their pattern matched (spec patterns.bindings).
    /// </summary>
    void DeclarePatternLocals(BoundExpression? expression, bool? references = null)
    {
        // A moving binding's matched value is held in a local of the statement's own, so that what the
        // pattern leaves is released after the names (spec patterns.bindings.moves).
        if (references is null or false)
        {
            foreach (var test in MovingTests(expression))
            {
                if (helds.ContainsKey(test))
                    continue;
                var held = Temp(test.Operand.Type);
                Add(new IrDeclare(held) { Origin = at });
                helds[test] = held;
            }
        }
        foreach (var local in BoundTree.PatternLocals(expression).Where(l => references is null || (l.RefKind != RefKind.None) == references))
        {
            var declare = new IrDeclare(Declare(local)) { Origin = at };
            if (local.IsReferenceBinding)
                bindings[local] = (block, declare);
            Add(declare);
        }
    }

    /// <summary>The tests whose match moves the value they match, outside any lambda, which is lowered on its own.</summary>
    IEnumerable<BoundIs> MovingTests(BoundExpression? expression) => expression switch
    {
        null or BoundLambda => [],
        BoundIs test when Patterns.Bindings(test.Pattern).Any(local => Moves(local.Type)) => [test],
        _ => BoundTree.Children(expression).SelectMany(MovingTests),
    };

    /// <summary>The local each moving <c>is</c> holds the value it moves into.</summary>
    readonly Dictionary<BoundIs, IrLocal> helds = new(ReferenceEqualityComparer.Instance);

    /// <summary>Where each <c>is ref</c> name is declared, which its holder is declared just before.</summary>
    readonly Dictionary<LocalSymbol, (IrBlock Block, IrDeclare Declare)> bindings = [];

    /// <summary>
    /// The reference local each pattern name means in a guard, into the matched value the guard tests,
    /// for a binding whose value the case moves.
    /// </summary>
    readonly Dictionary<LocalSymbol, IrLocal> guardReferences = [];

    /// <summary>
    /// <c>r is ref T name</c>: stores the reference in the name and tests it. The hold of the object
    /// it points into is declared with the name, so that the object stays held while the name is in
    /// scope, and ends where the test fails (spec memory.nullable-references).
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
        if (!BoundTree.PatternLocals(condition).Any(l => references is null || (l.RefKind != RefKind.None) == references))
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
    /// before it passed (see <see cref="Conjunction"/>). The places the bindings take are built from
    /// the root each time they are asked for: what is matched first, and then what it left behind.
    /// </summary>
    void Tests(BoundPattern pattern, Func<IrPlace> value, List<Func<IrOperand>> tests, List<Binding> bindings)
    {
        switch (pattern)
        {
            case BoundDiscardPattern:
                break;
            case BoundBindingPattern binding:
                bindings.Add(new Binding(binding.Local, value, Moves(binding.Type)));
                break;
            case BoundReferencePattern reference:
                bindings.Add(new Binding(reference.Binding, value, Moves: false, reference.Binding.RefKind));
                break;
            case BoundNullPattern:
                tests.Add(() => IsNull(value()));
                break;
            case BoundTypePattern { Type.IsNullableValue: true } test:
                tests.Add(() => Tested(Present(value())));
                if (test.Binding is { } valueBinding)
                    bindings.Add(new Binding(valueBinding, () => value().Assumed(), Moves(valueBinding.Type)));
                break;
            case BoundTypePattern test:
            {
                // A value whose type is already a `Target` needs only to be non-null.
                bool upcast = Conversions.IsSubtype(test.Type.NonNull, test.Target, symbol);
                if (!upcast)
                    tests.Add(() => Tested(new IrTest(IrTestKind.Class, IrRead.Borrow(value())) { Target = test.Target }));
                else if (test.Type.IsNullable)
                    tests.Add(() => Tested(Present(value())));
                if (test.Binding is { } local)
                    bindings.Add(new Binding(local, value, Moves(local.Type)));
                break;
            }
            case BoundConstantPattern { Value.Value: bool expected }:
                tests.Add(() => expected ? IrRead.Copy(value()) : Tested(new IrUnary(BoundUnaryOperator.LogicalNot, IrRead.Copy(value()))));
                break;
            case BoundConstantPattern constant:
                tests.Add(() => Tested(new IrBinary(BoundBinaryOperator.Equal, IrRead.Copy(value()),
                    new IrConstant(constant.Value.Type, constant.Value.Value), WylType.Bool)));
                break;
            case BoundStructPattern structPattern:
            {
                Func<IrPlace> fields = value;
                if (structPattern.Type.IsNullableValue)
                {
                    tests.Add(() => Tested(Present(value())));
                    fields = () => value().Assumed();
                }
                for (int i = 0; i < structPattern.Fields.Count; i++)
                {
                    var symbol = structPattern.Symbols[i];
                    Tests(structPattern.Fields[i], () => fields().Field(symbol), tests, bindings);
                }
                break;
            }
            case BoundVariantPattern variant:
                tests.Add(() => Tested(new IrTest(IrTestKind.Tag, IrRead.Borrow(value())) { Variant = variant.Variant }));
                for (int i = 0; i < variant.Fields.Count; i++)
                {
                    int index = i;
                    Tests(variant.Fields[i], () => value().VariantField(variant.Variant, index), tests, bindings);
                }
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

    /// <summary>A binding's value as its local's type: its place's value copied or moved, or a reference to it.</summary>
    IrOperand BoundValue(Binding binding)
    {
        if (binding.Reference != RefKind.None)
            return IrRead.Copy(new IrPlace(Let(new IrAddr(binding.Reference, binding.Place()))));
        var read = binding.Moves ? IrRead.Move(binding.Place()) : IrRead.Copy(binding.Place());
        return read.Type == binding.Local.Type ? read : IrRead.Move(new IrPlace(Let(new IrConvert(binding.Local.Type, read))));
    }

    /// <summary>
    /// <c>x is pattern</c>. Binding stores to existing locals, releasing their old values, which may
    /// run a destructor that changes the matched value, so every bound value is copied first. The
    /// first store needs no copy of its own: it reads its value before releasing anything. A moving
    /// binding moves the whole matched value here, where the test is true, and the names take what
    /// they bind out of what is left.
    /// </summary>
    IrRvalue Is(BoundIs test)
    {
        if (test.Pattern is BoundReferencePattern reference)
            return ReferenceTest(test.Operand, reference.Binding);
        var matched = Matched(test.Operand);
        IrPlace root = matched;
        var tests = new List<Func<IrOperand>>();
        var bindings = new List<Binding>();
        Tests(test.Pattern, () => root, tests, bindings);
        if (bindings.Count == 0)
            return new IrUse(Conjunction(tests, 0, null));
        bool moves = bindings.Any(b => b.Moves);
        IrLocal? held = null;
        if (moves && !helds.TryGetValue(test, out held))
            Add(new IrDeclare(held = Temp(matched.Type)) { Origin = at });
        return new IrUse(Conjunction(tests, 0, () =>
        {
            if (held is not null)
            {
                Add(new IrStore(new IrPlace(held), IrRead.Move(root)) { Origin = at });
                root = new IrPlace(held);
            }
            var copies = bindings.Skip(1).Select(b => (b.Local, Copy: Let(new IrUse(BoundValue(b))))).ToList();
            Add(new IrStore(new IrPlace(locals[bindings[0].Local]), BoundValue(bindings[0])) { Origin = at });
            foreach (var (local, copy) in copies)
                Add(new IrStore(new IrPlace(locals[local]), IrRead.Move(new IrPlace(copy))) { Origin = at });
            return IrConstant.Bool(true);
        }));
    }

    /// <summary>
    /// Whether a case's patterns match, to be tested where it is called, or null if they always do,
    /// and the names they bind; only a case with one pattern binds any. Over a nullable value, every
    /// pattern but <c>null</c>, and <c>default</c>, first tests that the value isn't null; each case
    /// tests it itself, as it does the rest of the value, since a guard may have changed it.
    /// </summary>
    Func<IrOperand>? CaseCondition<T>(BoundSwitchCase<T> c, Func<IrPlace> matched, bool nullable, List<Binding> bindings)
    {
        var alternatives = new List<List<Func<IrOperand>>>();
        foreach (var pattern in c.Patterns)
        {
            var tests = new List<Func<IrOperand>>();
            if (pattern is BoundNullPattern)
                tests.Add(() => CaseNull(matched(), pattern.Location));
            else if (nullable)
            {
                tests.Add(() => Tested(Present(matched())));
                Tests(pattern, () => NonNull(matched()), tests, bindings);
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
            alternatives.Add([() => Tested(Present(matched()))]);
        }
        IrOperand Any(int from) => from == alternatives.Count - 1
            ? Conjunction(alternatives[from], 0, null)
            : OperandOf(Chosen(WylType.Bool, () => Conjunction(alternatives[from], 0, null), () => IrConstant.Bool(true), () => Any(from + 1)));
        return () => Any(0);
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
    /// are fresh locals copied from the matched value before its body runs, and before its guard.
    /// A moving binding moves the whole matched value where the case is chosen, and the names take
    /// what they bind out of what it leaves. The value is matched where it is, so a later case sees
    /// what a guard changed (spec patterns.matching).
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
            IrPlace root = matched;
            var bindings = new List<Binding>();
            var matches = CaseCondition(cases[i], () => root, nullable, bindings);
            bool moves = bindings.Any(b => b.Moves);
            if (cases[i].Guard is { } guard)
            {
                Guarded(i, guard, matches, bindings, moves, useRoot: place => root = place);
                return;
            }
            var condition = matches?.Invoke();
            var then = Then(i, () =>
            {
                IrLocal? held = moves ? HeldValue(matched) : null;
                if (held is not null)
                {
                    Add(new IrStore(new IrPlace(held), IrRead.Move(root)) { Origin = at });
                    root = new IrPlace(held);
                }
                foreach (var binding in bindings)
                {
                    Add(new IrDeclare(Declare(binding.Local)) { Origin = at });
                    Add(new IrStore(new IrPlace(locals[binding.Local]), BoundValue(binding)) { Origin = at });
                }
            });
            if (condition is null)
            {
                Add(then);
                return;
            }
            Add(new IrIf(condition, then, Otherwise(i)) { Origin = at });
        }

        IrBlock Then(int i, Action bind)
        {
            var then = new IrBlock { Origin = at };
            reachable = true;
            Into(then, () =>
            {
                bind();
                body(cases[i].Body);
            });
            completes |= reachable;
            return then;
        }

        IrBlock Otherwise(int i)
        {
            var otherwise = new IrBlock { Origin = at };
            Into(otherwise, () => From(i + 1));
            return otherwise;
        }

        // As `if (value is Pattern && guard)`: the names are declared around the case's `if`, and
        // assigned where the patterns matched, before the guard runs. In the guard, a name the move
        // would take is a read-only reference into the matched value; the others are as they will be
        // in the body.
        void Guarded(int i, BoundExpression guard, Func<IrOperand>? matches, List<Binding> bindings, bool moves, Action<IrPlace> useRoot)
        {
            var scope = new IrBlock { Origin = at };
            Into(scope, () =>
            {
                IrLocal? held = moves ? HeldValue(matched) : null;
                foreach (var binding in bindings)
                    Add(new IrDeclare(Declare(binding.Local)) { Origin = at });
                DeclarePatternLocals(guard);
                IrOperand Bound()
                {
                    var saved = new Dictionary<LocalSymbol, IrLocal>(guardReferences);
                    try
                    {
                        foreach (var binding in bindings)
                        {
                            if (binding.Moves)
                                guardReferences[binding.Local] = GuardReference(binding, RefKind.In, binding.Local.Type.NullableReference(RefKind.In));
                            else if (moves && binding.Reference != RefKind.None)
                                guardReferences[binding.Local] = GuardReference(binding, binding.Reference, binding.Local.Type);
                            else
                                Add(new IrStore(new IrPlace(locals[binding.Local]), BoundValue(binding)) { Origin = at });
                        }
                        return Condition(guard);
                    }
                    finally
                    {
                        guardReferences.Clear();
                        foreach (var (symbol, local) in saved)
                            guardReferences[symbol] = local;
                    }
                }
                var condition = matches is null ? Bound() : OperandOf(Chosen(WylType.Bool, matches, Bound, () => IrConstant.Bool(false)));
                Add(new IrIf(condition, Then(i, () =>
                {
                    if (held is not null)
                    {
                        Add(new IrStore(new IrPlace(held), IrRead.Move(matched)) { Origin = at });
                        useRoot(new IrPlace(held));
                    }
                    foreach (var binding in bindings.Where(b => b.Moves || held is not null && b.Reference != RefKind.None))
                        Add(new IrStore(new IrPlace(locals[binding.Local]), BoundValue(binding)) { Origin = at });
                }), Otherwise(i)) { Origin = at });
            });
            Add(scope);
        }
        From(0);
        reachable = completes;
    }

    /// <summary>A local holding the matched value a case moves into, declared in the case's block before its names.</summary>
    IrLocal HeldValue(IrPlace matched)
    {
        var held = Temp(matched.Type);
        Add(new IrDeclare(held) { Origin = at });
        return held;
    }

    /// <summary>The reference local a name means in a guard, referring into the matched value the guard tests.</summary>
    IrLocal GuardReference(Binding binding, RefKind kind, WylType type)
    {
        var reference = new IrLocal(binding.Local.Name, type, IrLocalKind.Reference, IrLocalRole.Variable) { RefKind = kind, Symbol = binding.Local };
        Add(new IrDeclare(reference) { Origin = at });
        Add(new IrStore(new IrPlace(reference), IrRead.Copy(new IrPlace(Let(new IrAddr(kind, binding.Place()))))) { Origin = at });
        return reference;
    }

    /// <summary>The value is evaluated once, and a temporary one lives until the switch ends (spec statements.switch).</summary>
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
