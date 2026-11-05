using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Analysis;

public sealed partial class IrVerifier
{
    /// <summary>What a reference, view, pointer, or borrowed local refers to, which its validity depends on.</summary>
    readonly Dictionary<IrLocal, Dependency> dependencies = [];
    readonly HashSet<IrLocal> borrowingHolds = [];
    readonly Dictionary<IrLocal, IrRvalue> slotObjects = [];
    readonly List<IrLocal> pendingMoves = [];

    /// <summary>What each temporary, or <c>is ref</c> name, holds a copy of the value of, until either is given another.</summary>
    readonly Dictionary<IrLocal, IrLocal> copies = [];

    /// <summary>The reference each <c>test.not_null</c> temporary tested, and the references known to be null where they are.</summary>
    readonly Dictionary<IrLocal, IrLocal> nullTests = [];
    readonly HashSet<IrLocal> knownNull = [];

    /// <summary>
    /// What may be true at a point, on some path to it; <see cref="Assigned"/>, <see cref="NonNull"/>,
    /// and <see cref="AssignedFields"/> are what is true on every path.
    /// </summary>
    sealed class State
    {
        public bool Reachable { get; private init; } = true;
        public HashSet<IrLocal> Assigned { get; private init; } = [];

        /// <summary>The locals of nullable types known not to be null, as narrowing finds them (spec flow.narrowing).</summary>
        public HashSet<IrLocal> NonNull { get; private init; } = [];

        /// <summary>In a constructor, the fields it must assign that it has.</summary>
        public HashSet<FieldSymbol> AssignedFields { get; private init; } = [];
        public HashSet<IrLocal> MaybeAssigned { get; private init; } = [];

        /// <summary>The locals moved on some path, and where each was moved, which any later use reports.</summary>
        public Dictionary<IrLocal, Location?> Moved { get; private init; } = [];
        public HashSet<IrLocal> Ended { get; private init; } = [];

        /// <summary>Dependents that may no longer be used, and why.</summary>
        public Dictionary<IrLocal, string> Invalid { get; private init; } = [];

        /// <summary>
        /// The lambdas made on some path here, which code that runs may run. As in flow analysis, one
        /// may run from where it is made on, a loop's next iteration included.
        /// </summary>
        public HashSet<IrFunction> Lambdas { get; private init; } = [];

        /// <summary>
        /// For a <c>bool</c> temporary, what is known where it is true and where it is false, null
        /// where it can't be: how a pattern's names are known to be stored where it matched, and a
        /// local to be narrowed where a condition tested it. The sets are never changed once made.
        /// </summary>
        public Dictionary<IrLocal, Outcome> Outcomes { get; private init; } = [];

        public static State Unreachable => new() { Reachable = false };

        public Known Known => new([.. Assigned], [.. NonNull], new(Moved));

        public State Clone() => new()
        {
            Reachable = Reachable,
            Assigned = [.. Assigned],
            NonNull = [.. NonNull],
            AssignedFields = [.. AssignedFields],
            MaybeAssigned = [.. MaybeAssigned],
            Moved = new(Moved),
            Ended = [.. Ended],
            Invalid = new(Invalid),
            Outcomes = new(Outcomes),
            Lambdas = [.. Lambdas],
        };

        public static State Merge(State a, State b)
        {
            if (!a.Reachable)
                return b;
            if (!b.Reachable)
                return a;
            var invalid = new Dictionary<IrLocal, string>(a.Invalid);
            foreach (var (local, reason) in b.Invalid)
                invalid.TryAdd(local, reason);
            var known = a.Known.Meet(b.Known);
            var moved = new Dictionary<IrLocal, Location?>(a.Moved);
            foreach (var (local, at) in b.Moved)
                moved.TryAdd(local, at);
            var outcomes = new Dictionary<IrLocal, Outcome>();
            foreach (var local in a.Outcomes.Keys.Union(b.Outcomes.Keys))
            {
                var merged = a.OutcomeOf(local).Join(b.OutcomeOf(local));
                if (merged.WhenTrue?.Adds(known) == true || merged.WhenFalse?.Adds(known) == true)
                    outcomes[local] = merged;
            }
            return new()
            {
                Outcomes = outcomes,
                Assigned = known.Assigned,
                NonNull = known.NonNull,
                AssignedFields = [.. a.AssignedFields.Intersect(b.AssignedFields)],
                MaybeAssigned = [.. a.MaybeAssigned.Union(b.MaybeAssigned)],
                Moved = moved,
                Ended = [.. a.Ended.Union(b.Ended)],
                Invalid = invalid,
                Lambdas = [.. a.Lambdas.Union(b.Lambdas)],
            };
        }

        public bool SameAs(State other) =>
            Reachable == other.Reachable && Assigned.SetEquals(other.Assigned) && MaybeAssigned.SetEquals(other.MaybeAssigned)
            && NonNull.SetEquals(other.NonNull) && AssignedFields.SetEquals(other.AssignedFields)
            && Moved.Keys.ToHashSet().SetEquals(other.Moved.Keys) && Ended.SetEquals(other.Ended) && Invalid.Keys.ToHashSet().SetEquals(other.Invalid.Keys)
            && Lambdas.SetEquals(other.Lambdas)
            && Outcomes.Count == other.Outcomes.Count && Outcomes.All(o => other.Outcomes.TryGetValue(o.Key, out var same) && o.Value.SameAs(same));

        public Outcome OutcomeOf(IrLocal condition) => Outcomes.TryGetValue(condition, out var known) ? known : new(Known, Known);

        /// <summary>The locals moved on a branch's path, which replace what the merged state only knows some path moved.</summary>
        public void TakeMoved(IReadOnlyDictionary<IrLocal, Location?> moved)
        {
            Moved.Clear();
            foreach (var (local, at) in moved)
                Moved[local] = at;
        }

        /// <summary>A local given a value again is not moved anymore, on any path an outcome describes.</summary>
        public void ForgetMoved(IrLocal moved)
        {
            Moved.Remove(moved);
            foreach (var (condition, outcome) in Outcomes.ToList())
            {
                if (outcome.WhenTrue?.Moved.ContainsKey(moved) == true || outcome.WhenFalse?.Moved.ContainsKey(moved) == true)
                    Outcomes[condition] = new(outcome.WhenTrue?.WithoutMoved(moved), outcome.WhenFalse?.WithoutMoved(moved));
            }
        }

        /// <summary>A local declared again, as in a later iteration, starts over.</summary>
        public void Forget(IrLocal local)
        {
            Assigned.Remove(local);
            MaybeAssigned.Remove(local);
            ForgetMoved(local);
            Ended.Remove(local);
            Invalid.Remove(local);
            Outcomes.Remove(local);
            NonNull.Remove(local);
            Drop(local, unassigned: true);
        }

        /// <summary>A local that may have been given another value is no longer known not to be null, here or where a condition holds.</summary>
        public void Unnarrow(IrLocal local)
        {
            NonNull.Remove(local);
            Drop(local, unassigned: false);
        }

        void Drop(IrLocal local, bool unassigned)
        {
            foreach (var (condition, outcome) in Outcomes.ToList())
            {
                if (outcome.Mentions(local, unassigned))
                    Outcomes[condition] = new(outcome.WhenTrue?.Without(local, unassigned), outcome.WhenFalse?.Without(local, unassigned));
            }
        }
    }

    /// <summary>
    /// What is known on every path to a point: the locals assigned, those known not to be null, and
    /// those moved there. A condition's outcome keeps the moves of each side, so that a use after it
    /// is an error only where the path that moved the value is taken.
    /// </summary>
    sealed record Known(HashSet<IrLocal> Assigned, HashSet<IrLocal> NonNull, Dictionary<IrLocal, Location?> Moved)
    {
        public Known Meet(Known other)
        {
            var moved = new Dictionary<IrLocal, Location?>(Moved);
            foreach (var (local, at) in other.Moved)
                moved.TryAdd(local, at);
            return new([.. Assigned.Intersect(other.Assigned)], [.. NonNull.Intersect(other.NonNull)], moved);
        }

        public bool SameAs(Known other) =>
            Assigned.SetEquals(other.Assigned) && NonNull.SetEquals(other.NonNull) && Moved.Keys.ToHashSet().SetEquals(other.Moved.Keys);

        /// <summary>Whether it knows more than <paramref name="other"/>.</summary>
        public bool Adds(Known other) => Assigned.Any(l => !other.Assigned.Contains(l)) || NonNull.Any(l => !other.NonNull.Contains(l))
            || !Moved.Keys.ToHashSet().SetEquals(other.Moved.Keys);

        public bool Mentions(IrLocal local, bool assigned) => NonNull.Contains(local) || (assigned && Assigned.Contains(local));

        public Known Without(IrLocal local, bool unassigned) =>
            new(unassigned ? [.. Assigned.Where(l => l != local)] : Assigned, [.. NonNull.Where(l => l != local)], Moved);

        public Known WithoutMoved(IrLocal local)
        {
            var moved = new Dictionary<IrLocal, Location?>(Moved);
            moved.Remove(local);
            return new(Assigned, NonNull, moved);
        }
    }

    /// <summary>What is known when a condition is true and when it is false; null for one that can't happen.</summary>
    sealed record Outcome(Known? WhenTrue, Known? WhenFalse)
    {
        public Outcome Negated => new(WhenFalse, WhenTrue);

        public Outcome Join(Outcome other) => new(Meet(WhenTrue, other.WhenTrue), Meet(WhenFalse, other.WhenFalse));

        static Known? Meet(Known? a, Known? b) => a is null ? b : b is null ? a : a.Meet(b);

        public bool Mentions(IrLocal local, bool assigned) => WhenTrue?.Mentions(local, assigned) == true || WhenFalse?.Mentions(local, assigned) == true;

        public bool SameAs(Outcome other) => Same(WhenTrue, other.WhenTrue) && Same(WhenFalse, other.WhenFalse);

        static bool Same(Known? a, Known? b) => a is null ? b is null : b is not null && a.SameAs(b);
    }

    /// <summary>
    /// What a dependent needs: its roots, the locals whose storage, or with <c>Through</c>, whose
    /// object or target, it lies in; and what it refers to. An alias (a borrowed local) needs nothing
    /// to write that; a reference needs nothing to destroy it. One taken from a <c>Borrowable</c>
    /// that isn't borrowed needs no code to run. An <see cref="IrRead.Element"/> borrow needs only
    /// that nothing writes an element of its type, or a part of one.
    /// </summary>
    sealed record Dependency(IReadOnlyList<(IrLocal Local, bool Through)> Roots, IrAccess? Target, bool Alias, bool NeedsBorrow, bool Element = false)
    {
        public Dependency Union(Dependency other) => new(
            [.. Roots.Union(other.Roots)],
            Target == other.Target ? Target : IrAccess.Unknown,
            Alias || other.Alias,
            NeedsBorrow || other.NeedsBorrow,
            Element && other.Element);

        /// <summary>The dependency of a reference, view, pointer, or alias an rvalue makes, if it makes one.</summary>
        public static Dependency? Of(IrRvalue value, IrVerifier verifier) => value switch
        {
            IrRef reference => verifier.Into(reference.Place, alias: false),
            IrAddr address => verifier.Into(address.Place, alias: false),
            IrData data => verifier.Into(data.Place, alias: false),
            IrUse use => Of(use.Operand, verifier),
            IrConvert conversion => Of(conversion.Operand, verifier),
            IrCall call when call.Method.Symbol.ReturnRefKind != RefKind.None || call.Type.IsRefStruct => verifier.Returned(call),
            IrInvoke invoke when invoke.Signature.Symbol.ReturnRefKind != RefKind.None || invoke.Type.IsRefStruct => new([], IrAccess.Unknown, false, false),
            IrConstruct { Type.IsRefStruct: true } construction => Made(construction.Arguments.OfType<IrRead>(), verifier),
            IrLambda lambda => verifier.Captured(lambda.Function),
            IrChoose choice => verifier.Chosen(choice),
            _ => null,
        };

        public static Dependency? Of(IrOperand operand, IrVerifier verifier) => operand switch
        {
            IrRead { Ownership: IrOwnership.Borrow } read => verifier.Into(read.Place, alias: true) with { Element = read.Element },
            // A copy of a reference, view, or pointer points where it does, so it outlives the local it
            // was copied from; a non-escaping function value needs what its lambda captured. Neither
            // needs the local itself, whose scope, ending, or being moved from changes neither. An
            // owned value copied out of a borrowed local is the value itself, so it needs nothing.
            IrRead { Place: { IsDeref: false, Projections.Count: 0, Local: var local } }
                when !CopiesOwnedValue(local) && verifier.dependencies.TryGetValue(local, out var copied) =>
                copied with { Alias = false, Element = false },
            _ => null,
        };

        /// <summary>
        /// Whether copying the local copies an owned value rather than a reference into storage. A
        /// type parameter may be a view or pointer, which still points where the place did.
        /// </summary>
        static bool CopiesOwnedValue(IrLocal local) =>
            local.Kind == IrLocalKind.Borrowed && !local.Type.IsSecondClass && !local.Type.IsPointer
            && !local.Type.NonNull.IsTypeParameter;

        static bool PointsAway(IrLocal local) => local.Kind == IrLocalKind.Reference || local.Type.IsRefStruct || local.Type.IsPointer;

        /// <summary>A <c>ref struct</c> made from views and pointers views what they do.</summary>
        static Dependency Made(IEnumerable<IrRead> arguments, IrVerifier verifier)
        {
            var parts = arguments.Where(a => !a.Place.IsDeref && a.Place.Projections.Count == 0 && verifier.dependencies.ContainsKey(a.Place.Local))
                .Select(a => a.Place.Local).ToList();
            var target = parts.Count == 1 ? verifier.dependencies[parts[0]].Target! : IrAccess.Unknown;
            return new([.. parts.SelectMany(p => PointsAway(p) ? verifier.dependencies[p].Roots : [(p, true)]).Distinct()], target, false, false);
        }
    }

    /// <summary>
    /// A non-escaping lambda's body runs when it is called, so it needs what it refers into from
    /// around it to still be valid wherever the lambda is used.
    /// </summary>
    Dependency? Captured(IrFunction lambda)
    {
        var roots = IrWalk.Locals(lambda.Body).Distinct().Where(l => dependencies.ContainsKey(l) && Visible(l)).ToList();
        return roots.Count == 0 ? null : new([.. roots.Select(r => (r, true))], null, false, false);
    }

    /// <summary>A choice needs what each lambda it may call needs, rather than the lambdas' temporaries, which it takes over.</summary>
    Dependency? Chosen(IrChoose choice)
    {
        var roots = choice.Options.OfType<IrRead>()
            .Select(o => o.Place is { IsDeref: false, Projections.Count: 0, Local: var local } && dependencies.TryGetValue(local, out var dependency) ? dependency : null)
            .OfType<Dependency>().SelectMany(d => d.Roots).Distinct().ToList();
        return roots.Count == 0 ? null : new(roots, null, false, false);
    }

    Dependency Into(IrPlace place, bool alias) => new([(place.Local, place.IsDeref)], Target(place), alias, NeedsBorrow: false);

    /// <summary>A returned reference or view points into the receiver's storage: a struct place's, or an object's, which may be a <c>Borrowable</c>.</summary>
    Dependency Returned(IrCall call) => call.Receiver switch
    {
        IrRefArgument { Place: { Type.IsRefStruct: true } span } => new([(span.Local, true)], IrAccess.Unknown, false, false),
        IrRefArgument reference => Into(reference.Place, alias: false),
        IrRead { Place: var holder } => new([(holder.Local, true)], IrAccess.Unknown, false,
            NeedsBorrow: program.Bound.MayBeBorrowable(holder.Type) && !borrowingHolds.Contains(holder.Local)),
        _ => new([], IrAccess.Unknown, false, false),
    };

    /// <summary>The storage a place lies in, through what the references and pointers it goes through are known to refer to.</summary>
    IrAccess Target(IrPlace place) => IrAccess.Target(place, local => dependencies.GetValueOrDefault(local)?.Target);

    /// <summary>
    /// Whether code the effects run may write the place: a <c>mut</c> field that something it runs
    /// writes, storage the function can't see, or a local that a lambda made so far changes.
    /// </summary>
    bool CodeWrites(IrAccess place, IrEffects effects, State state) => writes.CodeMayWrite(place, effects.Runs, new LambdasMade(state, writes.Lambdas));

    /// <summary>The lambdas made on some path to a point, which code that runs there may run.</summary>
    sealed record LambdasMade(State State, LambdaWrites Writes) : IIrLambdaChanges
    {
        public bool MayChange(IrLocal local) => State.Lambdas.Any(l => Writes.Around(l).Contains(local));
    }

    /// <summary>Whether a lambda made on some path here may change the local when it runs.</summary>
    bool IsChangedByLambda(IrLocal local, State state) => new LambdasMade(state, writes.Lambdas).MayChange(local);

    public const string ReplacedNullable = "the nullable value or variant it points into may be replaced";

    /// <summary>Why the effects would make a dependent unusable, or null.</summary>
    string? Breaks(Dependency dependency, IrEffects effects, IReadOnlyList<IrAccess> writes, State state)
    {
        if (dependency.NeedsBorrow && effects.RunsCode)
            return "it points into a Borrowable that isn't borrowed, and code that may change its length runs";
        if (dependency.Target is not { } target)
            return null;
        if (dependency.Alias && (writes.Any(target.Overlaps) || CodeWrites(target, effects, state)))
            return "the place it borrows may be written";
        if (!dependency.Alias && (writes.Any(w => w.Destroys(target)) || (target.IsDestructible && CodeWrites(target, effects, state))))
            return ReplacedNullable;
        return null;
    }

    bool Visible(IrLocal local) => scopes.Any(s => s.Contains(local));

    void Apply(State state, IrEffects effects, string what)
    {
        RecordNullableWrites(effects);
        var writes = effects.Writes.Select(w => Target(w.Place)).ToList();
        foreach (var write in writes)
        {
            if (write is { Storage: IrStorage.Frame, Path.Count: 0, Local: { } replaced })
            {
                state.Unnarrow(replaced);
                Replaced(replaced);
            }
        }
        foreach (var (local, dependency) in dependencies.ToList())
        {
            if (!Visible(local) || state.Invalid.ContainsKey(local))
                continue;
            if (dependency.Element && this.writes.MayWrite(effects, IrAccess.Element(local.Type.NonNull)))
                Invalidate(state, local, $"an element of its type, or a part of one, may be written, by {what}");
            else if (Breaks(dependency.Element ? dependency with { Target = null } : dependency, effects, writes, state) is { } reason)
                Invalidate(state, local, $"{reason}, by {what}");
        }
        foreach (var write in effects.Writes.Where(w => !w.Place.IsDeref && w.Place.Projections.Count == 0))
            DependentsOf(write.Place.Local, state, $"'{N(write.Place.Local)}' may be reassigned by {what}", throughOnly: true);
        if (effects.RunsCode)
        {
            foreach (var local in scopes.SelectMany(s => s).Where(l => IsChangedByLambda(l, state)).ToList())
                DependentsOf(local, state, $"a lambda may reassign '{N(local)}' during {what}", throughOnly: true);
        }
    }

    void Invalidate(State state, IrLocal local, string reason)
    {
        if (!state.Invalid.TryAdd(local, reason))
            return;
        DependentsOf(local, state, $"'{N(local)}' was invalidated: {reason}");
    }

    void DependentsOf(IrLocal root, State state, string reason, bool throughOnly = false, IReadOnlySet<IrLocal>? except = null)
    {
        foreach (var (local, dependency) in dependencies.ToList())
        {
            if (local != root && Visible(local) && except?.Contains(local) != true && dependency.Roots.Any(r => r.Local == root && (r.Through || !throughOnly)))
                Invalidate(state, local, reason);
        }
    }

    /// <summary>
    /// What a <c>bool</c> temporary's value says, from the constant, condition, or null test it is
    /// made from. A source variable's says nothing, so that only a condition narrows (spec flow.narrowing).
    /// </summary>
    void Learn(IrLocal local, IrRvalue value, State state)
    {
        state.Outcomes.Remove(local);
        if (local.Type != WylType.Bool || SourceOf(local) is not null)
            return;
        Outcome? outcome = value switch
        {
            IrUse { Operand: var operand } => OutcomeOf(operand, state),
            IrUnary { Operator: BoundUnaryOperator.LogicalNot, Operand: var operand } => OutcomeOf(operand, state)?.Negated,
            IrTest { Kind: IrTestKind.NotNull or IrTestKind.HasValue, Operand: IrRead { Place: { IsDeref: false, Projections.Count: 0, Local: var tested } } } =>
                new(state.Known with { NonNull = [.. state.NonNull, .. new[] { tested, copies.GetValueOrDefault(tested) }.OfType<IrLocal>().Where(l => Narrowable(l, state))] }, state.Known),
            _ => null,
        };
        if (outcome is not null)
            state.Outcomes[local] = outcome;
    }

    /// <summary>
    /// Whether null checks narrow the local: not a <c>ref</c> or <c>in</c> one, which another
    /// reference to its variable could change, nor one a lambda made so far assigns, which any code
    /// may run. A <c>ref?</c> one always is, since nothing can make it refer elsewhere.
    /// </summary>
    bool Narrowable(IrLocal local, State state) => local.Type.MayBeNull
        && (local.Type.IsNullableReference || (local.RefKind == RefKind.None && local.Kind != IrLocalKind.Reference && !LambdaAssigns(local, state)));

    bool LambdaAssigns(IrLocal local, State state) => state.Lambdas.Any(l => writes.Lambdas.Assigns(l).Contains(local));

    /// <summary>The source local the local is, if it is one: a variable or parameter the source names.</summary>
    static LocalSymbol? SourceOf(IrLocal local) => local.IsTemporary ? null : local.Symbol is { IsTemporary: false } symbol ? symbol : null;

    /// <summary>
    /// Gives a local a value, which narrows it if the value can't be null; an <c>is ref</c> name only
    /// its test narrows, since it is assigned only where that matched. A temporary, or an <c>is ref</c>
    /// name, that copies a local's value tests it: a null test of the copy narrows both.
    /// </summary>
    void SetNonNull(IrLocal local, IrRvalue value, bool nonNull, State state)
    {
        state.Unnarrow(local);
        Replaced(local);
        if (nonNull && Narrowable(local, state) && local.Symbol?.IsReferenceBinding != true)
            state.NonNull.Add(local);
        var operand = value switch { IrUse use => use.Operand, IrConvert conversion => conversion.Operand, _ => null };
        if ((SourceOf(local) is null || local.Symbol!.IsReferenceBinding) && operand is IrRead { Place: { IsDeref: false, Projections.Count: 0, Local: var copied } })
            copies[local] = copies.GetValueOrDefault(copied) ?? copied;
    }

    /// <summary>What copied a local's value doesn't once the local is given another.</summary>
    void Replaced(IrLocal local)
    {
        copies.Remove(local);
        foreach (var copy in copies.Where(c => c.Value == local).Select(c => c.Key).ToList())
            copies.Remove(copy);
    }

    /// <summary>Whether a value can't be null as narrowing sees it: by its type, or as the value of a local known not to be.</summary>
    static bool ValueNonNull(IrRvalue value, State state) => value switch
    {
        IrUse { Operand: var operand } => OperandNonNull(operand, state),
        IrConvert { Operand: var operand } => !value.Type.MayBeNull || OperandNonNull(operand, state),
        IrAddr => true,
        _ => !value.Type.MayBeNull && value.Type != WylType.Null,
    };

    static bool OperandNonNull(IrOperand operand, State state) => operand switch
    {
        IrConstant constant => constant.Value is not null,
        IrRead { Place: { IsDeref: false, Projections.Count: 0, Local: var local } } => !local.Type.MayBeNull || state.NonNull.Contains(local),
        _ => !operand.Type.MayBeNull,
    };

    static Outcome? OutcomeOf(IrOperand operand, State state) => operand switch
    {
        IrConstant { Value: true } => new(state.Known, null),
        IrConstant { Value: false } => new(null, state.Known),
        IrRead { Place: { IsDeref: false, Projections.Count: 0, Local: var condition } } when state.Outcomes.TryGetValue(condition, out var known) => known,
        _ => null,
    };

    void Define(IrLocal local, IrRvalue value, State state)
    {
        if (value is IrTest { Kind: IrTestKind.NotNull, Operand: IrRead { Place: { IsDeref: false, Projections.Count: 0, Local: var tested } } })
            nullTests[local] = tested;
        else
            nullTests.Remove(local);
        Learn(local, value, state);
        SetNonNull(local, value, ValueNonNull(value, state), state);
        state.Assigned.Add(local);
        state.MaybeAssigned.Add(local);
        if (Dependency.Of(value, this) is { } dependency)
            dependencies[local] = dependency;
        else
            dependencies.Remove(local);
        if (value is IrHold { Borrows: true })
            borrowingHolds.Add(local);
        else
            borrowingHolds.Remove(local);
    }

    void Use(IrLocal local, State state, bool reads)
    {
        if (!Visible(local))
        {
            Error(declarations.ContainsKey(local) ? $"'{N(local)}' is used outside its scope" : $"'{N(local)}' isn't declared");
            return;
        }
        if (reads && !state.Assigned.Contains(local))
        {
            if (SourceOf(local) is { } symbol)
            {
                if (reportedUnassigned.Add(symbol))
                    Violation(l => Catalog.MayBeUnassigned(l, symbol.Name));
            }
            else
                Error($"'{N(local)}' may be read before it is stored");
        }
        if (reads)
        {
            // A move earlier in the same instruction is not in the state yet. A temporary's second
            // move reports itself as a compiler invariant, in <see cref="Operand"/>.
            bool movedEarlier = state.Moved.TryGetValue(local, out var movedAt);
            bool movedInInstruction = pendingMoves.Contains(local) && SourceOf(local) is not null;
            if (movedEarlier || movedInInstruction)
            {
                if (SourceOf(local) is { } moved)
                {
                    if (reportedMoved.Add(moved))
                        Violation(l => Catalog.UseAfterMove(l, moved.Name, movedEarlier ? movedAt : current?.Origin));
                }
                else
                    Error($"'{N(local)}' is used after it is moved");
            }
        }
        if (state.Ended.Contains(local))
            Error($"'{N(local)}' is used after it ends");
        if (state.Invalid.TryGetValue(local, out var reason))
            Error($"'{N(local)}' is used where it may no longer be valid: {reason}");
    }

    /// <param name="initializes">The place is a field a constructor must assign, which a store is assigning.</param>
    void Place(IrPlace place, State state, bool reads, bool initializes = false)
    {
        var local = place.Local;
        Use(local, state, reads || place.IsDeref);
        CheckNullableOrder(place);
        if (local == constructed && !initializes)
            CheckConstructed(place, state);
        if (place.IsDeref)
        {
            if (local.Deref is null)
                Error($"'{N(local)}' can't be dereferenced");
        }
        else if (local.Kind == IrLocalKind.Reference && place.Projections.Count > 0)
            Error($"'{N(local)}' is a reference: the place it refers to is '*{N(local)}'");

        var holder = place.BaseType;
        for (int i = 0; i < place.Projections.Count; i++)
        {
            var projection = place.Projections[i];
            switch (projection)
            {
                case IrField when holder.NonNull.IsReference && !(place.IsDeref && i == 0):
                    Error("a place dereferences only at its base: an object reached through a field must be read into a local first");
                    break;
                case IrElement element:
                    if (element.Index is not (IrConstant { Type.IsInteger: true } or IrRead { Type.IsInteger: true, Place: { IsDeref: false, Projections.Count: 0 } }))
                        Error("an element's index is an integer constant or local");
                    Operand(element.Index, state);
                    break;
            }
            if (projection.Type == WylType.Error)
                Error($"a projection doesn't apply to a '{holder}'");
            holder = projection.Type;
        }
    }

    void Operand(IrOperand operand, State state)
    {
        if (operand is not IrRead read)
            return;
        Place(read.Place, state, reads: true);
        var local = read.Place.Local;
        // No source copies a value that is not copyable: lowering moves every one of them.
        if (read.Ownership == IrOwnership.Copy && !read.Type.IsCopyable && !read.Type.ContainsTypeParameters)
            Error($"a '{read.Type}' is copied, which is not copyable");
        if (read.Ownership != IrOwnership.Move)
            return;
        if (read.Place.IsDeref || local.Kind != IrLocalKind.Value || local.IsThis || (!local.IsTemporary && read.Place.Projections.Any(p => p is not IrAssumed)))
        {
            if (SourceOf(local) is { } symbol)
                Violation(l => Catalog.MoveOutOfPlace(l, MovedPart(read.Place), read.Type.ToString()));
            else
                Error($"only a local's whole value can be moved, not '{N(local)}''s");
        }
        else if (pendingMoves.Contains(local))
        {
            if (SourceOf(local) is { } symbol)
            {
                if (reportedMoved.Add(symbol))
                    Violation(l => Catalog.UseAfterMove(l, symbol.Name, current?.Origin));
            }
            else
                Error($"'{N(local)}' is moved twice");
        }
        else
            pendingMoves.Add(local);
    }

    /// <summary>What a move would take from a place that is not a whole local, as a message names it.</summary>
    string MovedPart(IrPlace place)
    {
        var name = SourceOf(place.Local)?.Name ?? N(place.Local);
        if (place.Local.IsThis)
            return "out of 'this'";
        if (place.IsDeref)
            return $"out of what '{name}' refers to";
        return place.Projections.Count > 0 && place.Projections[0] is IrElement
            ? $"an element out of '{name}'"
            : $"a field out of '{name}'";
    }

    void Argument(IrArgument argument, State state)
    {
        switch (argument)
        {
            case IrOperand operand:
                Operand(operand, state);
                break;
            case IrRefArgument reference:
                Place(reference.Place, state, reads: true);
                break;
            case IrAddrArgument address:
                Place(address.Place, state, reads: true);
                break;
        }
    }

    /// <summary>Checks an rvalue's operands and types, applies its effects, and moves what it moves.</summary>
    void Rvalue(IrRvalue value, State state, IrLocal? defined)
    {
        pendingMoves.Clear();
        switch (value)
        {
            case IrUse use:
                Operand(use.Operand, state);
                if (use.Operand is IrRead { Ownership: IrOwnership.Borrow } && defined?.Kind != IrLocalKind.Borrowed)
                    Error("a borrowed value is only kept in a borrowed local");
                break;
            case IrUnary unary:
                Operand(unary.Operand, state);
                CheckLoc(unary.Loc);
                break;
            case IrBinary binary:
                Operand(binary.Left, state);
                Operand(binary.Right, state);
                CheckLoc(binary.Loc);
                break;
            case IrCall call:
                Call(call, state);
                break;
            case IrInvoke invoke:
                Operand(invoke.Callee, state);
                if (!invoke.Callee.Type.NonNull.IsFunction)
                    Error($"invokes a '{invoke.Callee.Type}', which isn't a function");
                Arguments(invoke.Signature, invoke.Arguments, state);
                break;
            case IrNew creation:
                Arguments(creation.Constructor, creation.Arguments, state);
                CheckLoc(creation.Loc);
                if (!creation.Type.IsClass)
                    Error($"'new' makes an object, not a '{creation.Type}'");
                Slot(creation.Slot, value, state);
                break;
            case IrConstruct construction:
                // A field left out fits only a memberwise constructor's parameter for that field.
                Arguments(construction.Constructor, construction.Arguments, state);
                CheckLoc(construction.Loc);
                break;
            case IrVariant variant:
                foreach (var field in variant.Fields)
                    Operand(field, state);
                var map = TypeMap.For(variant.Type);
                if (variant.Type.Symbol != variant.Variant.Owner || variant.Fields.Count != variant.Variant.Fields.Count
                    || variant.Fields.Where((f, i) => f.Type != map.Apply(variant.Variant.Fields[i].Type)).Any())
                    Error($"the fields don't fit variant '{variant.Variant.Name}' of '{variant.Type}'");
                break;
            case IrArray array:
                foreach (var element in array.Elements)
                    Operand(element, state);
                if (array.Type is not FixedArrayType fixedArray || array.Elements.Count != fixedArray.Length || array.Elements.Any(e => e.Type != fixedArray.Element))
                    Error($"the elements don't fit a '{array.Type}'");
                break;
            case IrDefault:
                break;
            case IrZeroed:
                if (function.Builds is null || defined != function.Struct)
                    Error("only a built struct starts as zeroed storage");
                break;
            case IrClosure closure:
            {
                IrLocal? capturedThis = null;
                var making = constructed;
                for (int i = 0; i < closure.Captures.Count && i < closure.Function.Captures.Count; i++)
                {
                    // What the source writes is used where the lambda's body uses it.
                    captureOrigin = FirstUse(closure.Function, closure.Function.Captures[i]);
                    // What a constructor makes is used where the body uses its copy.
                    bool copiesMade = closure.Captures[i] is IrRead { Place: { IsDeref: false, Projections.Count: 0 } place } && place.Local == making && making is not null;
                    if (copiesMade)
                        (capturedThis, constructed) = (closure.Function.Captures[i], null);
                    Operand(closure.Captures[i], state);
                    constructed = making;
                }
                captureOrigin = null;
                if (!closure.Function.IsEscaping)
                    Error("a closure is made from an escaping lambda");
                if (closure.Captures.Count != closure.Function.Captures.Count
                    || closure.Captures.Where((c, i) => c.Type != closure.Function.Captures[i].Type).Any())
                    Error("the captures don't fit the lambda's");
                Slot(closure.Slot, value, state);
                Closure(closure.Function, state, capturedThis);
                break;
            }
            case IrLambda lambda:
                if (lambda.Function.IsEscaping)
                    Error("an escaping lambda is made by a closure");
                Lambda(lambda.Function, state);
                foreach (var assigned in writes.Lambdas.Assigns(lambda.Function))
                    state.Unnarrow(assigned);
                state.Lambdas.Add(lambda.Function);
                break;
            case IrChoose choice:
                Operand(choice.Index, state);
                foreach (var option in choice.Options)
                    Operand(option, state);
                if (!choice.Index.Type.IsInteger || choice.Options.Any(o => !o.Type.NonNull.IsFunction))
                    Error("a choice selects a function with an integer");
                break;
            case IrRef reference:
                Place(reference.Place, state, reads: true);
                break;
            case IrAddr address:
                Place(address.Place, state, reads: true);
                break;
            case IrData data:
                Place(data.Place, state, reads: true);
                if (!data.Place.Type.IsFixedArray)
                    Error($"'data' points into a fixed array, not a '{data.Place.Type}'");
                break;
            case IrTest test:
                Operand(test.Operand, state);
                var type = test.Operand.Type;
                bool fits = test.Kind switch
                {
                    IrTestKind.NotNull => type.IsNullableObject || type.IsPointer,
                    IrTestKind.HasValue => type.IsNullableValue,
                    IrTestKind.Tag => test.Variant is { } v && type.Symbol == v.Owner,
                    IrTestKind.Class => test.Target is not null && type.NonNull.IsReference,
                    _ => false,
                };
                if (!fits)
                    Error($"a '{test.Kind}' test doesn't apply to a '{type}'");
                break;
            case IrConvert conversion:
                Operand(conversion.Operand, state);
                CheckLoc(conversion.Loc);
                break;
            case IrHold hold:
                Place(hold.Place, state, reads: true);
                if (!hold.Place.Type.MayShareStorage)
                    Error($"a hold holds an object or a string, not a '{hold.Place.Type}'");
                break;
            default:
                throw new InvalidOperationException($"unexpected IR rvalue {value.GetType().Name}");
        }

        var effects = IrEffects.Of(value);
        if (value is IrCall or IrInvoke or IrNew or IrConstruct)
            CheckInUse(value, effects, state);
        CheckMovedReferences(value);
        Apply(state, effects, Describe(value));
        CommitMoves(state);
    }

    /// <summary>
    /// A reference taken by an earlier operand refers into the value at the call, so moving the
    /// value in a later operand would leave it referring to nothing.
    /// </summary>
    void CheckMovedReferences(IrRvalue value)
    {
        if (pendingMoves.Count == 0)
            return;
        foreach (var argument in IrWalk.Operands(value))
        {
            var (place, kind) = argument switch
            {
                IrRefArgument reference => (reference.Place, reference.Kind),
                IrAddrArgument address => (address.Place, address.Kind),
                _ => (null, RefKind.None),
            };
            if (place is not { IsDeref: false, Local: var local } || !pendingMoves.Contains(local) || SourceOf(local) is not { } symbol)
                continue;
            if (reportedMoved.Add(symbol))
                Violation(l => Catalog.MoveWhileUsed(l, symbol.Name, FunctionSymbol.RefPrefix(kind).Trim()));
        }
    }

    /// <summary>A temporary moved by an instruction is gone once the instruction is done.</summary>
    void CommitMoves(State state)
    {
        foreach (var moved in pendingMoves)
        {
            state.Moved[moved] = current?.Origin;
            DependentsOf(moved, state, $"'{N(moved)}' was moved");
        }
        pendingMoves.Clear();
    }

    static string Describe(IrRvalue value) => value switch
    {
        IrCall call => $"the call of '{call.Method.Symbol.Name}'",
        IrInvoke => "the call of a function value",
        IrNew => "a 'new'",
        IrConstruct => "a construction",
        IrHold => "a borrow",
        _ => "an instruction",
    };

    void Call(IrCall call, State state)
    {
        var method = call.Method;
        // A by-value receiver is passed as the first argument, so the call has no receiver of its own.
        bool takesReceiver = method.Symbol.IsInstanceMethod && !method.Symbol.HasByValueReceiver;
        if (takesReceiver != call.Receiver is not null)
            Error(call.Receiver is null ? $"calls method '{method.Symbol.Name}' without a receiver" : $"calls '{method.Symbol.Name}' with a receiver");
        if (call.Receiver is { } receiver)
        {
            Argument(receiver, state);
            // What it returns points into what the receiver holds, which must outlive the call.
            bool returnsInto = method.Symbol.ReturnRefKind != RefKind.None || call.Type.IsRefStruct;
            if (returnsInto && receiver is IrRead read && !(read.Type.MayShareStorage && read.Place is { IsDeref: false, Projections.Count: 0 }))
                Error("a reference or view a method returns points into its receiver: an object or string in a local, or a struct passed as a place");
        }
        else if (method.Symbol.ReturnRefKind != RefKind.None)
            Error("only a method returns a reference, into its receiver");
        Arguments(method, call.Arguments, state);
        if (function.ChainsWith(call))
            state.AssignedFields.UnionWith(requiredFields);
        CheckLoc(call.Loc);
        if (method.Symbol.PassesLocation != call.Loc is not null)
            Error(call.Loc is null ? $"'{method.Symbol.Name}' is given its caller's location, which the call doesn't pass" : $"passes a location to '{method.Symbol.Name}', which takes none");
    }

    /// <summary>A function value a non-escaping function parameter takes, which the call converts to the parameter's type.</summary>
    static bool PassesFunction(WylType value, WylType parameter) =>
        parameter.NonNull.IsNonEscapingFunction && value.NonNull is FunctionType function && function.NonEscaping == parameter.NonNull && (parameter.IsNullable || !value.IsNullable);

    void Arguments(MethodReference method, IReadOnlyList<IrArgument> arguments, State state)
    {
        var parameters = method.Symbol.Parameters;
        int count = parameters.Count + method.ReceiverCount;
        if (arguments.Count != count)
        {
            Error($"passes {arguments.Count} arguments to '{method.Symbol.Name}', which takes {count}");
            return;
        }
        for (int i = 0; i < arguments.Count; i++)
        {
            Argument(arguments[i], state);
            var type = method.ArgumentType(i);
            var kind = method.ArgumentRefKind(i);
            bool fits = arguments[i] switch
            {
                IrAddrArgument address => type is NullableReferenceType nullable && address.Place.Type == nullable.Referenced && (address.Kind == nullable.Kind || nullable.Kind == RefKind.In),
                IrRefArgument reference => !type.IsNullableReference && reference.Place.Type == type && (reference.Kind == kind || kind == RefKind.In),
                IrOperand operand => operand.Type == type || (type is NullableReferenceType nullable && operand.Type is NullableReferenceType passed && passed.Referenced == nullable.Referenced) || PassesFunction(operand.Type, type),
                IrFieldDefault field => i >= method.ReceiverCount && parameters[i - method.ReceiverCount].Field == field.Field,
                _ => false,
            };
            if (fits && arguments[i] is IrOperand && kind != RefKind.None && !type.IsNullableReference)
                fits = false;
            if (!fits)
                Error(i < method.ReceiverCount
                    ? $"the receiver doesn't fit '{method.ReceiverType}'"
                    : $"argument {i + 1 - method.ReceiverCount} doesn't fit parameter '{FunctionSymbol.ParameterText(kind, type)} {parameters[i - method.ReceiverCount].Name}'");
        }
    }

    /// <summary>
    /// What a call is given in place, a reference or a borrowed value, is used while it runs, so the
    /// call's own effects must not invalidate it: a receiver that the callee could reassign meanwhile
    /// must be held first.
    /// </summary>
    void CheckInUse(IrRvalue value, IrEffects effects, State state)
    {
        if (!effects.RunsCode && effects.Writes.Count == 0)
            return;
        foreach (var argument in IrWalk.Passed(value)?.All ?? [])
        {
            var (place, alias) = argument switch
            {
                IrRefArgument reference => (reference.Place, false),
                IrAddrArgument address => (address.Place, false),
                IrRead { Ownership: IrOwnership.Borrow } read => (read.Place, true),
                _ => (null, false),
            };
            if (place is not null && WouldBreak(place, alias, effects, state) is { } reason)
            {
                if (!ReportNullableValue(place, reason, effects, state))
                    Error($"'{IrPrinter.Print(root, place)}' is used during {Describe(value)}, but {reason}");
            }
            // A lambda it is given runs during it, using what the lambda refers into.
            if (argument is IrRead { Place: { IsDeref: false, Projections.Count: 0, Local: { Type.NonNull.IsNonEscapingFunction: true } lambda } }
                && dependencies.ContainsKey(lambda) && !state.Invalid.ContainsKey(lambda))
            {
                var trial = state.Clone();
                Apply(trial, effects, "it");
                if (trial.Invalid.TryGetValue(lambda, out var broken))
                    Error($"'{N(lambda)}' runs during {Describe(value)}, but {broken}");
            }
        }
    }

    /// <summary>
    /// Reports a place in a narrowed nullable value of a source variable that the operation using it
    /// may set to null, which would destroy the place, as the user's error (spec memory.nullable-references): by
    /// passing the variable by 'ref', or by running a lambda that assigns it. Flow analysis keeps
    /// no narrowing past where such a lambda is made, so the lambda is made during the operation,
    /// after the place was found, as an argument is.
    /// </summary>
    bool ReportNullableValue(IrPlace place, string reason, IrEffects effects, State state)
    {
        if (!reason.StartsWith(ReplacedNullable) || !IsNarrowedSource(place))
            return false;
        if (effects.Writes.Any(w => !w.Place.IsDeref && w.Place.Projections.Count == 0 && w.Place.Local == place.Local))
            ReportNullable(place.Local, current, byLambda: false);
        else if (effects.RunsCode && IsChangedByLambda(place.Local, state))
            ReportNullable(place.Local, current, byLambda: true);
        else
            return false;
        return true;
    }

    /// <summary>A place in the narrowed value of a source variable, which the user's error is reported for.</summary>
    bool IsNarrowedSource(IrPlace place) =>
        diagnostics is not null && place.Local.Symbol is not null && !place.IsDeref && place.Local.Type.IsNullableValue && place.Projections.FirstOrDefault() is IrAssumed;

    void ReportNullable(IrLocal local, IrStatement? statement, bool byLambda)
    {
        var at = statement?.Origin ?? function.Symbol.Declaration?.Location;
        if (at is not { } location || !reportedNullables.Add((statement, local)))
            return;
        diagnostics!.Add(Catalog.NullableValueInUse(location, local.Symbol!.Name, byLambda));
    }

    /// <summary>
    /// A place in a narrowed value is evaluated after the operands written before it in the same
    /// expression, which must not have set the variable to null (spec memory.nullable-references).
    /// </summary>
    void CheckNullableOrder(IrPlace place)
    {
        if (!IsNarrowedSource(place) || current?.Origin is not { } within)
            return;
        foreach (var (local, at, by) in nullableWrites)
        {
            if (local == place.Local && by == function && at.Source == within.Source && at.Span != within.Span && at.Span.Start >= within.Span.Start && at.Span.End <= within.Span.End)
                ReportNullable(local, current, byLambda: false);
        }
    }

    /// <summary>
    /// Remembers where an instruction may set a source variable holding a nullable value, for
    /// <see cref="CheckNullableOrder"/>, with the function it is in: one in a lambda's body sets it
    /// where the lambda runs, not where the lambda is written.
    /// </summary>
    void RecordNullableWrites(IrEffects effects)
    {
        if (diagnostics is null || current?.Origin is not { } at)
            return;
        foreach (var write in effects.Writes.Where(w => !w.Place.IsDeref && w.Place.Projections.Count == 0 && w.Place.Local is { Symbol: not null, Type.IsNullableValue: true }))
            nullableWrites.Add((write.Place.Local, at, function));
    }

    string? WouldBreak(IrPlace place, bool alias, IrEffects effects, State state)
    {
        var writes = effects.Writes.Select(w => Target(w.Place)).ToList();
        if (Breaks(Into(place, alias), effects, writes, state) is { } reason)
            return reason;
        var local = place.Local;
        if (place.IsDeref && effects.Writes.Any(w => !w.Place.IsDeref && w.Place.Projections.Count == 0 && w.Place.Local == local))
            return $"it may reassign '{N(local)}'";
        if (place.IsDeref && effects.RunsCode && IsChangedByLambda(local, state))
            return $"a lambda may reassign '{N(local)}'";
        var trial = state.Clone();
        Apply(trial, effects, "it");
        return !state.Invalid.ContainsKey(local) && trial.Invalid.TryGetValue(local, out var invalid) ? invalid : null;
    }

    void Slot(IrLocal? slot, IrRvalue creation, State state)
    {
        if (slot is null)
            return;
        if (slot.Kind != IrLocalKind.Slot)
            Error($"'{N(slot)}' isn't a slot");
        Use(slot, state, reads: false);
        if (declarations.TryGetValue(slot, out var declaration) && declaration.LoopDepth != loopDepth)
            Error($"the object in slot '{N(slot)}' is made inside a loop or lambda nested in the slot's block, so it may be made more than once");
        if (slotObjects.TryGetValue(slot, out var other) && other != creation)
            Error($"slot '{N(slot)}' is given more than one object");
        slotObjects[slot] = creation;
    }

    /// <summary>
    /// A non-escaping lambda's body runs where it is called, during the code that made it, so it
    /// sees that code's locals. What is assigned where it is made stays assigned, but narrowing
    /// is not kept, since the code before it runs could undo it, except a <c>ref?</c> local's,
    /// which nothing can make refer elsewhere.
    /// </summary>
    void Lambda(IrFunction lambda, State state)
    {
        if (!verifiedLambdas.Add(lambda))
            Error($"lambda '{lambda.Name}' is made twice");
        var (savedFunction, savedLoops, savedCurrent, savedMoves, savedNarrowed) = (function, loops, current, pendingMoves.ToList(), narrowedOutside);
        function = lambda;
        loops = [];
        narrowedOutside = NarrowedAround(state);
        // It may run any number of times, as a loop's body does.
        loopDepth++;
        scopes.Add([]);
        var inner = state.Clone();
        inner.NonNull.RemoveWhere(l => !l.Type.IsNullableReference);
        foreach (var parameter in Entry(lambda))
            DeclareParameter(parameter, inner);
        inner = Block(lambda.Body, inner, newScope: false);
        if (inner.Reachable && lambda.Symbol.ReturnType != WylType.Void)
            MissingReturn(lambda);
        scopes.RemoveAt(scopes.Count - 1);
        loopDepth--;
        (function, loops, current, narrowedOutside) = (savedFunction, savedLoops, savedCurrent, savedNarrowed);
        pendingMoves.Clear();
        pendingMoves.AddRange(savedMoves);
    }

    /// <summary>An escaping lambda's body sees only its parameters and the copies its closure holds.</summary>
    /// <param name="capturedThis">In a constructor, the copy of what it makes, which the body may use as the constructor may where it is made.</param>
    void Closure(IrFunction lambda, State state, IrLocal? capturedThis)
    {
        if (!verifiedLambdas.Add(lambda))
            Error($"lambda '{lambda.Name}' is made twice");
        var saved = (function, loops, scopes, loopDepth, current, moves: pendingMoves.ToList(), narrowedOutside, constructed);
        narrowedOutside = NarrowedAround(state);
        constructed = capturedThis;
        VerifyFunction(lambda, root, capturedThis is null ? [] : state.AssignedFields);
        (function, loops, scopes, loopDepth, current, _, narrowedOutside, constructed) = saved;
        pendingMoves.Clear();
        pendingMoves.AddRange(saved.moves);
    }

    /// <summary>The source locals narrowed where a lambda is made, for explaining why they aren't in its body.</summary>
    HashSet<LocalSymbol> NarrowedAround(State state) => [.. narrowedOutside, .. state.NonNull.Select(l => l.Symbol).OfType<LocalSymbol>()];

    /// <summary>The origin of the first statement of a lambda's body that uses the local.</summary>
    static Location? FirstUse(IrFunction lambda, IrLocal local) =>
        IrWalk.Descendants(lambda.Body).FirstOrDefault(s => IrWalk.Operands(s).SelectMany(IrWalk.Places).SelectMany(IrWalk.PlaceLocals).Contains(local))?.Origin;
}
