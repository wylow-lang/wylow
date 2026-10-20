using System.Globalization;
using System.Text;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.CodeGen;

public sealed partial class CppEmitter
{
    /// <summary>
    /// The object whose storage holds a place, when it may die while the place is in use: the
    /// nearest object in a field path, unless it is <c>this</c> or a local. Locals are safe because
    /// nothing else can assign them; object locals can't even be passed by <c>ref</c>.
    /// </summary>
    static BoundExpression? PinRoot(BoundExpression place)
    {
        var current = place;
        while (current is BoundFieldAccess field)
        {
            if (field.Receiver.Type.IsReference)
                return IsStable(field.Receiver) ? null : field.Receiver;
            current = field.Receiver;
        }
        return null;
    }

    static bool IsStable(BoundExpression expression) => expression is BoundThis or BoundLocalAccess;

    /// <summary>Whether releasing a value of the type may destroy an object.</summary>
    static bool MayHoldReferences(VplType type) => MayHoldReferences(type, []);

    static bool MayHoldReferences(VplType type, HashSet<VplType> visiting)
    {
        if (type.IsReference || type.IsTypeParameter)
            return true;
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
        {
            // A temporary pin lives until the end of the full expression, which includes the call.
            return combine([.. operands.Select(o =>
            {
                var text = OperandText(o, o.PinRoot is { } root ? $"vpl::pin({Expression(root)})" : null);
                return o.Keep ? $"vpl::keep({text})" : text;
            })]);
        }

        var body = new StringBuilder("[&] { ");
        var temps = new List<string>();
        foreach (var operand in operands)
        {
            string? pin = null;
            if (operand.PinRoot is { } root)
            {
                pin = NewTemp();
                body.Append($"auto {pin} = {Expression(root)}; ");
            }
            var temp = NewTemp();
            body.Append($"{(operand.ByReference ? "auto&&" : "auto")} {temp} = {OperandText(operand, pin)}; ");
            temps.Add(temp);
        }
        return body.Append($"return {combine(temps)}; }}()").ToString();
    }

    string OperandText(Operand operand, string? pinRootText)
    {
        if (pinRootText is not null)
            replacements[operand.PinRoot!] = pinRootText;
        var text = operand.ByReference ? Place(operand.Expression) : Expression(operand.Expression);
        if (pinRootText is not null)
            replacements.Remove(operand.PinRoot!);
        return operand.PinValue ? $"vpl::pin({text})" : text;
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
    // state. Any call may write any object, so object fields count as read by every call.
    bool Conflicts(Operand a, Operand b) =>
        (HasEffects(a.Expression) && HasEffects(b.Expression))
        || (Writes(a.Expression) && Reads(b, ReadsState))
        || (Writes(b.Expression) && Reads(a, ReadsState))
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

    static IEnumerable<BoundExpression> Children(BoundExpression expression) => expression switch
    {
        BoundUnary unary => [unary.Operand],
        BoundBinary binary => [binary.Left, binary.Right],
        BoundCall call => call.Receiver is null ? call.Arguments : [call.Receiver, .. call.Arguments],
        BoundConstruction construction => [.. construction.Arguments, .. construction.Initializers.Select(i => i.Value)],
        BoundNew creation => creation.Arguments,
        BoundConversion conversion => [conversion.Operand],
        BoundConditional conditional => [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse],
        BoundFieldAccess field => [field.Receiver],
        BoundNonNull nonNull => [nonNull.Operand],
        BoundCoalesce coalesce => [coalesce.Left, coalesce.Right],
        BoundConditionalAccess access => [access.Receiver, access.WhenNotNull],
        BoundIs pattern => [pattern.Operand],
        BoundSwitchExpression switchExpression => [switchExpression.Value, .. switchExpression.Cases.Select(c => c.Body)],
        BoundVariant variant => variant.Arguments,
        BoundTry attempt => [attempt.Operand],
        _ => [],
    };

    bool HasEffects(BoundExpression expression) => expression switch
    {
        BoundCall or BoundNew or BoundNonNull or BoundTry => true,
        BoundIs pattern when Patterns.Bindings(pattern.Pattern).Any() => true,
        BoundConversion { Type.IsEnum: true } => true,
        BoundConstruction construction when !construction.Constructor.Symbol.IsImplicit
            || NeedsDefaultCode(construction.Type) => true,
        BoundBinary binary when MayPanic(binary) => true,
        BoundUnary { Operator: BoundUnaryOperator.Negate } unary when unary.Type.IsInteger => true,
        _ => Children(expression).Any(HasEffects),
    };

    /// <summary>Whether evaluating the expression may modify locals, parameters, or fields of values.</summary>
    static bool Writes(BoundExpression expression) => expression switch
    {
        BoundCall call when call.Method.Symbol.Parameters.Any(p => p.RefKind == RefKind.Ref)
            || (call.Receiver is not null && !call.Receiver.Type.IsReference && !call.Method.Symbol.IsReadOnly) => true,
        BoundConstruction construction when construction.Constructor.Symbol.Parameters.Any(p => p.RefKind == RefKind.Ref) => true,
        BoundNew creation when creation.Constructor.Symbol.Parameters.Any(p => p.RefKind == RefKind.Ref) => true,
        BoundIs pattern when Patterns.Bindings(pattern.Pattern).Any() => true,
        _ => Children(expression).Any(Writes),
    };

    /// <summary>Whether evaluating the expression may run user code, which can modify any object.</summary>
    static bool WritesHeap(BoundExpression expression) => expression switch
    {
        BoundCall { Method.Symbol.IsBuiltin: false } or BoundNew => true,
        BoundConstruction construction when !construction.Constructor.Symbol.IsImplicit => true,
        _ => Children(expression).Any(WritesHeap),
    };

    static bool ReadsState(BoundExpression expression) =>
        expression is BoundLocalAccess or BoundThis or BoundFieldAccess || Children(expression).Any(ReadsState);

    /// <summary>
    /// Whether the expression reads storage that a call could change: fields of objects, and
    /// whatever a <c>ref</c>/<c>in</c> parameter or a struct method's <c>this</c> refers to,
    /// which may be inside an object.
    /// </summary>
    static bool ReadsHeap(BoundExpression expression) => expression switch
    {
        BoundFieldAccess { Receiver.Type.IsReference: true } => true,
        BoundLocalAccess { Local.RefKind: not RefKind.None } => true,
        BoundThis { Type.IsStructOrEnum: true } => true,
        _ => Children(expression).Any(ReadsHeap),
    };

    static bool MayPanic(BoundBinary binary) => binary.Left.Type.IsInteger && binary.Operator switch
    {
        BoundBinaryOperator.Add or BoundBinaryOperator.Subtract or BoundBinaryOperator.Multiply => true,
        BoundBinaryOperator.Divide or BoundBinaryOperator.Remainder =>
            binary.Right is not BoundLiteral { Value: int and not 0 and not -1 } and not BoundLiteral { Value: long and not 0L and not -1L },
        _ => false,
    };
}
