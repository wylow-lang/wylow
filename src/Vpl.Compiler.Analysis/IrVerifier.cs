using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Ir;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Analysis;

/// <summary>
/// The flow analysis: checks the IR's invariants, after lowering and after each pass. A failure
/// is a compiler bug, except where the lowered program breaks a rule the source must keep, which
/// is the user's error (<see cref="Violation"/>): a source local read before it is assigned, or
/// assigned twice without <c>mut</c>; one used as not null where narrowing doesn't show it isn't;
/// a function whose end is reachable that returns a value; a constructor that uses what it makes,
/// or returns, before it assigns every field without a default.
/// <list type="number">
/// <item>Locals are declared once and used inside their scope. A temporary is defined once, and
/// a value out of control flow stored once on every path, before it is read; one is moved at most
/// once and not used after.</item>
/// <item>Places dereference only at the base, and their projections fit their types.</item>
/// <item>Every use of a reference or view lies where what it points into is still held: its hold
/// hasn't ended, the object local holding it hasn't been reassigned, and if it came from a
/// <c>Borrowable</c> that isn't borrowed, no code has run since. A borrowed local is used only while
/// nothing may have written the place it aliases.</item>
/// <item>No reference through a nullable value's <c>!</c> or a variant's field is used after an
/// instruction that may write the value or the variant.</item>
/// <item>A slot's <c>new</c> is in the slot's block, not inside a loop nested in it, and a slot
/// takes one object.</item>
/// </list>
/// Each instruction's effects come from <see cref="IrEffects"/>, so a pass that relies on the same
/// effects to remove a hold or a copy is checked by them too. Types are checked exactly: lowering
/// makes every conversion explicit.
/// </summary>
public sealed partial class IrVerifier
{
    readonly List<string> errors = [];
    readonly HashSet<string> reported = [];
    readonly HashSet<IrFunction> verifiedLambdas = [];

    IrFunction root = null!;
    IrFunction function = null!;
    IrStatement? current;

    /// <summary>The open scopes, innermost last, each with the locals declared in it so far.</summary>
    List<List<IrLocal>> scopes = [];
    readonly Dictionary<IrLocal, Declaration> declarations = [];
    List<LoopContext> loops = [];
    int loopDepth;
    readonly HashSet<IrLocal> filledSlots = [];

    readonly Writes writes;
    readonly IrProgram program;
    readonly DiagnosticBag? diagnostics;
    readonly HashSet<(IrStatement?, IrLocal)> reportedNullables = [];
    readonly List<(IrLocal Local, Location At, IrFunction By)> nullableWrites = [];
    readonly HashSet<(Location, string)> reportedViolations = [];
    readonly HashSet<LocalSymbol> reportedUnassigned = [];

    /// <summary>While a closure's captures are read, where its body uses the one being read, which an error about it is reported at.</summary>
    Location? captureOrigin;

    /// <summary>Inside a lambda, the source locals narrowed where it was made, for explaining why they aren't inside.</summary>
    HashSet<LocalSymbol> narrowedOutside = [];

    /// <summary>In a constructor, the fields it must assign, and the object or value it makes, whose other uses must wait for them.</summary>
    List<FieldSymbol> requiredFields = [];
    IrLocal? constructed;

    IrVerifier(IrProgram program, Writes writes, DiagnosticBag? diagnostics)
    {
        this.program = program;
        this.writes = writes;
        this.diagnostics = diagnostics;
    }

    /// <summary>
    /// Verifies the program, with what each of its functions may write, which a loop element borrowed in place needs.
    /// With <paramref name="diagnostics"/>, a program that uses a nullable value while its variable may be
    /// set to null (<see cref="ReplacedNullable"/>) is the user's error, reported there; without, it is a failure.
    /// </summary>
    public static IReadOnlyList<string> Verify(IrProgram program, Writes writes, DiagnosticBag? diagnostics = null)
    {
        var verifier = new IrVerifier(program, writes, diagnostics);
        // A lambda is verified where it is made.
        foreach (var function in program.Functions.Where(f => !f.IsLambda))
            verifier.VerifyFunction(function, function);
        foreach (var lambda in program.Functions.Where(f => f.IsLambda && !verifier.verifiedLambdas.Contains(f)))
            verifier.Report(lambda, null, "the lambda is never made");
        return verifier.errors;
    }

    sealed record Declaration(IrStatement? Statement, int LoopDepth);

    sealed class LoopContext(IrLoop loop)
    {
        public IrLoop Loop { get; } = loop;
        public List<State> Breaks { get; } = [];
        public List<State> Continues { get; } = [];
        public bool InContinue { get; set; }
    }

    void Report(IrFunction where, IrStatement? statement, string message)
    {
        var text = statement is null ? $"{where.Name}: {message}" : $"{where.Name}: {message}\n    at: {IrPrinter.Print(root, statement)}";
        if (reported.Add(text))
            errors.Add(text);
    }

    void Error(string message) => Report(function, current, message);

    /// <summary>
    /// A violation of a rule the source must keep: on the lowered program, the user's error at the
    /// source it comes from; after a pass, a compiler bug.
    /// </summary>
    void Violation(string message, Location? at = null)
    {
        if (diagnostics is null)
        {
            Error(message);
            return;
        }
        var location = at ?? captureOrigin ?? current?.Origin ?? function.Body.Origin;
        if (location is { } found && reportedViolations.Add((found, message)))
            diagnostics.Error(found, message);
    }

    static Location EndOf(Location body) =>
        new(body.Source, new TextSpan(Math.Max(body.Span.End - 1, body.Span.Start), body.Span.Length > 0 ? 1 : 0));

    void MissingReturn(IrFunction where)
    {
        var message = where.IsLambda ? "this lambda must return a value on every path" : $"'{where.Symbol.Name}' must return a value on every path";
        if (where.Body.Origin is { } body)
            Violation(message, EndOf(body));
        else
            Report(where, null, message);
    }

    IReadOnlyDictionary<IrLocal, string>? names;

    /// <summary>A local's name as the printed IR shows it.</summary>
    string N(IrLocal local) => (names ??= IrPrinter.Names(root)).GetValueOrDefault(local, local.Name);

    /// <param name="assignedFields">For a closure made in a constructor, the fields assigned where it is made.</param>
    void VerifyFunction(IrFunction verified, IrFunction printedIn, IReadOnlySet<FieldSymbol>? assignedFields = null)
    {
        if (root != printedIn)
            names = null;
        root = printedIn;
        function = verified;
        scopes = [[]];
        nullableWrites.Clear();
        loops = [];
        loopDepth = 0;
        // A closure made in a constructor captures what it is made from, which is checked where it is made.
        if (!verified.IsLambda)
        {
            narrowedOutside = [];
            requiredFields = verified.Symbol is { IsConstructor: true, IsImplicit: false, ContainingType: { } owner } && verified.Builds is null
                ? [.. owner.Fields.Where(f => f.RequiresAssignment)]
                : [];
            constructed = requiredFields.Count > 0 ? verified.This ?? verified.Struct : null;
        }
        var state = new State();
        state.AssignedFields.UnionWith(assignedFields ?? new HashSet<FieldSymbol>());
        foreach (var parameter in Entry(verified))
            DeclareParameter(parameter, state);
        state = Block(verified.Body, state, newScope: false);
        if (state.Reachable && ResultType(verified) != VplType.Void)
            MissingReturn(verified);
        if (state.Reachable && !verified.IsLambda && verified.Body.Origin is { } body)
            CheckFieldsAssigned(state, EndOf(body));
        if (verified.Builds is not null)
            CheckBuilt(verified);
    }

    void CheckFieldsAssigned(State state, Location at)
    {
        if (requiredFields.FirstOrDefault(f => !state.AssignedFields.Contains(f)) is { } field)
            Violation($"field '{field.Name}' must be assigned before the constructor returns", at);
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
                Violation($"field '{first.Field.Name}' might not have been assigned");
        }
        else if (requiredFields.FirstOrDefault(f => !state.AssignedFields.Contains(f)) is { } unassigned)
            Violation($"'this' cannot be used until every field is assigned, and '{unassigned.Name}' might not be");
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

    /// <summary>What the function returns: a struct constructor its value, and a class constructor nothing.</summary>
    static VplType ResultType(IrFunction function) =>
        function.Symbol.IsConstructor ? function.Struct?.Type ?? VplType.Void : function.Symbol.ReturnType;

    static IEnumerable<IrLocal> Entry(IrFunction function) =>
        [.. function.This is { } self ? [self] : Array.Empty<IrLocal>(), .. function.Parameters, .. function.Captures];

    /// <summary>What the caller holds needs no hold here, but writes through it reach the caller's storage.</summary>
    void DeclareParameter(IrLocal parameter, State state)
    {
        if (declarations.ContainsKey(parameter))
            Error($"'{N(parameter)}' is declared twice");
        declarations[parameter] = new Declaration(null, 0);
        scopes[^1].Add(parameter);
        state.Assigned.Add(parameter);
        if (parameter.Kind == IrLocalKind.Reference || parameter.Type.IsRefStruct)
            dependencies[parameter] = new Dependency([], IrAccess.Unknown, Alias: false, NeedsBorrow: false);
    }

    State Block(IrBlock block, State state, bool newScope = true)
    {
        if (newScope)
            scopes.Add([]);
        foreach (var statement in block.Statements)
        {
            if (!state.Reachable)
            {
                current = statement;
                Error("the statement is unreachable");
                break;
            }
            state = Statement(statement, state);
        }
        if (newScope)
            state = EndScope(state);
        return state;
    }

    /// <summary>The block's locals are released in reverse order, which may run code; nothing may depend on them after.</summary>
    State EndScope(State state)
    {
        var ending = scopes[^1];
        scopes.RemoveAt(scopes.Count - 1);
        if (!state.Reachable)
            return state;
        var released = ending.Where(l => !state.Ended.Contains(l) && !state.Moved.Contains(l)).SelectMany(l => IrEffects.Releasing(l).Runs).Distinct().ToList();
        if (released.Count > 0)
            Apply(state, new IrEffects([], released, false), "a release at the end of a block");
        foreach (var local in ending)
        {
            DependentsOf(local, state, $"'{N(local)}' went out of scope");
            state.Forget(local);
        }
        return state;
    }

    State Statement(IrStatement statement, State state)
    {
        current = statement;
        switch (statement)
        {
            case IrBlock block:
                return Block(block, state);
            case IrLet let:
                Rvalue(let.Value, state, let.Local);
                if (IsDeclaredHold(let.Local))
                    Refill(let.Local, state);
                else
                    Declare(let.Local, state, let);
                CheckLet(let);
                Define(let.Local, let.Value, state);
                return state;
            case IrDeclare declare:
                if (declare.Local.Kind == IrLocalKind.Borrowed || declare.Local.Kind == IrLocalKind.Hold && !declare.Local.IsTemporary)
                    Error($"a {declare.Local.Kind.ToString().ToLowerInvariant()} local needs its value where it is declared");
                Declare(declare.Local, state, declare);
                if (declare.Local.Kind == IrLocalKind.Slot)
                    state.Assigned.Add(declare.Local);
                return state;
            case IrStore store:
                Store(store, state);
                return state;
            case IrDo run:
                Rvalue(run.Value, state, null);
                return state;
            case IrCheck check:
                Check(check, state);
                return state;
            case IrIf conditional:
                return If(conditional, state);
            case IrLoop loop:
                return Loop(loop, state);
            case IrBreak jump:
                if (Enclosing(jump.Loop) is { } exited)
                    exited.Breaks.Add(state.Clone());
                return State.Unreachable;
            case IrContinue jump:
                if (Enclosing(jump.Loop) is { } continued)
                {
                    if (continued.InContinue)
                        Error("'continue' inside the loop's continue block");
                    continued.Continues.Add(state.Clone());
                }
                return State.Unreachable;
            case IrReturn ret:
                Return(ret, state);
                return State.Unreachable;
            case IrEnd end:
                End(end, state);
                return state;
            case IrUnreachable unreachable:
                CheckLoc(unreachable.Loc);
                return State.Unreachable;
            default:
                throw new InvalidOperationException($"unexpected IR statement {statement.GetType().Name}");
        }
    }

    LoopContext? Enclosing(IrLoop loop)
    {
        var context = loops.LastOrDefault(l => l.Loop == loop);
        if (context is null)
            Error("the jump isn't inside the loop it names");
        return context;
    }

    void Declare(IrLocal local, State state, IrStatement statement)
    {
        if (declarations.ContainsKey(local))
            Error($"'{N(local)}' is declared twice");
        declarations[local] = new Declaration(statement, loopDepth);
        scopes[^1].Add(local);
        state.Forget(local);
    }

    /// <summary>Whether the local is a hold declared before the <c>let</c>s that make it, as an <c>is ref</c> name's is, so that it outlives them.</summary>
    bool IsDeclaredHold(IrLocal local) =>
        local.Kind == IrLocalKind.Hold && declarations.TryGetValue(local, out var declaration) && declaration.Statement is IrDeclare;

    /// <summary>A declared hold made again, as a loop's test does each time, lets go of what it held first.</summary>
    void Refill(IrLocal local, State state)
    {
        if (!Visible(local))
            Error($"'{N(local)}' is used outside its scope");
        DependentsOf(local, state, $"'{N(local)}' was made again");
        state.Ended.Remove(local);
    }

    void CheckLet(IrLet let)
    {
        var (local, value) = (let.Local, let.Value);
        if (IrLet.Mismatch(local, value, N(local)) is { } mismatch)
            Error(mismatch);
        if (local.Type != value.Type)
            Error($"'{N(local)}' is a '{local.Type}', but is defined with a '{value.Type}'");
    }

    void Store(IrStore store, State state)
    {
        var target = store.Target;
        pendingMoves.Clear();
        Operand(store.Value, state);
        bool storesNonNull = OperandNonNull(store.Value, state);
        if (store.Value is IrRead { Ownership: IrOwnership.Borrow })
            Error("a store needs a value of its own: copy or move it");
        // A whole store gives the local a value that doesn't depend on what its old one did.
        if (!target.IsDeref && target.Projections.Count == 0)
            state.Invalid.Remove(target.Local);
        var initialized = Initialized(target);
        Place(target, state, reads: target.IsDeref || target.Projections.Count > 0, initializes: initialized is not null);
        if (target.Type != store.Value.Type)
            Error($"stores a '{store.Value.Type}' in a '{target.Type}'");

        var local = target.Local;
        bool whole = !target.IsDeref && target.Projections.Count == 0;
        bool deferred = declarations.TryGetValue(local, out var declaration) && declaration.Statement is IrDeclare;
        if (!target.IsDeref && local.IsTemporary && !deferred)
            Error($"'{N(local)}' is a temporary, defined once");
        else if (!target.IsDeref && local.IsTemporary && !whole)
            Error($"'{N(local)}' is a value out of control flow, stored only as a whole");
        else if (whole && local.Kind == IrLocalKind.Reference && !deferred)
            Error($"'{N(local)}' is a reference, which can't be made to refer elsewhere; store through it with '*'");
        else if (whole && local.Kind is IrLocalKind.Hold or IrLocalKind.Slot or IrLocalKind.Borrowed)
            Error($"'{N(local)}' is a {local.Kind.ToString().ToLowerInvariant()} local, which can't be stored");
        if (whole && (local.IsTemporary || local.Kind == IrLocalKind.Reference) && deferred)
        {
            if (state.MaybeAssigned.Contains(local))
                Error($"'{N(local)}' may be stored more than once on a path");
        }
        // A variable declared without a value that isn't 'mut' is assigned once.
        if (whole && local.Symbol is { IsDeferred: true, IsMutable: false, RefKind: RefKind.None } symbol && state.MaybeAssigned.Contains(local))
            Violation($"'{symbol.Name}' might already have been assigned; declare it 'mut' to assign it more than once");

        Apply(state, IrEffects.Of(store), "the store");
        CommitMoves(state);
        if (initialized is not null)
            state.AssignedFields.Add(initialized);
        if (whole)
        {
            Learn(local, new IrUse(store.Value), state);
            SetNonNull(local, new IrUse(store.Value), storesNonNull, state);
            state.Assigned.Add(local);
            state.MaybeAssigned.Add(local);
            state.Invalid.Remove(local);
            foreach (var stale in nullTests.Where(t => t.Value == local).Select(t => t.Key).ToList())
                nullTests.Remove(stale);
            if (Dependency.Of(store.Value, this) is { } dependency)
                dependencies[local] = dependencies.TryGetValue(local, out var other) ? other.Union(dependency) : dependency;
        }
    }

    void Check(IrCheck check, State state)
    {
        pendingMoves.Clear();
        foreach (var operand in check.Operands)
            Operand(operand, state);
        CheckLoc(check.Loc);
        bool fits = check.Kind switch
        {
            IrCheckKind.Index => check.Operands is [{ Type.IsInteger: true }, { Type.IsInteger: true }],
            IrCheckKind.NotNull => check.Operands is [{ Type: var t }] && (t.IsNullableReference || (t.IsNullable && t.NonNull.IsReference) || t.IsPointer),
            IrCheckKind.HasValue => check.Operands is [{ Type.IsNullableValue: true }],
            IrCheckKind.NoMatch => check.Operands.Count == 0,
            IrCheckKind.Narrowed => check.Operands is [IrRead { Place: { IsDeref: false, Projections.Count: 0, Local.Symbol: not null }, Type.MayBeNull: true }],
            _ => false,
        };
        if (!fits)
            Error($"the operands don't fit a '{check.Kind}' check");
        if ((check.Kind == IrCheckKind.Narrowed) != (check.Loc is null))
            Error("a check that may fail, and only one, carries a location");
        if (check is { Kind: IrCheckKind.Narrowed, Operands: [IrRead { Place.Local: var narrowed }] }
            && state.Assigned.Contains(narrowed) && !state.NonNull.Contains(narrowed))
            NotNarrowed(narrowed, state);
        Apply(state, IrEffects.Of(check), "the check");
        CommitMoves(state);
    }

    /// <summary>
    /// A source local used as not null where narrowing doesn't show it isn't (spec 02-types.md). An
    /// <c>is ref</c> name is stored as it is tested, but is only assigned where the test matched.
    /// </summary>
    void NotNarrowed(IrLocal local, State state)
    {
        var symbol = local.Symbol!;
        var name = symbol.Name;
        if (symbol.IsReferenceBinding)
        {
            if (reportedUnassigned.Add(symbol))
                Violation($"'{name}' might not have been assigned");
            return;
        }
        Violation(symbol.Type.IsNullableReference
            ? $"'{name}' might refer to nothing here; check it with 'if ({name} != null)', or use '!'"
            : symbol.RefKind != RefKind.None
            ? $"'{name}' might be null here: a '{FunctionSymbol.RefPrefix(symbol.RefKind).Trim()}' {(symbol.IsParameter ? "parameter" : "variable")} is not narrowed by null checks, since another reference to its variable could change it; copy it to a local first, or use '!'"
            : LambdaAssigns(local, state)
            ? $"'{name}' might be null here: a lambda assigns it, so null checks don't narrow it; copy it to a local first, or use '!'"
            : narrowedOutside.Contains(symbol)
            ? $"'{name}' might be null here: null checks outside a lambda don't narrow inside it; check it inside, or copy it to a local first"
            : $"'{name}' might be null here; check it with 'if ({name} != null)', or use '?.' or '!'");
    }

    State If(IrIf conditional, State state)
    {
        pendingMoves.Clear();
        Operand(conditional.Condition, state);
        CommitMoves(state);
        if (conditional.Condition.Type != VplType.Bool)
            Error($"the condition is a '{conditional.Condition.Type}', not a 'bool'");
        var outcome = conditional.Condition is IrRead { Place: { IsDeref: false, Projections.Count: 0, Local: var condition } } && state.Outcomes.TryGetValue(condition, out var known) ? known : null;
        var thenState = state.Clone();
        var elseState = state.Clone();
        if (outcome?.WhenTrue is { } whenTrue)
        {
            thenState.Assigned.UnionWith(whenTrue.Assigned);
            thenState.NonNull.UnionWith(whenTrue.NonNull);
        }
        if (outcome?.WhenFalse is { } whenFalse)
        {
            elseState.Assigned.UnionWith(whenFalse.Assigned);
            elseState.NonNull.UnionWith(whenFalse.NonNull);
        }
        var then = Block(conditional.Then, thenState);
        // Where a reference's null test failed, nothing it points into needs to stay held.
        var isNull = conditional.Condition is IrRead { Place: { IsDeref: false, Projections.Count: 0, Local: var tested } } && nullTests.TryGetValue(tested, out var reference) ? reference : null;
        if (isNull is not null)
            knownNull.Add(isNull);
        var otherwise = conditional.Else is { } elseBlock ? Block(elseBlock, elseState) : elseState;
        if (isNull is not null)
            knownNull.Remove(isNull);
        return State.Merge(then, otherwise);
    }

    /// <summary>Runs the body until what holds at its start stops changing, so that what a later iteration sees is checked too.</summary>
    State Loop(IrLoop loop, State state)
    {
        var context = new LoopContext(loop);
        loops.Add(context);
        loopDepth++;
        var entry = state.Clone();
        var declaredBefore = declarations.Keys.ToHashSet();
        var lambdasBefore = verifiedLambdas.ToHashSet();
        for (int pass = 0; ; pass++)
        {
            // A later pass declares the body's locals and makes its lambdas again.
            foreach (var local in declarations.Keys.Where(l => !declaredBefore.Contains(l)).ToList())
                declarations.Remove(local);
            verifiedLambdas.IntersectWith(lambdasBefore);
            context.Breaks.Clear();
            context.Continues.Clear();
            var end = Block(loop.Body, entry.Clone());
            end = context.Continues.Aggregate(end, State.Merge);
            if (loop.Continue is { } next && end.Reachable)
            {
                context.InContinue = true;
                end = Block(next, end);
                context.InContinue = false;
            }
            var merged = State.Merge(entry.Clone(), end);
            if (merged.SameAs(entry) || pass > 8)
                break;
            entry = merged;
        }
        loopDepth--;
        loops.RemoveAt(loops.Count - 1);
        return context.Breaks.Aggregate(State.Unreachable, State.Merge);
    }

    void Return(IrReturn ret, State state)
    {
        var returnType = ResultType(function);
        // A constructor's own return, which isn't written, is at the end of its body.
        if (constructed is not null && !function.IsLambda && (ret.Origin ?? (function.Body.Origin is { } body ? EndOf(body) : null)) is { } returned)
            CheckFieldsAssigned(state, returned);
        if (ret.Value is null)
        {
            if (returnType != VplType.Void)
                Error($"returns nothing from a function returning '{returnType}'");
            return;
        }
        pendingMoves.Clear();
        bool returnsMade = function.Struct is { } made && ret.Value is IrRead { Place: { IsDeref: false, Projections.Count: 0 } place } && place.Local == made;
        // Returning what a struct constructor makes isn't a use of it.
        var making = constructed;
        if (returnsMade)
            constructed = null;
        Operand(ret.Value, state);
        constructed = making;
        pendingMoves.Clear();
        if (function.Struct is not null && !returnsMade)
            Error("a struct constructor returns its value");
        if (returnType == VplType.Void)
            Error("returns a value from a function returning nothing");
        else if (ret.Value.Type != returnType)
            Error($"returns a '{ret.Value.Type}' from a function returning '{returnType}'");
        bool reference = function.Symbol.ReturnRefKind != RefKind.None;
        if (reference && ret.Value is not (IrConstant { Value: null } or IrRead { Place: { IsDeref: false, Projections.Count: 0, Local.Kind: IrLocalKind.Reference } }))
            Error("a function returning a reference returns a reference local");
        if (ret.Value is IrRead { Ownership: IrOwnership.Borrow })
            Error("a return needs a value of its own: copy or move it");
    }

    void End(IrEnd end, State state)
    {
        var local = end.Local;
        if (local.Kind is not (IrLocalKind.Hold or IrLocalKind.Slot))
            Error($"only a hold or a slot can be ended, not '{N(local)}'");
        // A slot may be ended again, as a loop's assignment does each time it runs.
        if (local.Kind != IrLocalKind.Slot || !state.Ended.Contains(local))
            Use(local, state, reads: false);
        Apply(state, IrEffects.Of(end), "the end of a hold");
        state.Ended.Add(local);
        DependentsOf(local, state, $"'{N(local)}' was ended", except: knownNull);
    }

    void CheckLoc(IrLoc? loc)
    {
        if (loc == IrLoc.Caller && !function.HasLocationParameter)
            Error("uses the caller's location in a function that isn't given one");
    }
}
