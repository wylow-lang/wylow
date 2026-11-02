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
                return ErrorExpression(Catalog.IndexCount(e.Location, type.ToString()));
            if (type.IsPointer && !InUnsafe)
                return ErrorExpression(Catalog.PointerIndexing(e.Location));
            var index = Convert(arguments[0].Expression, WylType.Int);
            if (type is FixedArrayType array && index is BoundLiteral { Value: int constant } && (constant < 0 || constant >= array.Length))
                return ErrorExpression(Catalog.IndexOutOfBounds(index.Location, constant, type.ToString()));
            return new BoundIndex(e.Location, target, index);
        }

        if (!(type.IsStructOrEnum || type.IsReference) || LookupMethods(type, SyntaxFacts.IndexerName).Count == 0)
            return ErrorExpression(Catalog.NotIndexable(e.Target.Location, type.ToString()));
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
            return ErrorExpression(Catalog.NoIndexerSetter(e.Location, DisplayName(type), read.Method.Symbol.Signature));
        CheckVisible(e.Target.Location, write.Symbol.Reach, $"'operator []=' of '{DisplayName(type)}'");
        if (!CheckUnsafeCall(e.Location, write)
            || !CheckMutReceiver(e.Location, write, read.Receiver, Catalog.ElementOfTemporary(e.Location)))
            return new BoundErrorExpression(e.Location);
        return new BoundIndexerElement(e.Location, read, write);
    }

    /// <summary>For a value an indexer returns where a place is needed, the type, if it assigns elements with <c>operator []=</c> instead.</summary>
    string? Elementless(BoundExpression expression) =>
        expression is BoundCall { Method.Symbol.IsIndexer: true, Receiver: { } receiver }
        && LookupMethods(receiver.Type, SyntaxFacts.IndexerSetterName).Count > 0
            ? DisplayName(receiver.Type)
            : null;

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
                Error(Catalog.SetterWithRefIndexer(own.Declaration!.NameLocation, s.Name));
            return;
        }
        if (!s.Methods.TryGetValue(SyntaxFacts.IndexerSetterName, out var declared))
            return;
        foreach (var setter in declared)
        {
            var location = setter.Declaration!.NameLocation;
            var parameters = setter.Parameters;
            if (setter.ReturnType != WylType.Void || setter.ReturnRefKind != RefKind.None)
                Error(Catalog.SetterReturn(location));
            if (setter.IsStatic)
            {
                Error(Catalog.StaticSetter(location));
                continue;
            }
            if (parameters.Count < 2)
            {
                Error(Catalog.SetterParameters(location));
                continue;
            }
            if (parameters.FirstOrDefault(p => p.DefaultSyntax is not null) is { } defaulted)
                Error(Catalog.SetterDefaults(defaulted.DefaultSyntax!.Location));
            var value = parameters[^1];
            if (value.RefKind == RefKind.Ref)
            {
                Error(Catalog.SetterRefValue(location));
                continue;
            }
            var indices = parameters.Take(parameters.Count - 1).ToList();
            var getter = getters.FirstOrDefault(g => g.Symbol.Parameters.Count == indices.Count
                && Enumerable.Range(0, indices.Count).All(i => g.Symbol.Parameters[i].RefKind == indices[i].RefKind
                    && TypeMap.For(g.Owner!).Apply(g.Symbol.Parameters[i].Type) == indices[i].Type));
            var indexList = string.Join(", ", indices.Select(p => FunctionSymbol.RefPrefix(p.RefKind) + p.Type));
            if (getter is null || getter.Symbol.ReturnType.IsNullableReference)
            {
                Error(Catalog.SetterWithoutGetter(location, indexList));
                continue;
            }
            var element = TypeMap.For(getter.Owner!).Apply(getter.Symbol.ReturnType);
            if (value.Type != element)
                Error(Catalog.SetterValueType(location, element.ToString(), value.Type.ToString(), indexList));
        }
    }

    /// <summary><c>new T[n]</c> constructs an <c>Array&lt;T&gt;</c> of default values.</summary>
    BoundExpression BindNewArray(NewArrayExpr e)
    {
        var element = BindType(e.ElementType, allowVoid: false);
        var length = new Argument(BindExpression(e.Length), false, e.Length.Location);
        if (element == WylType.Error || length.Expression.Type == WylType.Error)
            return new BoundErrorExpression(e.Location);
        var array = stdlib.Array ?? throw new InvalidOperationException("the standard library declares 'Array'");
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
        Error(Catalog.NoDefaultElement(location, element.ToString()));
        return false;
    }

    BoundExpression BindArrayLiteral(ArrayLiteralExpr e, WylType? expected)
    {
        var elementExpected = (expected as FixedArrayType)?.Element;
        var elements = e.Elements.Select(element => BindExpression(element, elementExpected)).ToList();
        if (elements.Any(element => element.Type == WylType.Error))
            return new BoundErrorExpression(e.Location);
        if (elements.FirstOrDefault(element => element.Type == WylType.Void) is { } empty)
            return ErrorExpression(Catalog.VoidArrayElement(empty.Location));

        var elementType = elementExpected ?? (elements.Count == 0 ? null : CommonType(elements));
        // An element is stored, so a function is escaping; converting a non-escaping one reports why it can't be.
        if (elementType is not null)
            elementType = EscapingForm(elementType);
        if (elementType is null)
        {
            return ErrorExpression(elements.Count == 0 ? Catalog.EmptyArrayType(e.Location) : Catalog.ArrayElementTypes(e.Location, elements.Select(v => v.Type.ToString()).Distinct()));
        }
        if (!CheckStorable(elementType, e.Location, "an array's element"))
            return new BoundErrorExpression(e.Location);
        return new BoundArrayLiteral(e.Location, elementType.FixedArrayOf(elements.Count), [.. elements.Select(element => Convert(element, elementType))]);
    }
}
