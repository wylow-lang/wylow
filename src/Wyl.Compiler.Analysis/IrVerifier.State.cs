using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Analysis;

public sealed partial class IrVerifier
{
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
        /// The in-use guards that are active, so that a write to a value they mark cannot make a
        /// reference into it dangle: releasing the value panics first (spec memory.holds).
        /// </summary>
        public HashSet<IrLocal> Guards { get; private init; } = [];

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
            Guards = [.. Guards],
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
                Guards = [.. a.Guards.Union(b.Guards)],
            };
        }

        public bool SameAs(State other) =>
            Reachable == other.Reachable && Assigned.SetEquals(other.Assigned) && MaybeAssigned.SetEquals(other.MaybeAssigned)
            && NonNull.SetEquals(other.NonNull) && AssignedFields.SetEquals(other.AssignedFields)
            && Moved.Keys.ToHashSet().SetEquals(other.Moved.Keys) && Ended.SetEquals(other.Ended) && Invalid.Keys.ToHashSet().SetEquals(other.Invalid.Keys)
            && Lambdas.SetEquals(other.Lambdas) && Guards.SetEquals(other.Guards)
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
}
