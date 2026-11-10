using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Analysis;

public sealed partial class IrVerifier
{
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
        /// type parameter may be a pointer, which still points where the place did.
        /// </summary>
        static bool CopiesOwnedValue(IrLocal local) =>
            local.Kind == IrLocalKind.Borrowed && !local.Type.MayPointAway && !local.Type.NonNull.IsTypeParameter;

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

    /// <summary>
    /// A returned reference or view points into the receiver's storage: a struct place's, or an
    /// object's, which may be a <c>Borrowable</c>. A reference that a call gives a holder for may
    /// point into what the holder holds instead.
    /// </summary>
    Dependency Returned(IrCall call)
    {
        var into = call.Receiver switch
        {
            IrRefArgument { Place: { Type.IsRefStruct: true } span } => new([(span.Local, true)], IrAccess.Unknown, false, false),
            IrRefArgument reference => Into(reference.Place, alias: false),
            IrRead { Place: var holder } => new([(holder.Local, true)], IrAccess.Unknown, false,
                NeedsBorrow: program.Bound.MayBeBorrowable(holder.Type, function.Symbol) && !borrowingHolds.Contains(holder.Local)),
            _ => new Dependency([], IrAccess.Unknown, false, false),
        };
        return call.Holder is { } given ? into with { Roots = [.. into.Roots, (given, true)], Target = IrAccess.Unknown } : into;
    }

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

    bool Visible(IrLocal local) => local.Role == IrLocalRole.Static || scopes.Any(s => s.Contains(local));

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
        // A reference into a value that is in use cannot dangle: releasing or replacing the value
        // panics before it can (spec memory.holds).
        if (dependencies.TryGetValue(local, out var dependency) && dependency.Roots.Any(r => Guarded(state, r.Local)))
            return;
        if (!state.Invalid.TryAdd(local, reason))
            return;
        DependentsOf(local, state, $"'{N(local)}' was invalidated: {reason}");
    }

    /// <summary>Whether an in-use guard marks the value at the local as in use.</summary>
    bool Guarded(State state, IrLocal local) => state.Guards.Any(guard => guardTargets.GetValueOrDefault(guard) == local);

    void DependentsOf(IrLocal root, State state, string reason, bool throughOnly = false, IReadOnlySet<IrLocal>? except = null)
    {
        foreach (var (local, dependency) in dependencies.ToList())
        {
            if (local != root && Visible(local) && except?.Contains(local) != true && dependency.Roots.Any(r => r.Local == root && (r.Through || !throughOnly)))
                Invalidate(state, local, reason);
        }
    }
}
