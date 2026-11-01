using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    /// <param name="assigned">Whether it is an assignment's target, which a type with <c>operator []=</c> assigns through it.</param>
    BoundExpression BindIndex(IndexExpr e, bool assigned = false)
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
        var read = BindMethodCall(e.Location, target, SyntaxFacts.IndexerName, e.Target.Location, null, arguments);
        if (!assigned || read is not BoundCall call || call.Method.Symbol.ReturnRefKind == RefKind.Ref
            || LookupMethods(type, SyntaxFacts.IndexerSetterName) is not { Count: > 0 } setters)
            return read;
        return BindIndexerElement(e, call, setters);
    }

    /// <summary>The <c>operator []=</c> whose indices are those of the <c>operator []</c> an assignment's indices resolved to.</summary>
    BoundExpression BindIndexerElement(IndexExpr e, BoundCall read, List<Candidate> setters)
    {
        var type = read.Receiver!.Type;
        var indices = read.Method.Symbol.Parameters.Count;
        var write = setters
            .Select(c => new MethodReference(c.Symbol, c.Owner, []))
            .FirstOrDefault(m => m.Symbol.Parameters.Count == indices + 1
                && Enumerable.Range(0, indices).All(i => m.ParameterRefKind(i) == read.Method.ParameterRefKind(i) && m.ParameterType(i) == read.Method.ParameterType(i)));
        if (write is null)
            return ErrorExpression(e.Location, $"'{DisplayName(type)}' has no 'operator []=' to assign an element read by '{read.Method.Symbol.Signature}'");
        CheckVisible(e.Target.Location, write.Symbol.Reach, $"'operator []=' of '{DisplayName(type)}'");
        if (!CheckUnsafeCall(e.Location, write)
            || !CheckMutReceiver(e.Location, write, read.Receiver, $"cannot assign to an element of a temporary value; {StoreTemporaryHint}"))
            return new BoundErrorExpression(e.Location);
        return new BoundIndexerElement(e.Location, read, write);
    }

    /// <summary>A hint for a value an indexer returns where a place is needed, if its type assigns elements with <c>operator []=</c> instead.</summary>
    string NotAnElementHint(BoundExpression expression) =>
        expression is BoundCall { Method.Symbol.IsIndexer: true, Receiver: { } receiver }
        && LookupMethods(receiver.Type, SyntaxFacts.IndexerSetterName).Count > 0
            ? $"; '{DisplayName(receiver.Type)}' has no element storage, and assigns its elements with 'operator []='"
            : "";

    /// <summary>
    /// <c>operator []=</c> is the writing half of an indexer (§6.6): each pairs with the
    /// <c>operator []</c> taking the same indices, whose element type it assigns, and which resolves
    /// the indices of an assignment for both. Beside a <c>ref</c> indexer, an assignment would have two meanings.
    /// </summary>
    void CheckIndexerSetters(TypeSymbol s)
    {
        var setters = LookupMethods(s.Type, SyntaxFacts.IndexerSetterName);
        if (setters.Count == 0)
            return;
        var getters = LookupMethods(s.Type, SyntaxFacts.IndexerName);
        if (getters.FirstOrDefault(g => g.Symbol.ReturnRefKind == RefKind.Ref) is { } referenceGetter)
        {
            var own = s.Methods.GetValueOrDefault(SyntaxFacts.IndexerSetterName)?.FirstOrDefault()
                ?? (referenceGetter.Symbol.ContainingType == s ? referenceGetter.Symbol : null);
            if (own is not null)
                Error(own.Declaration!.NameLocation, $"'{s.Name}' cannot have both 'operator []=' and an 'operator []' that returns 'ref', since an assignment would have two meanings");
            return;
        }
        if (!s.Methods.TryGetValue(SyntaxFacts.IndexerSetterName, out var declared))
            return;
        foreach (var setter in declared)
        {
            var location = setter.Declaration!.NameLocation;
            var parameters = setter.Parameters;
            if (setter.ReturnType != WylType.Void || setter.ReturnRefKind != RefKind.None)
                Error(location, "'operator []=' must return 'void'");
            if (setter.IsStatic)
            {
                Error(location, "'operator []=' cannot be static");
                continue;
            }
            if (parameters.Count < 2)
            {
                Error(location, "'operator []=' takes the indices, then the assigned value, as in 'void operator []=(int i, T value)'");
                continue;
            }
            if (parameters.FirstOrDefault(p => p.DefaultSyntax is not null) is { } defaulted)
                Error(defaulted.DefaultSyntax!.Location, "'operator []=' cannot have default values; an assignment takes the defaults of the matching 'operator []'");
            var value = parameters[^1];
            if (value.RefKind == RefKind.Ref)
            {
                Error(location, "the value of 'operator []=' cannot be 'ref'; it is passed by value or 'in'");
                continue;
            }
            var indices = parameters.Take(parameters.Count - 1).ToList();
            var getter = getters.FirstOrDefault(g => g.Symbol.Parameters.Count == indices.Count
                && Enumerable.Range(0, indices.Count).All(i => g.Symbol.Parameters[i].RefKind == indices[i].RefKind
                    && TypeMap.For(g.Owner!).Apply(g.Symbol.Parameters[i].Type) == indices[i].Type));
            var indexList = string.Join(", ", indices.Select(p => FunctionSymbol.RefPrefix(p.RefKind) + p.Type));
            if (getter is null || getter.Symbol.ReturnType.IsNullableReference)
            {
                Error(location, $"'operator []=' needs an 'operator []({indexList})' returning the element it assigns");
                continue;
            }
            var element = TypeMap.For(getter.Owner!).Apply(getter.Symbol.ReturnType);
            if (value.Type != element)
                Error(location, $"the value of 'operator []=' must have the element type '{element}' that 'operator []({indexList})' returns");
        }
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
