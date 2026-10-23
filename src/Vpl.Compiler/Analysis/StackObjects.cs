using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Analysis;

/// <summary>An object made by <c>new</c> on the stack; a temporary's lives until the end of its statement.</summary>
public readonly record struct StackObject(BoundNew Creation, bool IsTemporary);

/// <summary>
/// Finds the objects made by <c>new</c> that no reference can outlive the scope making them, so
/// that they can be in its stack frame instead of on the heap: those held by a local, until the
/// local is given another value or its block ends, and temporaries, for their statement. An object
/// escapes if a reference to it may be stored anywhere but a local declared within that scope,
/// returned, captured by an escaping lambda, or given to a function that may do any of these with
/// it. Each function is summarized by which of its parameters, and whether its <c>this</c>, may
/// escape, including through the functions it calls, and which it may return, which makes the
/// call's value that argument.
/// </summary>
/// <remarks>
/// <para>
/// A local's object is looked for only where the statement making it is in the block declaring the
/// local, so that it runs at most once in each run of the block and its slot can be declared with
/// the local (03-memory-model.md, Stack allocation). The local must not be given another value
/// where no slot could be ended there: by a lambda, in a <c>for</c> loop's update, or through a
/// <c>ref</c>. Any other assignment to it ends the slots of the objects it may have held. So the
/// object's other references must be gone by the local's next assignment: a local it is copied to
/// must have a scope that no such assignment is in, and that assignment must not give the local
/// the object back.
/// </para>
/// <para>
/// A temporary is looked for only in a statement that evaluates each of its expressions at most
/// once and ends where the emitter ends its slot: a declaration, an assignment, an expression
/// statement, or a return, without a <c>try</c>, which runs part of the statement on its own.
/// </para>
/// </remarks>
public sealed partial class StackObjects
{
    /// <summary>
    /// What a function may let escape, its parameters by index and its <c>this</c>, and which of
    /// them it may return.
    /// </summary>
    sealed record Summary(IReadOnlySet<int> Parameters, bool This, IReadOnlySet<int> ReturnedParameters, bool ReturnsThis)
    {
        public static Summary Empty() => new(new HashSet<int>(), false, new HashSet<int>(), false);

        public int Size => Parameters.Count + (This ? 1 : 0) + ReturnedParameters.Count + (ReturnsThis ? 1 : 0);
    }

    readonly WholeProgram whole;
    readonly ElementAliasing elements;
    readonly Summaries<Summary> summaries;
    readonly Dictionary<BoundStatement, List<StackObject>> made = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<BoundAssignment, List<BoundNew>> ended = new(ReferenceEqualityComparer.Instance);

    StackObjects(WholeProgram whole, ElementAliasing elements)
    {
        this.whole = whole;
        this.elements = elements;
        summaries = new Summaries<Summary>(whole.Units, Summary.Empty, s => s.Size);
    }

    public static StackObjects Find(WholeProgram whole, ElementAliasing elements)
    {
        var result = new StackObjects(whole, elements);
        result.summaries.Solve(result.SummaryOf);
        foreach (var body in whole.Bodies)
            result.FindIn(body);
        return result;
    }

    /// <summary>
    /// The objects on the stack whose slots a statement declares, outside any lambda in it, in the
    /// order they are made: its temporaries, and for a declaration, every object its local holds.
    /// </summary>
    public IReadOnlyList<StackObject> MadeBy(BoundStatement statement) => made.TryGetValue(statement, out var found) ? found : [];

    /// <summary>The objects on the stack whose slots an assignment ends: those its local may have held before, which it releases.</summary>
    public IReadOnlyList<BoundNew> EndedBy(BoundAssignment assignment) => ended.TryGetValue(assignment, out var found) ? found : [];

    /// <summary>Where each object on the stack is made.</summary>
    public IEnumerable<Location> Locations => made.Values.SelectMany(m => m).Select(m => m.Creation.Location);

    Summary SummaryOf(Unit unit)
    {
        var function = unit.Symbol;
        var flow = new Flow(this, summarizing: true);
        foreach (var parameter in function.Parameters)
            flow.Declare(parameter);
        foreach (var statement in unit.Statements)
            flow.Statement(statement);
        foreach (var initializer in unit.Initializers)
            flow.Escape(initializer);
        if (unit.ImplicitBaseConstructor is { } baseConstructor)
            flow.Call(baseConstructor, [Flow.This], [], direct: true);
        return new Summary(
            function.Parameters.Index().Where(p => flow.Escapes(p.Item)).Select(p => p.Index).ToHashSet(),
            flow.Escapes(Flow.This),
            function.Parameters.Index().Where(p => flow.IsReturned(p.Item)).Select(p => p.Index).ToHashSet(),
            flow.IsReturned(Flow.This));
    }

    /// <summary>The summaries of the functions a call may run, or null if one of them has none, such as an extern function.</summary>
    List<Summary>? Targets(FunctionSymbol symbol, bool direct)
    {
        var found = new List<Summary>();
        foreach (var function in whole.Targets(symbol, direct))
        {
            if (summaries[function] is not { } summary)
                return null;
            found.Add(summary);
        }
        return found;
    }

    /// <summary>
    /// Whether an object of exactly the type is <c>Borrowable</c>, and the calls around its borrows
    /// may let it escape: the implementations of its class or the classes it extends.
    /// </summary>
    bool BorrowsLetEscape(VplType type)
    {
        if (whole.Program.Stdlib.Borrowable is not { } borrowable || !Conversions.IsSubtype(type, borrowable.Type))
            return false;
        HashSet<TypeSymbol?> classes = [type.Symbol, .. Conversions.Supertypes(type).Select(t => t.Symbol)];
        return borrowable.Methods.Values.SelectMany(m => m)
            .SelectMany(m => whole.Targets(m, direct: false))
            .Where(f => classes.Contains(f.ContainingType))
            .Any(f => summaries[f] is not { } summary || summary.This);
    }

    void FindIn(BoundStatement statement)
    {
        if (statement is BoundBlock block)
            FindIn(block.Statements);
        foreach (var inner in BoundTree.InnerStatements(statement))
            FindIn(inner);
    }

    void FindIn(IReadOnlyList<BoundStatement> statements)
    {
        var held = new Dictionary<LocalSymbol, List<BoundNew>>();
        var replacements = new Dictionary<LocalSymbol, List<Replacement>?>();
        for (int i = 0; i < statements.Count; i++)
        {
            var statement = statements[i];
            BoundNew? kept = null;
            if (Holding(statements, i) is var (holder, declaredAt, creation))
            {
                if (!replacements.TryGetValue(holder, out var replaced))
                    replacements[holder] = replaced = MayHold(holder) ? Replacements(statements, declaredAt, holder) : null;
                if (replaced is not null && HoldsAlone(statements, i, holder, creation, replaced))
                {
                    Add(statements[declaredAt], new StackObject(kept = creation, IsTemporary: false));
                    if (!held.TryGetValue(holder, out var creations))
                        held[holder] = creations = [];
                    creations.Add(creation);
                }
            }

            var temporaries = MayHoldTemporaries(statement)
                ? BoundTree.OwnExpressions(statement).SelectMany(Evaluated).OfType<BoundNew>().Where(c => !ReferenceEquals(c, kept)).ToList()
                : [];
            if (temporaries.Count > 0 && temporaries.Distinct(ReferenceEqualityComparer.Instance).Count() == temporaries.Count)
            {
                var flow = new Flow(this, summarizing: false);
                flow.Statement(statement);
                if (statement is BoundVariableDeclaration declaration)
                    flow.Undeclare(declaration.Local);
                foreach (var local in BoundTree.OwnExpressions(statement).SelectMany(BoundTree.PatternLocals))
                    flow.Undeclare(local);
                foreach (var temporary in temporaries.Where(t => !flow.Escapes(t)))
                    Add(statement, new StackObject(temporary, IsTemporary: true));
            }
        }

        foreach (var (holder, creations) in held)
        {
            foreach (var replacement in replacements[holder]!)
                ended[replacement.Assignment] = [.. creations.Where(c => !ReferenceEquals(c, Created(replacement.Assignment.Value)))];
        }
    }

    void Add(BoundStatement statement, StackObject found)
    {
        if (!made.TryGetValue(statement, out var list))
            made[statement] = list = [];
        list.Add(found);
    }

    /// <summary>A statement making an object for a local: the local, the index of its declaration among the statements, and the object.</summary>
    static (LocalSymbol Holder, int Declared, BoundNew Creation)? Holding(IReadOnlyList<BoundStatement> statements, int index)
    {
        switch (statements[index])
        {
            case BoundVariableDeclaration { Initializer: { } initializer } declaration when Created(initializer) is { } creation:
                return (declaration.Local, index, creation);
            case BoundAssignment { Target: BoundLocalAccess target } assignment when Created(assignment.Value) is { } creation:
                for (int i = 0; i < index; i++)
                {
                    if (statements[i] is BoundVariableDeclaration declaration && declaration.Local == target.Local)
                        return (target.Local, i, creation);
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>Whether the emitter declares a local as a plain variable, which a slot can be declared with.</summary>
    bool MayHold(LocalSymbol local) =>
        local is { RefKind: RefKind.None, IsAssignedByLambda: false } && !local.Type.IsRefStruct && !elements.AliasesElement(local);

    /// <summary>An assignment to a local, and the index of the statement it is in among those of the local's block.</summary>
    readonly record struct Replacement(BoundAssignment Assignment, int Index);

    /// <summary>
    /// The assignments to a local in the statements from its declaration on, or null if it may be
    /// given another value where no slot could be ended: in a <c>for</c> loop's update, or through
    /// a <c>ref</c>. Lambdas that assign it are ruled out by <see cref="MayHold"/>.
    /// </summary>
    static List<Replacement>? Replacements(IReadOnlyList<BoundStatement> statements, int declared, LocalSymbol local)
    {
        bool IsLocal(BoundExpression? expression) => expression is BoundLocalAccess access && access.Local == local;
        var found = new List<Replacement>();
        for (int index = declared; index < statements.Count; index++)
        {
            foreach (var statement in AllStatements(statements[index]))
            {
                switch (statement)
                {
                    case BoundFor { Update: BoundAssignment update } when IsLocal(update.Target):
                    case BoundVariableDeclaration { Local.RefKind: RefKind.Ref } reference when IsLocal(reference.Initializer):
                        return null;
                    case BoundAssignment assignment when IsLocal(assignment.Target):
                        found.Add(new Replacement(assignment, index));
                        break;
                }
            }
            var calls = BoundTree.AllExpressions(statements[index]).Select(BoundTree.CallOf).OfType<CallSite>();
            if (calls.Any(c => c.Arguments.Where((a, i) => IsLocal(a) && c.Method.ParameterRefKind(i) == RefKind.Ref).Any()))
                return null;
        }
        return found;
    }

    /// <summary>
    /// Whether the object a statement gives a local can't be referred to once its slot ends, which
    /// is where the local's block ends or the local is next assigned. The code that can use it
    /// before then runs up to the first assignment that is a statement of the block itself, which
    /// replaces it on every path.
    /// </summary>
    bool HoldsAlone(IReadOnlyList<BoundStatement> statements, int index, LocalSymbol holder, BoundNew creation, List<Replacement> replacements)
    {
        int end = replacements.Where(r => r.Index > index && ReferenceEquals(statements[r.Index], r.Assignment))
            .Select(r => r.Index).DefaultIfEmpty(statements.Count - 1).Min();
        var flow = new Flow(this, summarizing: false);
        flow.Declare(holder);
        for (int i = index; i <= end; i++)
            flow.Statement(statements[i]);
        if (flow.Escapes(creation))
            return false;

        var reached = flow.Reachable(creation).ToHashSet(ReferenceEqualityComparer.Instance);
        var later = replacements.Where(r => r.Index > index && r.Index <= end).ToList();
        if (later.Any(r => flow.Assigned(r.Assignment).Any(reached.Contains)))
            return false;
        reached.Remove(holder);
        for (int i = index + 1; i <= end; i++)
        {
            if (later.Any(r => r.Index == i) && HeldIn(statements[i]).Any(reached.Contains))
                return false;
            if (replacements.Any(r => r.Index >= i) && BlockScoped(statements[i]).Any(reached.Contains))
                return false;
        }
        return true;
    }

    /// <summary>The statement and those inside it, including in lambdas.</summary>
    static IEnumerable<BoundStatement> AllStatements(BoundStatement statement) =>
    [
        statement,
        .. BoundTree.InnerStatements(statement).SelectMany(AllStatements),
        .. BoundTree.OwnExpressions(statement).SelectMany(Evaluated).OfType<BoundLambda>().SelectMany(l => AllStatements(l.Body)),
    ];

    /// <summary>The statement and those inside it, except in lambdas, which run as functions of their own.</summary>
    static IEnumerable<BoundStatement> Executed(BoundStatement statement) =>
        [statement, .. BoundTree.InnerStatements(statement).SelectMany(Executed)];

    /// <summary>
    /// What may still hold a value when a statement ends but not after: the locals it declares,
    /// outside lambdas, whose own locals are gone once they return, and the values its switches
    /// match, which the emitter holds for the switch.
    /// </summary>
    static IEnumerable<object> HeldIn(BoundStatement statement) => Executed(statement).SelectMany<BoundStatement, object>(s =>
    [
        .. BoundTree.OwnExpressions(s).SelectMany(BoundTree.PatternLocals),
        .. s switch
        {
            BoundVariableDeclaration declaration => [declaration.Local],
            BoundForRange range => [range.Local],
            BoundSwitch choice => [choice, .. choice.Cases.SelectMany(c => c.Patterns).SelectMany(Patterns.Bindings)],
            _ => Array.Empty<object>(),
        },
    ]);

    /// <summary>The locals a statement declares for the rest of its block, rather than for itself.</summary>
    static IEnumerable<LocalSymbol> BlockScoped(BoundStatement statement) => statement switch
    {
        BoundVariableDeclaration declaration => [declaration.Local, .. BoundTree.PatternLocals(declaration.Initializer)],
        BoundAssignment or BoundExpressionStatement or BoundReturn => BoundTree.OwnExpressions(statement).SelectMany(BoundTree.PatternLocals),
        BoundSwitch choice => BoundTree.PatternLocals(choice.Value),
        _ => [],
    };

    bool MayHoldTemporaries(BoundStatement statement) => statement switch
    {
        BoundVariableDeclaration { Local: var local } => local.RefKind == RefKind.None && !local.Type.IsRefStruct && !elements.AliasesElement(local),
        BoundAssignment or BoundExpressionStatement or BoundReturn => true,
        _ => false,
    } && !BoundTree.OwnExpressions(statement).SelectMany(Evaluated).OfType<BoundTry>().Any();

    /// <summary>The expression and those inside it, except in lambdas, which run as functions of their own.</summary>
    static IEnumerable<BoundExpression> Evaluated(BoundExpression expression) =>
        [expression, .. BoundTree.Children(expression).SelectMany(Evaluated)];

    /// <summary>The object a variable's initializer or assigned value makes, if that is all it does.</summary>
    static BoundNew? Created(BoundExpression value) => value switch
    {
        BoundNew creation => creation,
        BoundConversion conversion when HoldsReference(conversion.Type) => Created(conversion.Operand),
        _ => null,
    };

    /// <summary>Whether a value of the type may be a reference to an object, as converting to it keeps the object's identity.</summary>
    static bool HoldsReference(VplType type) => type.NonNull.IsReference || type.NonNull.IsTypeParameter;
}
