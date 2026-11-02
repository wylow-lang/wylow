using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.Analysis;

/// <summary>
/// What each lowered function may let escape, for keeping objects on the stack
/// (<c>StackSlots</c>): which of its parameters, and whether its <c>this</c>, a
/// reference to may be kept after it returns, including through the functions it calls, and which
/// it may return, which makes the call's value that argument; for an escaping lambda, also which
/// of its captures its body may let escape or return.
/// </summary>
public sealed class Escapes
{
    public sealed record Summary(IReadOnlySet<int> Parameters, bool This, IReadOnlySet<int> ReturnedParameters, bool ReturnsThis, IReadOnlySet<IrLocal> Captures) : IMonotone<Summary>
    {
        public static Summary Empty() => new(new HashSet<int>(), false, new HashSet<int>(), false, new HashSet<IrLocal>());

        public bool Contains(Summary earlier) =>
            earlier.Parameters.IsSubsetOf(Parameters)
            && (!earlier.This || This)
            && earlier.ReturnedParameters.IsSubsetOf(ReturnedParameters)
            && (!earlier.ReturnsThis || ReturnsThis)
            && earlier.Captures.IsSubsetOf(Captures);
    }

    readonly Callees callees;
    readonly Summaries<IrFunction, Summary> summaries;

    Escapes(Callees callees)
    {
        this.callees = callees;
        summaries = new Summaries<IrFunction, Summary>(callees.Program.Functions, Summary.Empty);
    }

    internal static Escapes Find(Callees callees)
    {
        var result = new Escapes(callees);
        result.summaries.Solve(result.Summarize);
        return result;
    }

    /// <summary>Where the values some code of the function is given may go, from nothing yet.</summary>
    public EscapeFlow Flow(IrFunction function) => new(this, callees.Fresh(function), summarizing: false);

    Summary Summarize(IrFunction function)
    {
        var flow = new EscapeFlow(this, function, summarizing: true);
        // An escaping lambda's captures are its closure's copies, which nothing can store anything in.
        foreach (var local in function.Parameters.Concat(function.Captures))
            flow.Declare(local);
        flow.Statement(function.Body);
        return new Summary(
            function.Parameters.Index().Where(p => flow.Escapes(p.Item)).Select(p => p.Index).ToHashSet(),
            function.This is { } self && flow.Escapes(self),
            function.Parameters.Index().Where(p => flow.IsReturned(p.Item)).Select(p => p.Index).ToHashSet(),
            function.This is { } returned && flow.IsReturned(returned),
            function.Captures.Where(l => flow.Escapes(l) || flow.IsReturned(l)).ToHashSet());
    }

    internal Summary SummaryOf(IrFunction function) => summaries[function]!;

    /// <summary>
    /// The summaries of the functions some code may run, or null if one of them isn't lowered, such
    /// as an extern function, which lets everything it is given escape.
    /// </summary>
    internal List<Summary>? Targets(IrRun run)
    {
        if (callees.Of(run) is not { RunsExtern: false } found)
            return null;
        return [.. found.Functions.Select(c => summaries[c.Function]!)];
    }

    /// <summary>
    /// Whether an object of exactly the type is <c>Borrowable</c>, and the calls around its borrows
    /// may let it escape: the implementations of its class or the classes it extends.
    /// </summary>
    internal bool BorrowsLetEscape(WylType type)
    {
        var program = callees.Program.Bound;
        if (!program.IsBorrowable(type))
            return false;
        var borrowable = program.Stdlib.Borrowable!;
        HashSet<TypeSymbol?> classes = [type.Symbol, .. Conversions.Supertypes(type).Select(t => t.Symbol)];
        foreach (var method in borrowable.Methods.Values.SelectMany(m => m))
        {
            if (callees.Of(new IrRunMethod(new MethodReference(method, borrowable.Type, []), Direct: false)) is not { RunsExtern: false } found)
                return true;
            if (found.Functions.Where(c => classes.Contains(c.Function.Symbol.ContainingType)).Any(c => summaries[c.Function]!.This))
                return true;
        }
        return false;
    }
}

/// <summary>
/// Where the values some code is given may go: the locals each may be stored in, and the values
/// that escape. The values are locals (<c>this</c> among them) and the objects and closures the
/// code makes. A value stored in a local the code doesn't declare escapes too, since that local
/// outlives the code. When summarizing a function, what it returns goes to the call's value; any
/// other code lets what it returns escape. A value read from a field or an element isn't followed:
/// storing one there makes it escape.
/// </summary>
public sealed class EscapeFlow
{
    static readonly object Result = new();

    readonly Escapes analysis;
    readonly bool summarizing;
    readonly Dictionary<object, List<object>> stores = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<object> escaping = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<IrLocal> declared = [];
    readonly Dictionary<IrStore, List<object>> assigned = new(ReferenceEqualityComparer.Instance);

    /// <summary>The non-escaping lambdas being walked as part of the code, whose returns go to whatever runs them.</summary>
    int lambdaDepth;

    internal EscapeFlow(Escapes analysis, IrFunction function, bool summarizing)
    {
        this.analysis = analysis;
        this.summarizing = summarizing;
        Function = function;
    }

    public IrFunction Function { get; }

    public void Declare(IrLocal local) => declared.Add(local);

    // `this` outlives the code, but only what is stored in it could escape that way, and storing in it escapes.
    public bool Escapes(object value) =>
        Reachable(value).Any(v => escaping.Contains(v) || (v is IrLocal { IsThis: false } local && !declared.Contains(local)));

    public bool IsReturned(object value) => Reachable(value).Contains(Result);

    /// <summary>The values a store to a local, walked as part of the code, may store.</summary>
    public IReadOnlyList<object> Assigned(IrStore store) => assigned.GetValueOrDefault(store) ?? [];

    /// <summary>The value and everything it may be stored in.</summary>
    public IEnumerable<object> Reachable(object value)
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

    public void Statement(IrStatement statement)
    {
        switch (statement)
        {
            case IrBlock block:
                foreach (var inner in block.Statements)
                    Statement(inner);
                break;
            case IrLet let:
                declared.Add(let.Local);
                Store(Value(let.Value), let.Local);
                break;
            case IrDeclare declare:
                declared.Add(declare.Local);
                break;
            case IrStore { Target: { IsDeref: false, Projections.Count: 0, Local: { IsThis: false } target } } store:
                var stored = Operand(store.Value);
                assigned[store] = stored;
                Store(stored, target);
                break;
            case IrStore store:
                escaping.UnionWith(Operand(store.Value));
                break;
            case IrDo run:
                Value(run.Value);
                break;
            case IrReturn ret:
                var returned = ret.Value is null ? [] : Operand(ret.Value);
                if (summarizing && lambdaDepth == 0)
                    Store(returned, Result);
                else
                    escaping.UnionWith(returned);
                break;
            case IrIf conditional:
                Statement(conditional.Then);
                if (conditional.Else is { } otherwise)
                    Statement(otherwise);
                break;
            case IrLoop loop:
                Statement(loop.Body);
                if (loop.Continue is { } next)
                    Statement(next);
                break;
        }
    }

    void Store(List<object> values, object target)
    {
        foreach (var value in values)
        {
            if (!stores.TryGetValue(value, out var targets))
                stores[value] = targets = [];
            targets.Add(target);
        }
    }

    static List<object> Operand(IrOperand operand) => operand is IrRead read ? Whole(read.Place) : [];

    // A reference into a field or an element, or a hold of what is in one, can't keep the object
    // it is in: a reference can't be stored, and what is in the field was stored there, so it
    // escaped then.
    static List<object> Argument(IrArgument argument) => argument switch
    {
        IrOperand operand => Operand(operand),
        IrRefArgument reference => Whole(reference.Place),
        IrAddrArgument address => Whole(address.Place),
        _ => [],
    };

    static List<object> Whole(IrPlace place) => place.Projections.Count == 0 ? [place.Local] : [];

    /// <summary>Walks what an rvalue does, and returns the values its own value may be.</summary>
    List<object> Value(IrRvalue value)
    {
        switch (value)
        {
            case IrUse use:
                return Operand(use.Operand);
            case IrNew creation:
                Call(new IrRunMethod(creation.Constructor, Direct: true), [creation], creation.Arguments);
                if (analysis.BorrowsLetEscape(creation.Type))
                    escaping.Add(creation);
                return [creation];
            case IrClosure closure:
                return Closure(closure);
            case IrLambda lambda:
                // A non-escaping lambda runs only while the code making it does, so its body is part of that code.
                foreach (var parameter in lambda.Function.Parameters)
                    declared.Add(parameter);
                lambdaDepth++;
                Statement(lambda.Function.Body);
                lambdaDepth--;
                return [];
            // A 'ref?' is a reference to its operand, as a 'ref' is to the place itself.
            case IrConvert conversion when conversion.Type.KeepsIdentity || conversion.Type.IsNullableReference:
                return Operand(conversion.Operand);
            // A base or chained constructor call runs on the object being made, which it is given as 'this'.
            case IrCall { Receiver: null, IsSuperCall: true, Method.Symbol.IsConstructor: true } construct when Function.This is { } made:
                return Call(new IrRunMethod(construct.Method, true), [made], construct.Arguments);
            case IrCall call:
                return Call(new IrRunMethod(call.Method, call.IsSuperCall), call.Receiver is null ? null : Argument(call.Receiver), call.Arguments);
            case IrInvoke invoke:
                return Arguments(invoke.Arguments, analysis.Targets(new IrRunFunctionValue(invoke.Callee.Type.NonNull)));
            // A memberwise constructor stores each argument in a field, as an implicit one has none.
            case IrConstruct { Constructor.Symbol: { IsMemberwise: false, IsImplicit: false } } construction:
                Call(new IrRunMethod(construction.Constructor, Direct: true), null, construction.Arguments);
                return [];
            case IrChoose choice:
                return [.. choice.Options.SelectMany(Operand)];
            case IrHold hold:
                return Whole(hold.Place);
            case IrRef reference:
                return Whole(reference.Place);
            case IrAddr address:
                return Whole(address.Place);
            case IrData data:
                return Whole(data.Place);
            case IrUnary or IrBinary or IrTest or IrDefault or IrZeroed:
                return [];
            default:
                // Anything else made from values holds them: a construction, an array, an enum variant,
                // or a conversion to a value that refers to its operand, such as a Weak<T>.
                foreach (var operand in IrWalk.Operands(value))
                    escaping.UnionWith(Argument(operand));
                return [];
        }
    }

    /// <summary>Walks a call, and returns the values the call's value may be: the arguments it may return.</summary>
    List<object> Call(IrRun run, List<object>? receiver, IReadOnlyList<IrArgument> arguments)
    {
        var targets = analysis.Targets(run);
        List<object> returned = [];
        if (receiver is not null)
            Pass(receiver, targets, t => t.This, t => t.ReturnsThis, returned);
        returned.AddRange(Arguments(arguments, targets));
        return returned;
    }

    List<object> Arguments(IReadOnlyList<IrArgument> arguments, List<Escapes.Summary>? targets)
    {
        List<object> returned = [];
        for (int i = 0; i < arguments.Count; i++)
        {
            int index = i;
            Pass(Argument(arguments[i]), targets, t => t.Parameters.Contains(index), t => t.ReturnedParameters.Contains(index), returned);
        }
        return returned;
    }

    /// <summary>
    /// Gives values to a parameter, or the <c>this</c>, of the functions a call may run; <paramref name="targets"/> is null if one is unknown.
    /// </summary>
    void Pass(List<object> values, List<Escapes.Summary>? targets, Func<Escapes.Summary, bool> escapes, Func<Escapes.Summary, bool> isReturned, List<object> returned)
    {
        // A target that may return the value doesn't stop one that may let it escape: both facts
        // hold, and a summary that only grows must keep the returned one when the escaped one appears.
        if (targets is null || targets.Any(escapes))
            escaping.UnionWith(values);
        if (targets is not null && targets.Any(isReturned))
            returned.AddRange(values);
    }

    /// <summary>
    /// An escaping lambda copies what it captures into a closure, the lambda's value, which holds the
    /// copies as an object holds its fields, and lets them escape when the lambda's body may let them
    /// escape or return them, whenever it is called.
    /// </summary>
    List<object> Closure(IrClosure closure)
    {
        if (closure.Captures.Count == 0)
            return [];
        var summary = analysis.SummaryOf(closure.Function);
        foreach (var (operand, capture) in closure.Captures.Zip(closure.Function.Captures))
        {
            var values = Operand(operand);
            Store(values, closure);
            if (summary.Captures.Contains(capture))
                escaping.UnionWith(values);
        }
        return [closure];
    }
}
