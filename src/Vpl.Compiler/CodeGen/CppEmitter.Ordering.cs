using System.Globalization;
using System.Text;
using Vpl.Compiler.Analysis;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.CodeGen;

public sealed partial class CppEmitter
{
    /// <summary>
    /// The object holding the storage that a place, or a span's view, lies in: the nearest object
    /// on the path to it. <c>Borrowed</c> when the object handed the storage out from a method and may
    /// be <c>Borrowable</c>, so it may move or free that storage unless it is borrowed while the
    /// place or view is used. Any other object only needs to be held.
    /// </summary>
    (BoundExpression Root, bool Borrowed)? StorageRoot(BoundExpression expression)
    {
        var current = expression;
        while (true)
        {
            switch (current)
            {
                case BoundFieldAccess field when field.Receiver.Type.IsReference:
                    return (field.Receiver, false);
                case BoundFieldAccess field:
                    current = field.Receiver;
                    break;
                case BoundIndex { Target.Type.IsFixedArray: true } index:
                    current = index.Target;
                    break;
                case BoundCall { Receiver: { } receiver } call when call.Method.Symbol.ReturnRefKind != RefKind.None || call.Type.IsRefStruct:
                    if (receiver.Type.IsReference)
                        return (receiver, MayBeBorrowable(receiver.Type));
                    current = receiver;
                    break;
                case BoundConstruction { Type.IsRefStruct: true } construction when construction.Arguments.OfType<BoundArrayData>().FirstOrDefault() is { } data:
                    current = data.Array;
                    break;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// The object to hold while a place is used by one operation. Locals and <c>this</c> need no
    /// hold, since nothing else can assign them (except locals a lambda assigns, which any call
    /// may); object locals can't even be passed by <c>ref</c>.
    /// </summary>
    BoundExpression? PinRoot(BoundExpression place) =>
        StorageRoot(place) is var (root, borrowed) && (borrowed || !IsStable(root)) ? root : null;

    /// <summary>
    /// The object to hold for the scope of a <c>ref</c> or span variable: even a local, since the
    /// function itself may reassign it, but not <c>this</c>, which the caller holds.
    /// </summary>
    BoundExpression? ScopeRoot(BoundExpression place) =>
        StorageRoot(place) is var (root, borrowed) && (borrowed || root is not BoundThis) ? root : null;

    /// <summary>A strong reference to <paramref name="root"/> for as long as <paramref name="place"/> is used, which also borrows it if needed.</summary>
    string Hold(BoundExpression place, BoundExpression root) =>
        StorageRoot(place) is (_, true) ? $"vpl::borrow({Expression(root)})" : $"vpl::pin({Expression(root)})";

    /// <summary>Whether an object of the type may be a <c>Borrowable</c>, which some class in the program it could be implements.</summary>
    bool MayBeBorrowable(VplType type) =>
        borrowableClasses.Any(c => c == type.Symbol || Conversions.Supertypes(c.Type).Any(t => t.Symbol == type.Symbol));

    static bool IsStable(BoundExpression expression) => expression is BoundThis or BoundLocalAccess { Local.IsAssignedByLambda: false };

    /// <summary>
    /// Whether a value used by one operation could be replaced while it runs, so it must be held.
    /// A temporary can't be: the full expression owns it until the operation is done.
    /// </summary>
    static bool MayBeReplaced(BoundExpression value) => !IsStable(value) && value is not (BoundNew or BoundInvocation or BoundLambda
        or BoundCall { Method.Symbol: { ReturnRefKind: RefKind.None, IsBuiltin: false } });

    /// <summary>Whether releasing a value of the type may destroy an object.</summary>
    static bool MayHoldReferences(VplType type) => MayHoldReferences(type, []);

    /// <summary>Whether releasing the value may destroy an object: a new closure only may if what it captures may.</summary>
    static bool MayOwnObjects(BoundExpression value)
    {
        if (value is not BoundLambda { Type.IsEscaping: true } lambda)
            return MayHoldReferences(value.Type);
        var (locals, self) = BoundTree.Captures(lambda);
        return self is not null || locals.Any(a => MayHoldReferences(a.Local.Type));
    }

    static bool MayHoldReferences(VplType type, HashSet<VplType> visiting)
    {
        if (type.IsNullableValue)
            return MayHoldReferences(type.NonNull, visiting);
        if (type.IsReference || type.IsTypeParameter || type.IsEscaping)
            return true;
        if (type.IsFixedArray)
            return MayHoldReferences(type.ElementType!, visiting);
        if (!type.IsStructOrEnum || !visiting.Add(type))
            return false;
        var map = TypeMap.For(type);
        return type.Symbol!.ValueFields.Any(f => MayHoldReferences(map.Apply(f.Type), visiting));
    }

    /// <summary>
    /// VPL evaluates left to right, but C++ leaves the order of operands and arguments unspecified.
    /// When the order is observable, evaluate the operands into temporaries inside an
    /// immediately-invoked lambda, which the C++ optimizer inlines away. Operands passed by
    /// reference are bound with <c>auto&amp;&amp;</c> so that the callee still sees the original place.
    /// </summary>
    string Ordered(IReadOnlyList<Operand> operands, Func<IReadOnlyList<string>, string> combine)
    {
        if (!NeedsOrdering(operands))
            return combine([.. operands.Select(InlineOperandText)]);
        var (declarations, temps) = OrderedTemporaries(operands);
        return $"[&] {{ {declarations}return {combine(temps)}; }}()";
    }

    // A temporary pin lives until the end of the full expression, which includes the call.
    string InlineOperandText(Operand operand)
    {
        var text = OperandText(operand, operand.PinRoot is { } root ? HoldOperand(operand, root) : null);
        return operand.Copy ? $"{CppType(operand.Expression.Type)}({text})" : operand.Keep ? $"vpl::keep({text})" : text;
    }

    /// <summary>Declarations that evaluate the operands in order, and the temporaries holding them.</summary>
    (string Declarations, List<string> Temporaries) OrderedTemporaries(IReadOnlyList<Operand> operands)
    {
        var body = new StringBuilder();
        var temps = new List<string>();
        foreach (var operand in operands)
        {
            string? pin = null;
            if (operand.PinRoot is { } root)
            {
                pin = NewTemp();
                body.Append($"auto {pin} = {HoldOperand(operand, root)}; ");
            }
            var temp = NewTemp();
            body.Append($"{(operand.ByReference ? "auto&&" : "auto")} {temp} = {OperandText(operand, pin)}; ");
            temps.Add(temp);
        }
        return (body.ToString(), temps);
    }

    // An object receiver is copied into a pin before the call, so its container need not stay borrowed.
    string HoldOperand(Operand operand, BoundExpression root) =>
        operand.PinValue ? $"vpl::pin({Expression(root)})" : Hold(operand.Expression, root);

    string OperandText(Operand operand, string? pinRootText)
    {
        if (pinRootText is not null)
            replacements[operand.PinRoot!] = pinRootText;
        var text = Expression(operand.Expression);
        if (pinRootText is not null)
            replacements.Remove(operand.PinRoot!);
        // A replaced receiver is already held by a pin.
        return operand.PinValue && !replacements.ContainsKey(operand.Expression) ? $"vpl::pin({text})" : text;
    }

    string Ordered(BoundExpression left, BoundExpression right, Func<IReadOnlyList<string>, string> combine) =>
        Ordered([new Operand(left, false), new Operand(right, false)], combine);

    bool NeedsOrdering(IReadOnlyList<Operand> operands)
    {
        for (int i = 0; i < operands.Count; i++)
        {
            for (int j = i + 1; j < operands.Count; j++)
            {
                if (Conflicts(operands[i], operands[j]))
                    return true;
            }
        }
        return false;
    }

    // Two effects (calls, panics) must stay in order, and so must a write and a read of the same
    // state. Any call may write any object, so object fields count as read by every call; a call
    // that may run a lambda may also write the locals lambdas change.
    bool Conflicts(Operand a, Operand b) =>
        (HasEffects(a.Expression) && HasEffects(b.Expression))
        || (Writes(a.Expression) && Reads(b, ReadsState))
        || (Writes(b.Expression) && Reads(a, ReadsState))
        || (RunsLambdas(a.Expression) && Reads(b, ReadsLambdaChanged))
        || (RunsLambdas(b.Expression) && Reads(a, ReadsLambdaChanged))
        || (WritesHeap(a.Expression) && Reads(b, ReadsHeap))
        || (WritesHeap(b.Expression) && Reads(a, ReadsHeap));

    // Binding a reference to a place reads only the objects the path goes through; the callee reads the rest later.
    static bool Reads(Operand operand, Func<BoundExpression, bool> reads) =>
        operand.ByReference && !operand.PinValue && IsPlace(operand.Expression)
            ? operand.PinRoot is { } root && reads(root)
            : reads(operand.Expression);

    static bool IsPlace(BoundExpression expression) => expression switch
    {
        BoundLocalAccess or BoundThis => true,
        BoundFieldAccess { Receiver.Type.IsReference: true } => true,
        BoundFieldAccess field => IsPlace(field.Receiver),
        _ => false,
    };

    bool HasEffects(BoundExpression expression) => expression switch
    {
        BoundCall or BoundInvocation or BoundNew or BoundNonNull or BoundTry => true,
        BoundIs pattern when Patterns.Bindings(pattern.Pattern).Any() => true,
        BoundConversion { Type.IsEnum: true } => true,
        BoundConstruction construction when !construction.Constructor.Symbol.IsImplicit
            || NeedsDefaultCode(construction.Type) => true,
        BoundBinary binary when MayPanic(binary) => true,
        BoundIndex { Target.Type.IsFixedArray: true, Index: not BoundLiteral } => true,
        BoundUnary { Operator: BoundUnaryOperator.Negate } unary when unary.Type.IsInteger => true,
        _ => BoundTree.Children(expression).Any(HasEffects),
    };

    /// <summary>Whether evaluating the expression may modify locals, parameters, or fields of values, other than by running a lambda.</summary>
    static bool Writes(BoundExpression expression) => expression switch
    {
        BoundInvocation invocation when invocation.Callee.Type.NonNull.FunctionParameters.Any(p => p.RefKind == RefKind.Ref) => true,
        BoundCall call when call.Method.Symbol.Parameters.Any(p => p.RefKind == RefKind.Ref)
            || (call.Receiver is not null && !call.Receiver.Type.IsReference && !call.Method.Symbol.IsReadOnly) => true,
        BoundConstruction construction when construction.Constructor.Symbol.Parameters.Any(p => p.RefKind == RefKind.Ref) => true,
        BoundNew creation when creation.Constructor.Symbol.Parameters.Any(p => p.RefKind == RefKind.Ref) => true,
        BoundIs pattern when Patterns.Bindings(pattern.Pattern).Any() => true,
        _ => BoundTree.Children(expression).Any(Writes),
    };

    /// <summary>
    /// Whether evaluating the expression may run a lambda: a call of a function value, or a call
    /// given one. Only a non-escaping lambda can change a local, and it can only run while a call it
    /// was passed to, or a call of it, runs.
    /// </summary>
    static bool RunsLambdas(BoundExpression expression) => expression switch
    {
        BoundInvocation => true,
        BoundCall call when call.Arguments.Any(IsFunctionValue) => true,
        BoundConstruction construction when construction.Arguments.Any(IsFunctionValue) => true,
        BoundNew creation when creation.Arguments.Any(IsFunctionValue) => true,
        _ => BoundTree.Children(expression).Any(RunsLambdas),
    };

    static bool IsFunctionValue(BoundExpression expression) => expression.Type.NonNull.IsFunction;

    /// <summary>Locals that a non-escaping lambda captures and that are changed anywhere, so that running a lambda may change them.</summary>
    HashSet<LocalSymbol> lambdaChanged = [];

    static HashSet<LocalSymbol> FindLambdaChanged(BoundProgram program) =>
    [
        .. FunctionValues.Lambdas(program).Where(l => !l.Type.IsEscaping)
            .SelectMany(l => BoundTree.Captures(l).Locals.Select(a => a.Local))
            .Where(l => l.IsModified),
    ];

    bool ReadsLambdaChanged(BoundExpression expression) =>
        expression is BoundLocalAccess access && lambdaChanged.Contains(access.Local) || BoundTree.Children(expression).Any(ReadsLambdaChanged);

    /// <summary>Whether evaluating the expression may run user code, which can modify any object.</summary>
    static bool WritesHeap(BoundExpression expression) => RunsLambdas(expression) || expression switch
    {
        BoundCall { Method.Symbol: { IsBuiltin: false } or { IsExtern: true } } or BoundNew or BoundInvocation => true,
        BoundConstruction construction when !construction.Constructor.Symbol.IsImplicit => true,
        _ => BoundTree.Children(expression).Any(WritesHeap),
    };

    static bool ReadsState(BoundExpression expression) =>
        expression is BoundLocalAccess or BoundThis or BoundFieldAccess or BoundIndex || BoundTree.Children(expression).Any(ReadsState);

    /// <summary>
    /// Whether the expression reads storage that a call could change: fields of objects, and
    /// whatever a <c>ref</c>/<c>in</c> parameter or a struct method's <c>this</c> refers to,
    /// which may be inside an object.
    /// </summary>
    static bool ReadsHeap(BoundExpression expression) => expression switch
    {
        BoundFieldAccess { Receiver.Type.IsReference: true } => true,
        BoundLocalAccess { Local.RefKind: not RefKind.None } => true,
        BoundIndex { Target.Type.IsPointer: true } => true,
        BoundThis { Type.IsStructOrEnum: true } => true,
        _ => BoundTree.Children(expression).Any(ReadsHeap),
    };

    static bool MayPanic(BoundBinary binary) => binary.Left.Type.IsInteger && binary.Operator switch
    {
        BoundBinaryOperator.Add or BoundBinaryOperator.Subtract or BoundBinaryOperator.Multiply => true,
        BoundBinaryOperator.Divide or BoundBinaryOperator.Remainder =>
            binary.Right is not BoundLiteral { Value: int and not 0 and not -1 } and not BoundLiteral { Value: long and not 0L and not -1L },
        _ => false,
    };
}
