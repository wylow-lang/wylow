using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

/// <summary>
/// For-each loops over ranges become <see cref="BoundForRange"/>. A for-each over an iterator
/// becomes a <see cref="BoundFor"/> that calls <c>moveNext</c> on a temporary that the iterator
/// moves or is copied into. Every other for-each is lowered to a counting <see cref="BoundFor"/>
/// over a temporary that holds what is iterated, so the loop borrows it exactly as a <c>ref</c> or
/// span variable would.
/// </summary>
public sealed partial class Binder
{
    BoundStatement BindForEach(ForEachStmt s)
    {
        scope = new Scope(scope);
        var result = s.RangeEnd is null ? BindCollectionLoop(s) : BindRangeLoop(s);
        scope = scope.Parent;
        return result;
    }

    BoundStatement BindLoopBody(BlockStmt body)
    {
        loopDepth++;
        var result = BindBlock(body);
        loopDepth--;
        return result;
    }

    BoundStatement BindRangeLoop(ForEachStmt s)
    {
        var start = BindExpression(s.Iterable);
        var end = BindExpression(s.RangeEnd!);
        var element = s.Element;
        var declared = LoopVariableType(element) is { } typeSyntax ? BindType(typeSyntax, allowVoid: false) : null;
        if (s.Index is not null)
            Error(Catalog.RangeIndex(s.Index.Location));
        if (element.RefKind != RefKind.None)
            Error(Catalog.RangeBorrowed(element.Location, FunctionSymbol.RefPrefix(element.RefKind).Trim()));

        var type = declared;
        if (start.Type != WylType.Error && end.Type != WylType.Error && type is null)
        {
            type = start.Type.IsInteger && end.Type.IsInteger ? CommonNumericType(start, end) : null;
            if (type is null)
                Error(Catalog.RangeBounds(s.Iterable.Location, start.Type.ToString(), end.Type.ToString()));
        }
        else if (type is not null && type != WylType.Error && !type.IsInteger)
        {
            Error(Catalog.RangeVariableType(element.Type!.Location, type.ToString()));
            type = WylType.Error;
        }
        type ??= WylType.Error;

        var local = new LocalSymbol(element.Name, type, isParameter: false) { IsMutable = element.IsMutable };
        Declare(local, element.NameLocation);
        var body = BindLoopBody(s.Body);
        return new BoundForRange(s.Location, local, Convert(start, type), Convert(end, type), s.IsInclusive, body);
    }

    BoundStatement BindCollectionLoop(ForEachStmt s)
    {
        // A nullable local can be looped over where it is known not to be null, as members are used.
        var iterable = AssumeNonNull(BindExpression(s.Iterable));
        if (iterable.Type == WylType.Error)
        {
            DeclareLoopVariables(s, WylType.Error);
            BindLoopBody(s.Body);
            return ErrorStatement(s.Location);
        }

        // The loop views the elements, of a fixed array or a span, or through the span a value converts to.
        if (!iterable.Type.IsReference && PlacePath.ViewedNullableValue(iterable) is { } root
            && (iterable.Type.IsFixedArray || stdlib.IsSpan(iterable.Type) || ViewedElement(iterable.Type, readOnly: true) is not null))
        {
            DeclareLoopVariables(s, WylType.Error);
            BindLoopBody(s.Body);
            Error(Catalog.LoopInsideNullable(s.Iterable.Location, root.Name));
            return ErrorStatement(s.Location);
        }
        if (IteratorTypes(iterable.Type) is [_, ..] iterators)
            return BindIteratorLoop(s, iterable, iterators);
        if (iterable.Type.IsFixedArray)
            return BindFixedArrayLoop(s, iterable);
        if (iterable.Type == WylType.String)
            return BindStringLoop(s, iterable);
        if (BindSpanLoop(s, iterable) is { } spanLoop)
            return spanLoop;

        DeclareLoopVariables(s, WylType.Error);
        BindLoopBody(s.Body);
        Error(Catalog.NotIterable(s.Iterable.Location, iterable.Type.ToString()));
        return ErrorStatement(s.Location);
    }

    /// <summary>The type a loop variable declares, if it isn't 'var'. Every element exists, so it can't be <c>ref?</c>.</summary>
    TypeSyntax? LoopVariableType(ForEachVariable variable)
    {
        if (variable.Type is not { Shape: TypeShape.NullableReference } syntax)
            return variable.Type;
        var keyword = FunctionSymbol.RefPrefix(syntax.ReferenceKind).Trim();
        Error(Catalog.NullableLoopVariable(syntax.Location, keyword));
        return syntax.Element;
    }

    void DeclareLoopVariables(ForEachStmt s, WylType elementType)
    {
        if (s.Index is { } index)
            Declare(new LocalSymbol(index.Name, WylType.Int, isParameter: false), index.NameLocation);
        Declare(new LocalSymbol(s.Element.Name, elementType, isParameter: false, s.Element.RefKind), s.Element.NameLocation);
    }

    /// <summary>
    /// Loops over a fixed array through a reference to it, or through a copy of a temporary one, which
    /// a <c>ref</c> loop would change only to lose,
    /// as <c>for (int i = 0; i &lt; N; i++) { element = array[i]; ... }</c>.
    /// </summary>
    BoundStatement BindFixedArrayLoop(ForEachStmt s, BoundExpression iterable)
    {
        var location = s.Iterable.Location;
        var (kind, _) = ClassifyPlace(iterable);
        var arrayRefKind = kind switch
        {
            PlaceKind.Mutable => RefKind.Ref,
            PlaceKind.ReadOnly => RefKind.In,
            _ => RefKind.None,
        };
        var array = new LocalSymbol("array", iterable.Type, isParameter: false, arrayRefKind) { IsTemporary = true };
        var counter = new LocalSymbol("i", WylType.Int, isParameter: false) { IsTemporary = true, IsMutable = true };
        var element = new BoundIndex(location, new BoundLocalAccess(location, array), new BoundLocalAccess(location, counter));
        bool readOnly = kind == PlaceKind.ReadOnly && s.Element.RefKind == RefKind.Ref;
        if (readOnly)
            Error(Catalog.LoopRefOverReadOnly(location, TextOf(location), ClassifyPlace(iterable).Reason));
        else if (kind == PlaceKind.Temporary && s.Element.RefKind == RefKind.Ref)
            Error(Catalog.LoopRefOverTemporary(location));

        var loop = BindCountingLoop(s, counter, new BoundLiteral(location, WylType.Int, ((FixedArrayType)iterable.Type).Length), readOnly ? null : element);
        return new BoundBlock(s.Location, [new BoundVariableDeclaration(location, array, iterable), loop]);
    }

    /// <summary>
    /// Loops over a string's characters, decoding each from the bytes, as
    /// <c>for (int i = 0; i &lt; text.byteLength; i = text.nextChar(i)) { c = text.charAt(i); ... }</c>,
    /// so the index is the character's byte offset. A string that could be reassigned is copied
    /// first, so the loop keeps reading the one it started with.
    /// </summary>
    BoundStatement BindStringLoop(ForEachStmt s, BoundExpression iterable)
    {
        var location = s.Iterable.Location;
        bool borrowed = s.Element.RefKind != RefKind.None;
        if (borrowed)
            Error(Catalog.StringCharactersBorrowed(s.Element.Location, FunctionSymbol.RefPrefix(s.Element.RefKind).Trim()));

        var statements = new List<BoundStatement>();
        Func<BoundExpression> text;
        if (iterable is BoundLiteral literal)
        {
            text = () => new BoundLiteral(location, WylType.String, literal.Value);
        }
        else
        {
            var local = iterable is BoundLocalAccess { Local: { IsWritable: false, RefKind: RefKind.None } fixedLocal } && fixedLocal.Type == WylType.String
                ? fixedLocal
                : new LocalSymbol("text", WylType.String, isParameter: false) { IsTemporary = true };
            if (local.IsTemporary)
                statements.Add(new BoundVariableDeclaration(location, local, iterable));
            text = () => new BoundLocalAccess(location, local);
        }
        var counter = new LocalSymbol("i", WylType.Int, isParameter: false) { IsTemporary = true, IsMutable = true };
        BoundCall Member(string name, params BoundExpression[] arguments)
        {
            var method = LookupMethods(WylType.String, name).First(c => c.Symbol.Parameters.Count == arguments.Length);
            return new BoundCall(location, new MethodReference(method.Symbol, method.Owner, []), text(), arguments);
        }

        BoundExpression Counter() => new BoundLocalAccess(location, counter);
        var element = borrowed ? null : Member("charAt", Counter());
        statements.Add(BindCountingLoop(s, counter, Member("byteLength"), element, Member("nextChar", Counter())));
        return new BoundBlock(s.Location, statements);
    }

    /// <summary>
    /// The <c>Iterator&lt;T&gt;</c>, <c>RefIterator&lt;T&gt;</c>, and <c>InIterator&lt;T&gt;</c> types
    /// that a type is or implements. A loop over a value of the type iterates it only if there is
    /// exactly one.
    /// </summary>
    List<WylType> IteratorTypes(WylType type)
    {
        TypeSymbol?[] iterators = [stdlib.Iterator, stdlib.RefIterator, stdlib.InIterator];
        return [.. ((IEnumerable<WylType>)[type, .. Conversions.Supertypes(type, currentFunction)])
            .Where(t => !t.IsNullable && t.Symbol is { } symbol && iterators.Contains(symbol))];
    }

    /// <summary>
    /// Loops over an iterator, which the loop takes by value into a temporary, as
    /// <c>{ var iterator = source; for (int i = 0; iterator.moveNext(); i++) { element = iterator.current(); ... } }</c>.
    /// The temporary is released when the block ends, however the loop ends. The counter exists
    /// only for an index. A loop over a <c>RefIterator</c> or an <c>InIterator</c> binds the element
    /// by the variable's own kind of reference, whose hold lasts until the iteration ends.
    /// </summary>
    BoundStatement BindIteratorLoop(ForEachStmt s, BoundExpression iterable, List<WylType> iterators)
    {
        var location = s.Iterable.Location;
        var type = iterable.Type;
        Diagnostic? error = null;
        if (iterators is [var first, var second, ..])
            error = Catalog.IteratorTypeArguments(location, type.ToString(), first.ToString(), second.ToString());
        else if (ViewedElement(type, readOnly: true) is not null)
            error = Catalog.IteratorAndSpan(location, type.ToString(), TextOf(location));
        if (error is not null)
        {
            DeclareLoopVariables(s, WylType.Error);
            BindLoopBody(s.Body);
            Error(error);
            return ErrorStatement(s.Location);
        }
        var kind = iterators[0].Symbol;
        var refKind = kind == stdlib.Iterator ? RefKind.None : s.Element.RefKind;
        bool bindable = true;
        if (kind == stdlib.Iterator && s.Element.RefKind != RefKind.None)
            Error(Catalog.IteratorValueBorrowed(s.Element.Location, FunctionSymbol.RefPrefix(s.Element.RefKind).Trim()));
        else if (kind == stdlib.InIterator && s.Element.RefKind == RefKind.Ref)
        {
            Error(Catalog.InIteratorElementRef(s.Element.Location));
            bindable = false;
        }

        var iterator = new LocalSymbol("iterator", type, isParameter: false) { IsTemporary = true, IsMutable = true };
        BoundExpression Iterator() => new BoundLocalAccess(location, iterator);
        var moveNext = BindMethodCall(location, Iterator(), "moveNext", location, null, []);
        var current = BindMethodCall(location, Iterator(), "current", location, null, []);

        var counter = s.Index is null ? null : new LocalSymbol("i", WylType.Int, isParameter: false) { IsTemporary = true, IsMutable = true };
        var declarations = new List<BoundStatement>();
        if (counter is not null)
            declarations.Add(DeclareIndex(s.Index!, counter, location));
        declarations.Add(DeclareElement(s.Element, current.Type == WylType.Error || !bindable ? null : current, refKind, loopElement: false));
        var body = BindLoopBody(s.Body);
        var loop = new BoundFor(
            s.Location,
            counter is null ? null : new BoundVariableDeclaration(location, counter, new BoundLiteral(location, WylType.Int, 0)),
            moveNext,
            counter is null ? null : Increment(counter, location),
            new BoundBlock(s.Body.Location, [.. declarations, body]));
        return new BoundBlock(s.Location, [new BoundVariableDeclaration(location, iterator, Convert(iterable, type)), loop]);
    }

    static BoundAssignment Increment(LocalSymbol counter, Location location) =>
        new(location, new BoundLocalAccess(location, counter),
            new BoundBinary(location, new BoundLocalAccess(location, counter), BoundBinaryOperator.Add, new BoundLiteral(location, WylType.Int, 1), WylType.Int));

    /// <summary>Declares a loop's index variable, a copy of <paramref name="counter"/>.</summary>
    BoundStatement DeclareIndex(ForEachVariable indexSyntax, LocalSymbol counter, Location location)
    {
        if (indexSyntax.RefKind != RefKind.None)
            Error(Catalog.IndexBorrowed(indexSyntax.Location));
        if (LoopVariableType(indexSyntax) is { } indexType && BindType(indexType, allowVoid: false) is var bound && bound != WylType.Int && bound != WylType.Error)
            Error(Catalog.IndexType(indexType.Location, bound.ToString()));
        var index = new LocalSymbol(indexSyntax.Name, WylType.Int, isParameter: false) { IsMutable = indexSyntax.IsMutable };
        Declare(index, indexSyntax.NameLocation);
        return new BoundVariableDeclaration(indexSyntax.Location, index, new BoundLocalAccess(location, counter));
    }

    /// <summary>
    /// Declares a loop's element variable from <paramref name="element"/>, by <paramref name="refKind"/>.
    /// <paramref name="loopElement"/> says whether the element is a place in what the loop holds,
    /// which an unchanged copy may refer to in place instead.
    /// </summary>
    BoundStatement DeclareElement(ForEachVariable elementSyntax, BoundExpression? element, RefKind refKind, bool loopElement)
    {
        var declared = LoopVariableType(elementSyntax) is { } typeSyntax ? BindType(typeSyntax, allowVoid: false) : null;
        var type = declared ?? element?.Type ?? WylType.Error;
        BoundExpression? value = element;
        if (element is null || type == WylType.Error)
            value = null;
        else if (refKind == RefKind.None)
            value = Convert(element, type);
        else if (!CheckReference(element, refKind, type, $"'{FunctionSymbol.RefPrefix(refKind).Trim()}' variable"))
            value = null;
        var local = new LocalSymbol(elementSyntax.Name, type, isParameter: false, refKind)
        {
            LoopElement = loopElement && refKind == RefKind.None ? elementSyntax.Location : null,
            IsMutable = elementSyntax.IsMutable,
        };
        Declare(local, elementSyntax.NameLocation);
        return new BoundVariableDeclaration(elementSyntax.Location, local, value ?? new BoundErrorExpression(elementSyntax.Location));
    }

    /// <summary>
    /// <c>for (int counter = 0; counter &lt; length; counter = next)</c>, by default <c>counter + 1</c>,
    /// whose body declares the loop's variables from <paramref name="element"/> and the counter,
    /// then runs the loop's body.
    /// </summary>
    BoundStatement BindCountingLoop(ForEachStmt s, LocalSymbol counter, BoundExpression length, BoundExpression? element, BoundExpression? next = null)
    {
        var location = s.Iterable.Location;
        var declarations = new List<BoundStatement>();
        if (s.Index is { } indexSyntax)
            declarations.Add(DeclareIndex(indexSyntax, counter, location));
        declarations.Add(DeclareElement(s.Element, element, s.Element.RefKind, loopElement: true));

        var body = BindLoopBody(s.Body);
        var start = new BoundVariableDeclaration(location, counter, new BoundLiteral(location, WylType.Int, 0));
        var condition = new BoundBinary(location, new BoundLocalAccess(location, counter), BoundBinaryOperator.Less, length, WylType.Bool);
        var update = next is null ? Increment(counter, location) : new BoundAssignment(location, new BoundLocalAccess(location, counter), next);
        return new BoundFor(s.Location, start, condition, update, new BoundBlock(s.Body.Location, [.. declarations, body]));
    }
}
