using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Analysis;

public sealed partial class IrVerifier
{
    /// <summary>
    /// Whether a constructor starts from empty storage: a class constructor that does not chain, or a
    /// struct constructor that starts from a zeroed value and not from the constructor it calls.
    /// </summary>
    static bool StartsEmpty(IrFunction constructor) => constructor.Struct is { } made
        ? constructor.Body.Statements.FirstOrDefault() is IrLet { Value: IrZeroed } start && start.Local == made
        : !constructor.Chains;

    /// <summary>
    /// A constructor stores each field it may leave out whose initial value isn't empty storage.
    /// Lowering stores each one in its own statement, so where the stores are needs no flow.
    /// </summary>
    void CheckConstructorStart(IrFunction constructor, TypeSymbol owner)
    {
        var made = constructor.Struct ?? constructor.This;
        var stored = IrWalk.Descendants(constructor.Body)
            .OfType<IrStore>()
            .Where(store => store.Target.Local == made && store.Target.IsDeref == made?.Type.IsReference)
            .Select(store => store.Target.Projections is [IrField field] ? field.Field : null)
            .ToHashSet();
        foreach (var field in owner.Fields.Where(f => !f.RequiresAssignment && !stored.Contains(f)))
        {
            if (field.Initializer is not null || field.Type.HasDefaultCode)
                Report(constructor, null, $"field '{field.Name}' is left zeroed, which isn't its default value");
        }
    }

    void CheckFieldsAssigned(State state, Location at)
    {
        if (requiredFields.FirstOrDefault(f => !state.AssignedFields.Contains(f)) is { } field)
            Violation(l => Catalog.FieldUnassignedAtReturn(l, field.Name), at);
    }

    /// <summary>
    /// A constructor uses what it makes only once it has assigned every field that has no default:
    /// it may read a field it has assigned, and nothing else of it before then.
    /// </summary>
    void CheckConstructed(IrPlace place, State state)
    {
        var first = place.Projections.FirstOrDefault() as IrField;
        if (first is not null && place.IsDeref == place.Local.Type.IsReference)
        {
            if (requiredFields.Contains(first.Field) && !state.AssignedFields.Contains(first.Field))
                Violation(l => Catalog.FieldMayBeUnassigned(l, first.Field.Name));
        }
        else if (requiredFields.FirstOrDefault(f => !state.AssignedFields.Contains(f)) is { } unassigned)
            Violation(l => Catalog.ThisBeforeFieldsAssigned(l, unassigned.Name));
    }

    /// <summary>A struct constructor's value given as a whole, as a chained constructor makes it, has every field.</summary>
    void AssignedWhole(IrLocal local, State state)
    {
        // A local given a value again is not moved anymore (spec flow.moves).
        state.ForgetMoved(local);
        if (local == constructed)
            state.AssignedFields.UnionWith(requiredFields);
    }

    /// <summary>The field a constructor must assign that a store assigns as a whole, if it is one.</summary>
    FieldSymbol? Initialized(IrPlace target) =>
        target.Local == constructed && target.IsDeref == constructed.Type.IsReference && target.Projections is [IrField { Field: var field }] && requiredFields.Contains(field)
            ? field
            : null;

    /// <summary>
    /// A built struct's zeroed fields are not yet values: nothing reads it until it is returned, and
    /// it stores each field whose default value isn't zeroed storage. Lowering stores each field once,
    /// in its own statement, so where the stores are needs no flow.
    /// </summary>
    void CheckBuilt(IrFunction built)
    {
        if (built.Struct is not { } made || built.Body.Statements.FirstOrDefault() is not IrLet { Value: IrZeroed } start || start.Local != made)
        {
            Report(built, null, "a built struct starts as zeroed storage");
            return;
        }
        HashSet<FieldSymbol> stored = [];
        foreach (var statement in IrWalk.Descendants(built.Body))
        {
            if (statement is IrReturn)
                continue;
            IEnumerable<IrArgument> operands = IrWalk.Operands(statement);
            if (statement is IrStore { Target: { IsDeref: false, Projections: [IrField field] } target } store && target.Local == made)
            {
                stored.Add(field.Field);
                operands = [store.Value];
            }
            if (operands.SelectMany(IrWalk.Places).SelectMany(IrWalk.PlaceLocals).Contains(made))
                Report(built, statement, $"reads '{N(made)}' before it is built");
        }
        var map = TypeMap.For(made.Type);
        foreach (var field in made.Type.Symbol!.Fields.Where(f => !stored.Contains(f)))
        {
            if (field.Initializer is not null || map.Apply(field.Type).HasDefaultCode)
                Report(built, null, $"field '{field.Name}' is left zeroed, which isn't its default value");
        }
    }
}
