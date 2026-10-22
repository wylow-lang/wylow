using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

public sealed partial class Binder
{
    BoundExpression BindIndex(IndexExpr e)
    {
        var target = BindExpression(e.Target);
        var arguments = BindArguments(e.Arguments);
        if (target.Type == VplType.Error || arguments.Any(a => a.Expression.Type == VplType.Error))
            return new BoundErrorExpression(e.Location);
        target = RequireNonNull(target);
        if (target.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);

        var type = target.Type;
        if (type.IsFixedArray || type.IsPointer)
        {
            if (arguments.Count != 1 || arguments[0].IsRef)
                return ErrorExpression(e.Location, $"an element of '{type}' is selected by a single index, as in 'a[i]'");
            if (type.IsPointer && !InUnsafe)
                return ErrorExpression(e.Location, "indexing a pointer is only allowed in 'unsafe' code");
            var index = Convert(arguments[0].Expression, VplType.Int);
            if (type.IsFixedArray && index is BoundLiteral { Value: int constant } && (constant < 0 || constant >= type.FixedLength))
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
        if (element == VplType.Error || length.Expression.Type == VplType.Error)
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
    bool CheckArrayElementsDefaultable(Location location, VplType type, MethodReference constructor)
    {
        if (type.Symbol != stdlib.Array || constructor.Symbol.Parameters.Count != 1)
            return true;
        var element = type.TypeArguments[0];
        if (Conversions.IsDefaultable(element))
            return true;
        Error(location, $"'{element}' has no default value to fill a new array with; use 'new Array<{element}>(length, value)'");
        return false;
    }

    BoundExpression BindArrayLiteral(ArrayLiteralExpr e, VplType? expected)
    {
        var elementExpected = expected is { IsFixedArray: true } ? expected.ElementType : null;
        var elements = e.Elements.Select(element => BindExpression(element, elementExpected)).ToList();
        if (elements.Any(element => element.Type == VplType.Error))
            return new BoundErrorExpression(e.Location);
        if (elements.FirstOrDefault(element => element.Type == VplType.Void) is { } empty)
            return ErrorExpression(empty.Location, "an array element must have a value");

        var elementType = elementExpected ?? (elements.Count == 0 ? null : CommonType(elements));
        // An element is stored, so a function is escaping; converting a non-escaping one reports why it can't be.
        if (elementType is { IsFunction: true })
            elementType = elementType.Escaping;
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
