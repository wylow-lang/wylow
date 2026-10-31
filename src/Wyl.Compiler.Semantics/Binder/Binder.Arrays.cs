using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    BoundExpression BindIndex(IndexExpr e)
    {
        var target = BindExpression(e.Target);
        var arguments = BindArguments(e.Arguments);
        if (target.Type == WylType.Error || arguments.Any(a => a.Expression.Type == WylType.Error))
            return new BoundErrorExpression(e.Location);
        target = RequireNonNull(target);
        if (target.Type == WylType.Error)
            return new BoundErrorExpression(e.Location);

        var type = target.Type;
        if (type is IndexedType)
        {
            if (arguments.Count != 1 || arguments[0].IsRef)
                return ErrorExpression(e.Location, $"an element of '{type}' is selected by a single index, as in 'a[i]'");
            if (type.IsPointer && !InUnsafe)
                return ErrorExpression(e.Location, "indexing a pointer is only allowed in 'unsafe' code");
            var index = Convert(arguments[0].Expression, WylType.Int);
            if (type is FixedArrayType array && index is BoundLiteral { Value: int constant } && (constant < 0 || constant >= array.Length))
                return ErrorExpression(index.Location, $"index {constant} is out of bounds for '{type}'");
            return new BoundIndex(e.Location, target, index);
        }

        if (!(type.IsStructOrEnum || type.IsReference) || LookupMethods(type, SyntaxFacts.IndexerName).Count == 0)
            return ErrorExpression(e.Target.Location, $"'{type}' cannot be indexed");
        return BindMethodCall(e.Location, target, SyntaxFacts.IndexerName, e.Target.Location, null, arguments);
    }

    /// <summary><c>new T[n]</c> constructs an <c>Array&lt;T&gt;</c> of default values.</summary>
    BoundExpression BindNewArray(NewArrayExpr e)
    {
        var element = BindType(e.ElementType, allowVoid: false);
        var length = new Argument(BindExpression(e.Length), false, e.Length.Location);
        if (element == WylType.Error || length.Expression.Type == WylType.Error)
            return new BoundErrorExpression(e.Location);
        if (stdlib.Array is not { } array)
            return ErrorExpression(e.Location, "'T[]' arrays are not supported yet");
        var type = Construct(array, [element], e.ElementType.Location);
        return BindNewObject(e.Location, e.ElementType.Location, type, [length]);
    }

    /// <summary>
    /// <c>new T[n]</c> fills the array with default values, which only the compiler can check
    /// exist, so it is also checked here for the constructor written out as <c>new Array&lt;T&gt;(n)</c>.
    /// </summary>
    bool CheckArrayElementsDefaultable(Location location, WylType type, MethodReference constructor)
    {
        if (type.Symbol != stdlib.Array || constructor.Symbol.Parameters.Count != 1)
            return true;
        var element = type.TypeArguments[0];
        if (element.IsDefaultable)
            return true;
        Error(location, $"'{element}' has no default value to fill a new array with; use 'new Array<{element}>(length, value)'");
        return false;
    }

    BoundExpression BindArrayLiteral(ArrayLiteralExpr e, WylType? expected)
    {
        var elementExpected = (expected as FixedArrayType)?.Element;
        var elements = e.Elements.Select(element => BindExpression(element, elementExpected)).ToList();
        if (elements.Any(element => element.Type == WylType.Error))
            return new BoundErrorExpression(e.Location);
        if (elements.FirstOrDefault(element => element.Type == WylType.Void) is { } empty)
            return ErrorExpression(empty.Location, "an array element must have a value");

        var elementType = elementExpected ?? (elements.Count == 0 ? null : CommonType(elements));
        // An element is stored, so a function is escaping; converting a non-escaping one reports why it can't be.
        if (elementType is not null)
            elementType = EscapingForm(elementType);
        if (elementType is null)
        {
            return ErrorExpression(e.Location, elements.Count == 0
                ? "cannot infer the type of an empty array; use it where a fixed array type is expected"
                : $"the elements of the array have incompatible types {string.Join(", ", elements.Select(v => $"'{v.Type}'").Distinct())}");
        }
        if (!CheckStorable(elementType, e.Location, "an array's element"))
            return new BoundErrorExpression(e.Location);
        return new BoundArrayLiteral(e.Location, elementType.FixedArrayOf(elements.Count), [.. elements.Select(element => Convert(element, elementType))]);
    }
}
