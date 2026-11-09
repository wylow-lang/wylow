using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.Lowering;

/// <summary>
/// Marks a value in use while something refers to it (spec <c>memory.holds</c>): from the start of
/// a call it is a receiver or a <c>ref</c> or <c>in</c> argument of, or a call that reaches it
/// through <c>!</c> or <c>?.</c>, until that call returns; for the scope of a <c>ref</c> or
/// <c>in</c> variable or pattern binding into it; and for the body of its destructor. One bit per
/// value is saved in a hidden local when a use starts, and restored when it ends.
/// </summary>
static class InUse
{
    public static void Apply(IrProgram program)
    {
        foreach (var function in program.Functions)
            Apply(function);
    }

    static void Apply(IrFunction function)
    {
        if (function.Symbol is { IsDestructor: true, ContainingType.Kind: TypeKind.Struct } && function.This is { } self)
            function.Body.Statements.Insert(0, GuardLet(new IrPlace(self), function.Body));
        Process(function.Body, function.Symbol, DeclaringBlocks(function.Body));
    }

    static Dictionary<IrLocal, IrBlock> DeclaringBlocks(IrBlock body)
    {
        var blocks = new Dictionary<IrLocal, IrBlock>();
        void Walk(IrBlock block)
        {
            foreach (var statement in block.Statements)
            {
                if (statement is IrDeclare declare)
                    blocks[declare.Local] = block;
                foreach (var inner in InnerBlocks(statement))
                    Walk(inner);
            }
        }
        Walk(body);
        return blocks;
    }

    static IEnumerable<IrBlock> InnerBlocks(IrStatement statement) => statement switch
    {
        IrBlock block => [block],
        IrIf conditional => conditional.Else is null ? [conditional.Then] : [conditional.Then, conditional.Else],
        IrLoop loop => loop.Continue is null ? [loop.Body] : [loop.Body, loop.Continue],
        _ => [],
    };

    /// <summary>A guard for each value the statement uses in place, and for each reference it starts.</summary>
    static void Process(IrBlock block, FunctionSymbol function, Dictionary<IrLocal, IrBlock> declaredIn)
    {
        var statements = block.Statements.ToList();
        block.Statements.Clear();
        // The places the block's reference temporaries refer to, which a store of one copies.
        var places = new Dictionary<IrLocal, IrPlace>();
        // The guards whose reference is made by a store, which end after its last read unless a
        // lambda made in the block reads it.
        var endsAfter = new Dictionary<int, List<IrLocal>>();
        foreach (var (statement, index) in statements.Select((s, i) => (s, i)))
        {
            foreach (var inner in InnerBlocks(statement))
                Process(inner, function, declaredIn);
            if (statement is IrLet { Value: IrRef reference } declared)
                places[declared.Local] = reference.Place;
            else if (statement is IrLet { Value: IrAddr address } addressed)
                places[addressed.Local] = address.Place;
            var guards = new List<IrLocal>();
            foreach (var place in CallGuards(statement, function))
            {
                var guard = GuardLet(place, statement);
                block.Statements.Add(guard);
                guards.Add(guard.Local);
            }
            block.Statements.Add(statement);
            // A return ends the function, and the block's cleanup restores the guard.
            if (statement is not IrReturn)
            {
                foreach (var guard in guards)
                    block.Statements.Add(new IrEnd(guard) { Origin = statement.Origin });
            }
            if (Reference(statement, places) is { } referenced)
            {
                // A store makes a reference into a local declared without a value, as a pattern
                // binding is.
                bool stored = statement is IrStore;
                IrBlock? scope = stored && declaredIn.TryGetValue(referenced.Local, out var declaredInBlock) && declaredInBlock != block
                    ? declaredInBlock
                    : null;
                foreach (var place in Guards(referenced.Place, function, forCall: false))
                {
                    var guard = GuardLet(place, statement);
                    block.Statements.Add(guard);
                    if (scope is not null)
                        scope.Statements.Add(new IrDeclare(guard.Local) { Origin = statement.Origin });
                    else if (stored && LastRead(statements, index, referenced.Local) is { } last)
                        (endsAfter.TryGetValue(last, out var list) ? list : endsAfter[last] = []).Add(guard.Local);
                }
            }
            if (endsAfter.TryGetValue(index, out var ends))
            {
                foreach (var guard in ends)
                    block.Statements.Add(new IrEnd(guard) { Origin = statement.Origin });
            }
        }
    }

    static int? LastRead(List<IrStatement> statements, int index, IrLocal local)
    {
        int? last = index;
        for (int next = index; next < statements.Count; next++)
        {
            if (ReadsInLambda(statements[next], local))
                return null;
            if (Reads(statements[next], local, intoLambdas: false))
                last = next;
        }
        return last;
    }

    static bool ReadsInLambda(IrStatement statement, IrLocal local) =>
        IrWalk.Descendants(statement)
            .OfType<IrLet>()
            .Select(let => let.Value)
            .Any(value => value switch
            {
                IrLambda lambda => Reads(lambda.Function.Body, local, intoLambdas: true),
                IrClosure closure => Reads(closure.Function.Body, local, intoLambdas: true),
                _ => false,
            });

    /// <summary>Whether the statement, or one inside it, reads the local.</summary>
    static bool Reads(IrStatement statement, IrLocal local, bool intoLambdas) =>
        IrWalk.Descendants(statement, intoLambdas)
            .SelectMany(IrWalk.Operands)
            .SelectMany(IrWalk.Places)
            .Any(place => place.Local == local);

    static IrLet GuardLet(IrPlace place, IrStatement origin) =>
        new(new IrLocal("inuse", WylType.Bool, IrLocalKind.InUse, IrLocalRole.Temporary), new IrInUse(place)) { Origin = origin.Origin };

    /// <summary>The values a call in the statement uses in place, which are in use while it runs.</summary>
    static IEnumerable<IrPlace> CallGuards(IrStatement statement, FunctionSymbol function)
    {
        if (Value(statement) is not { } value || IrWalk.Passed(value) is not { } passed || TakesOut(passed.Method.Symbol))
            yield break;
        foreach (var argument in passed.All)
        {
            var place = argument switch
            {
                IrRefArgument reference => reference.Place,
                IrAddrArgument address => address.Place,
                IrRead { Ownership: IrOwnership.Borrow } read => read.Place,
                _ => null,
            };
            if (place is null)
                continue;
            foreach (var guard in Guards(place, function, forCall: true))
                yield return guard;
        }
    }

    /// <summary>The rvalue a statement evaluates, where its call or construction is.</summary>
    static IrRvalue? Value(IrStatement statement) => statement switch
    {
        IrLet let => let.Value,
        IrDo run => run.Value,
        _ => null,
    };

    /// <summary>
    /// <c>replace</c>, <c>swap</c>, and <c>take</c> take a value out of a place, and their own
    /// <c>ref</c> arguments do not make it in use (spec <c>library.replace</c>). The standard
    /// library's <c>takeValue</c> moves an isolated box's value out the same way, and a mark set on
    /// that value would leave with it.
    /// </summary>
    static bool TakesOut(FunctionSymbol symbol) =>
        symbol.BuiltinCppName is "wyl::replace" or "wyl::swap" or "wyl::take" or "wyl::threading::takeValue";

    /// <summary>The reference local a statement makes, and the place it refers to.</summary>
    static (IrPlace Place, IrLocal Local)? Reference(IrStatement statement, Dictionary<IrLocal, IrPlace> places) => statement switch
    {
        IrLet { Local.Kind: IrLocalKind.Reference, Local.Symbol: not null, Value: var value } let when Referent(value) is { } place =>
            (place, let.Local),
        IrStore { Target: { IsDeref: false, Projections.Count: 0, Local: var reference, Local.Kind: IrLocalKind.Reference, Local.Symbol: not null }, Value: var value }
            when Referent(value, places) is { } place => (place, reference),
        _ => null,
    };

    static IrPlace? Referent(IrRvalue value) => value switch
    {
        IrRef reference => reference.Place,
        IrAddr address => address.Place,
        _ => null,
    };

    static IrPlace? Referent(IrOperand value, Dictionary<IrLocal, IrPlace> places) => value switch
    {
        IrRead { Place: { IsDeref: false, Projections.Count: 0, Local: var temp } } when places.TryGetValue(temp, out var place) => place,
        _ => null,
    };

    /// <summary>
    /// The values at the place that are in use while it is used: every unique struct with a
    /// destructor the place is in or reaches into, a type parameter that may be one, an enum a
    /// reference reaches into the payload of, and a non-copyable nullable reached through
    /// <c>!</c> by a call.
    /// </summary>
    static IEnumerable<IrPlace> Guards(IrPlace place, FunctionSymbol function, bool forCall)
    {
        var type = place.BaseType;
        for (int i = 0; i <= place.Projections.Count; i++)
        {
            if (i > 0)
                type = place.Projections[i - 1].Type;
            if (type == WylType.Error)
                continue;
            bool more = i < place.Projections.Count;
            bool destructor = !type.IsReference && type.Symbol is { Destructor: not null } && !type.IsNullable;
            bool parameter = type.IsTypeParameter && !type.IsCopyableIn(function);
            bool payload = more && type.IsEnum && type.IsUniqueType;
            bool throughBang = forCall && more && type.IsNullableValue && place.Projections[i] is IrAssumed && !type.NonNull.IsCopyableIn(function);
            if (destructor || parameter || payload || throughBang)
                yield return Prefix(place, i);
        }
    }

    static IrPlace Prefix(IrPlace place, int count) =>
        count == place.Projections.Count ? place : new IrPlace(place.Local, place.IsDeref, [.. place.Projections.Take(count)]);
}
