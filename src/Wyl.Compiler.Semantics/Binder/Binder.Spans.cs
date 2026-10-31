using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

/// <summary>
/// Spans and other <c>ref struct</c>s, which are second-class like <c>ref</c>: they may be locals
/// and parameters, and a method may return one that views its receiver, but they cannot be
/// stored anywhere that could outlive what they view. The compiler holds what a span views for
/// as long as the span is used; see <c>IrLowering.ObjectLocal</c>.
/// </summary>
public sealed partial class Binder
{
    /// <summary>Reports a second-class type used where it could be stored; <paramref name="what"/> names the use.</summary>
    bool CheckStorable(WylType type, Location location, string what)
    {
        if (type is NullableReferenceType reference)
        {
            Error(location, $"'{type}' cannot be {what}; it can only be held by a '{FunctionSymbol.RefPrefix(reference.Kind).Trim()}?' variable or parameter");
            return false;
        }
        if (!type.IsRefStruct)
            return true;
        Error(location, $"'{type}' cannot be {what}; like a 'ref', it can only be a local variable or parameter");
        return false;
    }

    /// <summary>
    /// The element type that a value of type <paramref name="from"/> can be viewed as, through a
    /// <c>ReadOnlySpan</c> if <paramref name="readOnly"/> or else a <c>Span</c>.
    /// </summary>
    WylType? ViewedElement(WylType from, bool readOnly)
    {
        if (from is FixedArrayType array)
            return array.Element;
        var hook = readOnly ? stdlib.ReadOnlySpannable : stdlib.Spannable;
        if (hook is null || from.IsNullable || !(from.IsStruct || from.IsReference))
            return null;
        IEnumerable<WylType> candidates = [from, .. Conversions.Supertypes(from)];
        return candidates.FirstOrDefault(t => t.Symbol == hook)?.TypeArguments[0];
    }

    /// <summary>
    /// The implicit conversion of a fixed array or a <c>Spannable</c> value to the span type
    /// <paramref name="to"/>, or null if there is none.
    /// </summary>
    BoundExpression? SpanConversion(BoundExpression expression, WylType to)
    {
        // A narrowed place may be viewed as its non-null type (02-types.md §2.5), which flow
        // analysis checks. Assigning a nullable value's local null destroys what a span would view,
        // so only one that can't be assigned again may be viewed; a reference's object is held.
        if (expression is BoundLocalAccess { AssumesNonNull: false, Type.MayBeNull: true } narrowed
            && (narrowed.Type.IsNullableReference || PlacePath.MayReferInto(narrowed.Local)))
            expression = new BoundLocalAccess(narrowed.Location, narrowed.Local, AssumesNonNull: true);
        var from = expression.Type;
        if (!stdlib.IsSpan(to) || from == to || from == WylType.Error)
            return null;
        bool readOnly = to.Symbol == stdlib.ReadOnlySpan;
        if (ViewedElement(from, readOnly) != to.TypeArguments[0])
            return null;

        var location = expression.Location;
        var (kind, _) = ClassifyPlace(expression);
        bool inNullable = PlacePath.NullableValueRoot(expression) is not null;
        if (from is FixedArrayType array)
        {
            if (kind == PlaceKind.Temporary || inNullable || (kind == PlaceKind.ReadOnly && !readOnly))
                return null;
            var constructor = to.Symbol!.Constructors.FirstOrDefault(c => c.Parameters.Count == 2);
            if (constructor is null)
                return null;
            return new BoundConstruction(location, to, new MethodReference(constructor, to, []),
                [new BoundArrayData(location, expression), new BoundLiteral(location, WylType.Int, array.Length)]);
        }

        var method = LookupMethods(from, readOnly ? "asReadOnlySpan" : "asSpan").FirstOrDefault(c => c.Symbol.Parameters.Count == 0);
        if (method is null || (!from.IsReference && (inNullable || (!method.Symbol.IsReadOnly && kind == PlaceKind.ReadOnly))))
            return null;
        return new BoundCall(location, new MethodReference(method.Symbol, method.Owner, []), expression, []);
    }

    string SpanConversionHint(BoundExpression expression, WylType to)
    {
        if (!stdlib.IsSpan(to) || ViewedElement(expression.Type.Narrowed, to.Symbol == stdlib.ReadOnlySpan) != to.TypeArguments[0])
            return "";
        // A nullable value's local that can be assigned again would destroy what the span views.
        if (PlacePath.NullableValueRoot(expression) is { } root)
            return $"; a span cannot view inside {DestroyedByNull(root)}";
        if (expression is BoundLocalAccess { Type.IsNullableValue: true, Local: var local } && !PlacePath.MayReferInto(local))
            return $"; a span cannot view inside {DestroyedByNull(local)}";
        if (expression.Type.Narrowed is not FixedArrayType array || array.Element != to.TypeArguments[0])
            return "";
        return ClassifyPlace(expression).Kind switch
        {
            PlaceKind.Temporary => "; a span can only view an array stored in a variable or field",
            PlaceKind.ReadOnly => "; the array is read-only, so view it with a 'ReadOnlySpan'",
            _ => "",
        };
    }

    /// <summary>
    /// Whether a span would view a temporary struct, which dies at the end of the statement. A
    /// temporary object is fine: the span holds it.
    /// </summary>
    bool ViewsTemporary(BoundExpression view) => PlacePath.Viewed(PlacePath.Of(view))
        .Any(viewed => viewed.Value.Type is { IsAggregate: true, IsRefStruct: false } && ClassifyPlace(viewed).Kind == PlaceKind.Temporary);

    /// <summary>
    /// Whether a value returned as a second-class type views only the method's receiver, which is
    /// what the caller holds for as long as it uses the value. An object or a string in the
    /// receiver holds its contents itself, and could be replaced while the caller uses them.
    /// </summary>
    static bool IsThisDerived(BoundExpression value) => PlacePath.Viewed(PlacePath.Of(value))
        .All(viewed => viewed.Value.Type.IsRefStruct ? viewed is { Root: BoundThis, Steps.Count: 0 } : IsThisRooted(viewed));

    /// <summary>
    /// Loops over a span: one given directly, or one the iterated value converts to, preferring a
    /// read-only one unless the loop variable is a <c>ref</c>. The span is held in a variable for the
    /// loop, so what it views is held and borrowed until the loop ends.
    /// </summary>
    BoundStatement? BindSpanLoop(ForEachStmt s, BoundExpression iterable)
    {
        var span = stdlib.IsSpan(iterable.Type) ? iterable : null;
        if (span is null && s.Element.RefKind != RefKind.Ref && ViewedElement(iterable.Type, readOnly: true) is { } readOnlyElement)
            span = SpanConversion(iterable, stdlib.ReadOnlySpan!.Construct([readOnlyElement]));
        if (span is null && ViewedElement(iterable.Type, readOnly: false) is { } element)
            span = SpanConversion(iterable, stdlib.Span!.Construct([element]));
        if (span is null)
            return null;

        var location = s.Iterable.Location;
        if (ViewsTemporary(span))
        {
            Error(location, "cannot loop over a temporary value's elements; store the value in a variable first");
            span = new BoundErrorExpression(location);
        }

        var spanLocal = new LocalSymbol("span", span.Type, isParameter: false) { IsTemporary = true };
        var counter = new LocalSymbol("i", WylType.Int, isParameter: false) { IsTemporary = true, IsMutable = true };
        BoundExpression Span() => new BoundLocalAccess(location, spanLocal);
        BoundCall Member(string name, IReadOnlyList<BoundExpression> arguments)
        {
            var method = LookupMethods(span.Type, name).First(c => c.Symbol.Parameters.Count == arguments.Count);
            return new BoundCall(location, new MethodReference(method.Symbol, method.Owner, []), Span(), arguments);
        }

        if (span.Type == WylType.Error)
        {
            DeclareLoopVariables(s, WylType.Error);
            BindLoopBody(s.Body);
            return ErrorStatement(s.Location);
        }
        var loop = BindCountingLoop(s, counter, Member("length", []), Member(SyntaxFacts.IndexerName, [new BoundLocalAccess(location, counter)]));
        return new BoundBlock(s.Location, [new BoundVariableDeclaration(location, spanLocal, span), loop]);
    }
}
