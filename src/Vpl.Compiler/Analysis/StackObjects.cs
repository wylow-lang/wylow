using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Analysis;

/// <summary>An object made by <c>new</c> on the stack; a temporary's lives until the end of its statement.</summary>
public readonly record struct StackObject(BoundNew Creation, bool IsTemporary);

/// <summary>
/// Finds the objects made by <c>new</c> that no reference can outlive the scope making them, so
/// that they can be in its stack frame instead of on the heap: those held by a local that is never
/// reassigned, for the rest of its block, and temporaries, for their statement. An object escapes
/// if a reference to it may be stored anywhere but a local declared within that scope, returned,
/// captured by an escaping lambda, or given to a function that may do any of these with it. Each
/// function is summarized by which of its parameters, and whether its <c>this</c>, may escape,
/// including through the functions it calls, and which it may return, which makes the call's value
/// that argument.
/// </summary>
/// <remarks>
/// A temporary is looked for only in a statement that evaluates each of its expressions at most
/// once and ends where the emitter ends its slot: a declaration, an assignment, an expression
/// statement, or a return, without a <c>try</c>, which runs part of the statement on its own.
/// </remarks>
public sealed class StackObjects
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

    /// <summary>The objects on the stack that a statement makes, outside any lambda in it, in the order they are made.</summary>
    public IReadOnlyList<StackObject> MadeBy(BoundStatement statement) => made.TryGetValue(statement, out var found) ? found : [];

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
        for (int i = 0; i < statements.Count; i++)
        {
            var statement = statements[i];
            var found = new List<StackObject>();
            BoundNew? held = null;
            if (statement is BoundVariableDeclaration { Local: { IsWritable: false, RefKind: RefKind.None }, Initializer: { } initializer }
                && Created(initializer) is { } creation)
            {
                var flow = new Flow(this, summarizing: false);
                foreach (var s in statements.Skip(i))
                    flow.Statement(s);
                if (!flow.Escapes(creation))
                    found.Add(new StackObject(held = creation, IsTemporary: false));
            }

            var temporaries = MayHoldTemporaries(statement)
                ? BoundTree.OwnExpressions(statement).SelectMany(Evaluated).OfType<BoundNew>().Where(c => !ReferenceEquals(c, held)).ToList()
                : [];
            if (temporaries.Count > 0 && temporaries.Distinct(ReferenceEqualityComparer.Instance).Count() == temporaries.Count)
            {
                var flow = new Flow(this, summarizing: false);
                flow.Statement(statement);
                if (statement is BoundVariableDeclaration declaration)
                    flow.Undeclare(declaration.Local);
                foreach (var local in BoundTree.OwnExpressions(statement).SelectMany(BoundTree.PatternLocals))
                    flow.Undeclare(local);
                found.AddRange(temporaries.Where(t => !flow.Escapes(t)).Select(t => new StackObject(t, IsTemporary: true)));
            }

            if (found.Count > 0)
                made[statement] = found;
        }
    }

    bool MayHoldTemporaries(BoundStatement statement) => statement switch
    {
        BoundVariableDeclaration { Local: var local } => local.RefKind == RefKind.None && !local.Type.IsRefStruct && !elements.AliasesElement(local),
        BoundAssignment or BoundExpressionStatement or BoundReturn => true,
        _ => false,
    } && !BoundTree.OwnExpressions(statement).SelectMany(Evaluated).OfType<BoundTry>().Any();

    /// <summary>The expression and those inside it, except in lambdas, which run as functions of their own.</summary>
    static IEnumerable<BoundExpression> Evaluated(BoundExpression expression) =>
        [expression, .. BoundTree.Children(expression).SelectMany(Evaluated)];

    /// <summary>The object a variable's initializer makes, if that is all it does.</summary>
    static BoundNew? Created(BoundExpression value) => value switch
    {
        BoundNew creation => creation,
        BoundConversion conversion when HoldsReference(conversion.Type) => Created(conversion.Operand),
        _ => null,
    };

    /// <summary>Whether a value of the type may be a reference to an object, as converting to it keeps the object's identity.</summary>
    static bool HoldsReference(VplType type) => type.NonNull.IsReference || type.NonNull.IsTypeParameter;

    /// <summary>
    /// Where the values some code is given may go: the locals each may be stored in, and the values
    /// that escape. The values are locals, <see cref="This"/>, and objects the code makes. A value
    /// stored in a local the code doesn't declare escapes too, since that local outlives the code.
    /// When <paramref name="summarizing"/> a function, what it returns goes to <see cref="Result"/>,
    /// which its callers see as the call's value; any other code lets what it returns escape.
    /// </summary>
    sealed class Flow(StackObjects analysis, bool summarizing)
    {
        public static readonly object This = new();
        static readonly object Result = new();

        readonly Dictionary<object, List<object>> stores = new(ReferenceEqualityComparer.Instance);
        readonly HashSet<object> escaping = new(ReferenceEqualityComparer.Instance);
        readonly HashSet<LocalSymbol> declared = [];

        /// <summary>The non-escaping lambdas being walked as part of the code, whose returns go to whatever runs them.</summary>
        int lambdaDepth;

        public void Declare(LocalSymbol local) => declared.Add(local);
        public void Undeclare(LocalSymbol local) => declared.Remove(local);

        public bool Escapes(object value) =>
            Reachable(value).Any(v => escaping.Contains(v) || (v is LocalSymbol local && !declared.Contains(local)));

        public bool IsReturned(object value) => Reachable(value).Contains(Result);

        /// <summary>The value and everything it may be stored in.</summary>
        IEnumerable<object> Reachable(object value)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<object>([value]);
            while (pending.TryPop(out var next))
            {
                if (!seen.Add(next))
                    continue;
                yield return next;
                foreach (var target in stores.GetValueOrDefault(next) ?? [])
                    pending.Push(target);
            }
        }

        public void Statement(BoundStatement statement)
        {
            switch (statement)
            {
                case BoundVariableDeclaration declaration:
                    declared.Add(declaration.Local);
                    Store(Value(declaration.Initializer), declaration.Local);
                    break;
                case BoundAssignment { Target: BoundLocalAccess { Local.RefKind: RefKind.None } target } assignment:
                    Store(Value(assignment.Value), target.Local);
                    break;
                case BoundAssignment assignment:
                    Use(assignment.Target);
                    Escape(assignment.Value);
                    break;
                case BoundReturn ret when summarizing && lambdaDepth == 0:
                    Store(Value(ret.Value), Result);
                    break;
                case BoundReturn ret:
                    Escape(ret.Value);
                    break;
                case BoundSuperCall super:
                    Call(super.Constructor, [This], super.Arguments, direct: true);
                    break;
                case BoundForRange range:
                    declared.Add(range.Local);
                    Use(range.Start);
                    Use(range.End);
                    Statement(range.Body);
                    break;
                case BoundSwitch choice:
                    Match(choice.Value, choice.Cases.SelectMany(c => c.Patterns));
                    foreach (var switchCase in choice.Cases)
                        Statement(switchCase.Body);
                    break;
                default:
                    foreach (var expression in BoundTree.OwnExpressions(statement))
                        Use(expression);
                    foreach (var inner in BoundTree.InnerStatements(statement))
                        Statement(inner);
                    break;
            }
        }

        public void Escape(BoundExpression? expression) => escaping.UnionWith(Value(expression));

        void Use(BoundExpression? expression) => Value(expression);

        void Store(List<object> values, object target)
        {
            foreach (var value in values)
            {
                if (!stores.TryGetValue(value, out var targets))
                    stores[value] = targets = [];
                targets.Add(target);
            }
        }

        /// <summary>Walks what an expression evaluates, and returns the values its own value may be.</summary>
        List<object> Value(BoundExpression? expression)
        {
            switch (expression)
            {
                case null:
                    return [];
                case BoundLocalAccess access:
                    return [access.Local];
                case BoundThis:
                    return [This];
                case BoundNew creation:
                    Call(creation.Constructor, [creation], creation.Arguments, direct: true);
                    if (analysis.BorrowsLetEscape(creation.Type))
                        escaping.Add(creation);
                    return [creation];
                case BoundConversion conversion when HoldsReference(conversion.Type):
                    return Value(conversion.Operand);
                case BoundNonNull nonNull:
                    return Value(nonNull.Operand);
                case BoundConditional conditional:
                    Use(conditional.Condition);
                    return [.. Value(conditional.WhenTrue), .. Value(conditional.WhenFalse)];
                case BoundCoalesce coalesce:
                    return [.. Value(coalesce.Left), .. Value(coalesce.Right)];
                case BoundConditionalAccess access:
                    declared.Add(access.Value);
                    Store(Value(access.Receiver), access.Value);
                    return Value(access.WhenNotNull);
                case BoundIs test:
                    Match(test.Operand, [test.Pattern]);
                    return [];
                case BoundSwitchExpression choice:
                    Match(choice.Value, choice.Cases.SelectMany(c => c.Patterns));
                    return [.. choice.Cases.SelectMany(c => Value(c.Body))];
                case BoundCall call:
                    return Call(call.Method, call.Receiver is null ? null : Value(call.Receiver), call.Arguments, call.IsSuperCall);
                case BoundInvocation invocation:
                    Use(invocation.Callee);
                    return Arguments(invocation.Arguments, [.. analysis.whole.LambdasOfType(invocation.Callee.Type.NonNull).Select(l => analysis.summaries[l]!)]);
                case BoundConstruction construction:
                    Call(construction.Constructor, null, construction.Arguments, direct: true);
                    foreach (var initializer in construction.Initializers)
                        Escape(initializer.Value);
                    return [];
                case BoundLambda lambda:
                    Lambda(lambda);
                    return [];
                case BoundFieldAccess or BoundBinary or BoundUnary or BoundIndex or BoundArrayLength or BoundArrayData or BoundTry:
                    foreach (var child in BoundTree.Children(expression))
                        Use(child);
                    return [];
                default:
                    // Anything else made from values holds them: an array literal, an enum variant, or a
                    // conversion to a value that refers to its operand, such as a Weak<T>.
                    foreach (var child in BoundTree.Children(expression))
                        Escape(child);
                    return [];
            }
        }

        /// <summary>Walks a call, and returns the values the call's value may be: the arguments it may return.</summary>
        public List<object> Call(MethodReference method, List<object>? receiver, IReadOnlyList<BoundExpression> arguments, bool direct)
        {
            var targets = analysis.Targets(method.Symbol, direct);
            List<object> returned = [];
            if (receiver is not null)
                Pass(receiver, targets, t => t.This, t => t.ReturnsThis, returned);
            returned.AddRange(Arguments(arguments, targets));
            return returned;
        }

        List<object> Arguments(IReadOnlyList<BoundExpression> arguments, List<Summary>? targets)
        {
            List<object> returned = [];
            for (int i = 0; i < arguments.Count; i++)
            {
                int index = i;
                Pass(Value(arguments[i]), targets, t => t.Parameters.Contains(index), t => t.ReturnedParameters.Contains(index), returned);
            }
            return returned;
        }

        /// <summary>Gives values to a parameter, or the <c>this</c>, of the functions a call may run; <paramref name="targets"/> is null if one is unknown.</summary>
        void Pass(List<object> values, List<Summary>? targets, Func<Summary, bool> escapes, Func<Summary, bool> isReturned, List<object> returned)
        {
            if (targets is null || targets.Any(escapes))
                escaping.UnionWith(values);
            else if (targets.Any(isReturned))
                returned.AddRange(values);
        }

        /// <summary>
        /// An escaping lambda copies what it captures into a closure that may outlive anything. A
        /// non-escaping one runs only while the code that makes it does, so its body is part of that code.
        /// </summary>
        void Lambda(BoundLambda lambda)
        {
            if (lambda.Function.IsEscaping)
            {
                var (locals, self) = BoundTree.Captures(lambda);
                escaping.UnionWith(locals.Select(a => a.Local));
                if (self is not null)
                    escaping.Add(This);
                return;
            }
            foreach (var parameter in lambda.Function.Parameters)
                declared.Add(parameter);
            lambdaDepth++;
            Statement(lambda.Body);
            lambdaDepth--;
        }

        void Match(BoundExpression value, IEnumerable<BoundPattern> patterns)
        {
            var values = Value(value);
            foreach (var binding in patterns.SelectMany(Patterns.Bindings))
            {
                declared.Add(binding);
                Store(values, binding);
            }
        }
    }
}
