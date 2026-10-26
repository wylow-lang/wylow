using Vpl.Compiler.Analysis;
using Vpl.Compiler.Ir;
using Vpl.Compiler.Semantics;

namespace Vpl.Compiler.Optimize;

/// <summary>
/// Moves a named local into the instruction that uses it last, instead of copying it, which saves
/// a reference count increment and decrement (docs/design/ir.md §9.10). The copy would have kept
/// the object alive until the local's scope ends, so this is only done for types whose release
/// nothing can observe: it runs no destructor, and no weak reference can refer to an object it may destroy. The local
/// must be mentioned only by copies, the last in the same loop it is declared in and the only
/// mention in its instruction, so that C++ can't read it after it is moved from.
/// </summary>
sealed class LastUseMoves
{
    readonly IrProgram program;
    readonly Callees callees;
    readonly LambdaWrites lambdas;
    readonly Dictionary<VplType, bool> inert = [];
    /// <summary>What each <c>Weak</c> in the program refers to.</summary>
    readonly List<VplType> weakTargets;

    LastUseMoves(IrProgram program, Callees callees, LambdaWrites lambdas)
    {
        this.program = program;
        this.callees = callees;
        this.lambdas = lambdas;
        var stdlib = program.Bound.Stdlib;
        weakTargets = [.. program.Functions
            .SelectMany(f => IrWalk.Descendants(f.Body, intoLambdas: false).OfType<IrLet>().Select(l => l.Local).Concat(f.Parameters))
            .Select(l => l.Type.NonNull).Where(stdlib.IsWeak).Select(t => t.TypeArguments[0].NonNull).Distinct()];
    }

    /// <summary>How many reads were turned into moves.</summary>
    public int Count { get; private set; }

    public static LastUseMoves Apply(IrProgram program, Callees callees, LambdaWrites lambdas)
    {
        var result = new LastUseMoves(program, callees, lambdas);
        foreach (var function in program.Functions)
            result.Apply(function);
        return result;
    }

    /// <summary>What is known of one local while its function's instructions are walked in order.</summary>
    sealed class Mentions(int loops)
    {
        public int Loops { get; } = loops;
        public bool Bad { get; set; }
        public IrStatement? LastStatement { get; set; }
        public IrRead? Last { get; set; }
        public int InLastStatement { get; set; }
        public int LastLoops { get; set; }
    }

    void Apply(IrFunction function)
    {
        var mentions = new Dictionary<IrLocal, Mentions>();
        foreach (var parameter in function.Parameters)
            mentions[parameter] = new Mentions(0);
        Walk(function.Body, 0, mentions);
        foreach (var (local, found) in mentions)
        {
            if (!found.Bad && found.Last is { Place: { IsDeref: false, Projections.Count: 0 } } read && found.InLastStatement == 1 && found.LastLoops == found.Loops
                && Consumes(found.LastStatement!, read) && Movable(local))
            {
                read.Ownership = IrOwnership.Move;
                Count++;
            }
        }
    }

    static void Walk(IrStatement statement, int loops, Dictionary<IrLocal, Mentions> mentions)
    {
        switch (statement)
        {
            case IrBlock block:
                foreach (var inner in block.Statements)
                    Walk(inner, loops, mentions);
                return;
            case IrIf conditional:
                Note(conditional, loops, mentions);
                Walk(conditional.Then, loops, mentions);
                if (conditional.Else is { } otherwise)
                    Walk(otherwise, loops, mentions);
                return;
            case IrLoop loop:
                Walk(loop.Body, loops + 1, mentions);
                if (loop.Continue is { } update)
                    Walk(update, loops + 1, mentions);
                return;
        }
        Note(statement, loops, mentions);
    }

    static void Note(IrStatement statement, int loops, Dictionary<IrLocal, Mentions> mentions)
    {
        void Spoil(IrLocal local)
        {
            if (mentions.TryGetValue(local, out var found))
                found.Bad = true;
        }

        if (statement is IrLet { Local: var declared } let)
        {
            if (!declared.IsTemporary && declared.Symbol is not null)
                mentions[declared] = new Mentions(loops);
            // A lambda uses the locals it names where it is called.
            if (let.Value is IrLambda lambda)
            {
                foreach (var captured in IrWalk.Locals(lambda.Function.Body))
                    Spoil(captured);
            }
        }
        if (statement is IrEnd end)
            Spoil(end.Local);
        if (statement is IrDeclare declare)
            Spoil(declare.Local);
        var counts = new Dictionary<IrLocal, int>();
        foreach (var argument in IrWalk.Operands(statement))
        {
            foreach (var place in IrWalk.Places(argument))
            {
                foreach (var local in IrWalk.PlaceLocals(place))
                {
                    counts[local] = counts.GetValueOrDefault(local) + 1;
                    if (argument is not IrRead { Ownership: IrOwnership.Copy } read || place.Local != local)
                        Spoil(local);
                    else if (mentions.TryGetValue(local, out var found))
                        (found.LastStatement, found.Last, found.LastLoops) = (statement, read, loops);
                }
            }
        }
        foreach (var (local, n) in counts)
        {
            if (mentions.TryGetValue(local, out var found) && found.LastStatement == statement)
                found.InLastStatement = n;
        }
    }

    /// <summary>Whether a counted value can be released earlier than its scope ends without anyone telling.</summary>
    bool Movable(IrLocal local)
    {
        var type = local.Type;
        if (local.Kind != IrLocalKind.Value || local.Role is IrLocalRole.This or IrLocalRole.Capture || lambdas.MayChange(local) || !type.IsCounted
            || type.NonNull.IsNonEscapingFunction)
            return false;
        if (!inert.TryGetValue(type, out var result))
            inert[type] = result = callees.Of(new IrRunRelease(type)) is { Functions.Count: 0, RunsExtern: false } && !MayBeWeaklyReferred(type);
        return result;
    }

    /// <summary>Whether releasing a value of the type may destroy an object that a <c>Weak</c> refers to, which would see it die.</summary>
    bool MayBeWeaklyReferred(VplType type)
    {
        if (weakTargets.Count == 0)
            return false;
        if (callees.Cycles.Destroyable(type) is not { } destroyed)
            return true;
        return destroyed.Any(c => weakTargets.Any(target =>
            target.IsTypeParameter || target.Symbol == c.Symbol || Conversions.Supertypes(c).Any(super => super.Symbol == target.Symbol)));
    }

    /// <summary>Whether the instruction takes ownership of the value it reads, rather than only looking at it.</summary>
    static bool Consumes(IrStatement statement, IrRead read) => statement switch
    {
        IrLet { Value: var value } => Consumes(value, read),
        IrDo { Value: var value } => Consumes(value, read),
        IrStore store => store.Value == read,
        _ => false,
    };

    static bool Consumes(IrRvalue value, IrRead read) => value switch
    {
        IrUse use => use.Operand == read,
        IrCall call => call.Arguments.Contains(read),
        IrInvoke invoke => invoke.Arguments.Contains(read),
        IrNew creation => creation.Arguments.Contains(read),
        IrConstruct construction => construction.Arguments.Contains(read),
        IrVariant variant => variant.Fields.Contains(read),
        IrArray array => array.Elements.Contains(read),
        IrClosure closure => closure.Captures.Contains(read),
        _ => false,
    };
}
