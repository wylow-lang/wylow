using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Analysis;

public sealed partial class StackObjects
{
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
        readonly Dictionary<BoundAssignment, List<object>> assigned = new(ReferenceEqualityComparer.Instance);

        /// <summary>The non-escaping lambdas being walked as part of the code, whose returns go to whatever runs them.</summary>
        int lambdaDepth;

        public void Declare(LocalSymbol local) => declared.Add(local);
        public void Undeclare(LocalSymbol local) => declared.Remove(local);

        public bool Escapes(object value) =>
            Reachable(value).Any(v => escaping.Contains(v) || (v is LocalSymbol local && !declared.Contains(local)));

        public bool IsReturned(object value) => Reachable(value).Contains(Result);

        /// <summary>The values an assignment to a local, walked as part of the code, may store.</summary>
        public IReadOnlyList<object> Assigned(BoundAssignment assignment) => assigned.GetValueOrDefault(assignment) ?? [];

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

        public void Statement(BoundStatement statement)
        {
            switch (statement)
            {
                case BoundVariableDeclaration declaration:
                    declared.Add(declaration.Local);
                    Store(Value(declaration.Initializer), declaration.Local);
                    // A reference or a view holds the object it refers into for its whole scope.
                    if (declaration.Local.RefKind != RefKind.None || declaration.Local.Type.IsRefStruct)
                        Store(Roots(declaration.Initializer), declaration.Local);
                    break;
                case BoundAssignment { Target: BoundLocalAccess { Local.RefKind: RefKind.None } target } assignment:
                    var values = Value(assignment.Value);
                    assigned[assignment] = values;
                    Store(values, target.Local);
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
                    var matched = Match(choice.Value, choice.Cases.SelectMany(c => c.Patterns));
                    // The emitter holds a matched value that isn't a local for the whole statement.
                    if (choice.Value is not BoundLocalAccess)
                        Store(matched, choice);
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

        static List<object> Roots(BoundExpression? place) => place is null ? [] :
        [
            .. Evaluated(place).Select<BoundExpression, object?>(e => e switch { BoundLocalAccess access => access.Local, BoundThis => This, _ => null }).OfType<object>(),
        ];

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
                // A 'ref?' is a reference to its operand, as a 'ref' is to the place itself.
                case BoundConversion conversion when HoldsReference(conversion.Type) || conversion.Type.IsNullableReference:
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
                    return [];
                case BoundLambda lambda:
                    return Lambda(lambda);
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
        /// An escaping lambda copies what it captures into a closure, the lambda's value, which holds
        /// the copies as an object holds its fields, unless the lambda's body may let them escape or
        /// return them, whenever it is called. A non-escaping one runs only while the code that makes
        /// it does, so its body is part of that code.
        /// </summary>
        List<object> Lambda(BoundLambda lambda)
        {
            if (lambda.Function.IsEscaping)
            {
                if (!MakesClosure(lambda))
                    return [];
                var (locals, self) = BoundTree.Captures(lambda);
                var summary = analysis.summaries[lambda.Function]!;
                foreach (var local in locals.Select(a => a.Local))
                    Hold(lambda, local, summary.Captures.Contains(local));
                if (self is not null)
                    Hold(lambda, This, summary.This || summary.ReturnsThis);
                return [lambda];
            }
            foreach (var parameter in lambda.Function.Parameters)
                declared.Add(parameter);
            lambdaDepth++;
            Statement(lambda.Body);
            lambdaDepth--;
            return [];
        }

        void Hold(BoundLambda closure, object captured, bool escapes)
        {
            if (escapes)
                escaping.Add(captured);
            else
                Store([captured], closure);
        }

        List<object> Match(BoundExpression value, IEnumerable<BoundPattern> patterns)
        {
            var values = Value(value);
            foreach (var binding in patterns.SelectMany(Patterns.Bindings))
            {
                declared.Add(binding);
                Store(values, binding);
            }
            return values;
        }
    }
}
