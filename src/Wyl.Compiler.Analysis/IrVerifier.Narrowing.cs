using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Analysis;

public sealed partial class IrVerifier
{
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
        if (value is IrHold hold)
            holdPlaces[local] = hold.Place;
        else
            holdPlaces.Remove(local);
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
}
