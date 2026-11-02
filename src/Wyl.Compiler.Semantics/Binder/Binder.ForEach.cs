using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

/// <summary>
/// For-each loops over ranges become <see cref="BoundForRange"/>. Every other for-each is lowered to
/// a counting <see cref="BoundFor"/> over a temporary that holds what is iterated, so the loop
/// borrows it exactly as a <c>ref</c> or span variable would.
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
    /// <c>for (int counter = 0; counter &lt; length; counter = next)</c>, by default <c>counter + 1</c>,
    /// whose body declares the loop's variables from <paramref name="element"/> and the counter,
    /// then runs the loop's body.
    /// </summary>
    BoundStatement BindCountingLoop(ForEachStmt s, LocalSymbol counter, BoundExpression length, BoundExpression? element, BoundExpression? next = null)
    {
        var location = s.Iterable.Location;
        BoundExpression Counter() => new BoundLocalAccess(location, counter);
        var declarations = new List<BoundStatement>();

        if (s.Index is { } indexSyntax)
        {
            if (indexSyntax.RefKind != RefKind.None)
                Error(Catalog.IndexBorrowed(indexSyntax.Location));
            if (LoopVariableType(indexSyntax) is { } indexType && BindType(indexType, allowVoid: false) is var bound && bound != WylType.Int && bound != WylType.Error)
                Error(Catalog.IndexType(indexType.Location, bound.ToString()));
            var index = new LocalSymbol(indexSyntax.Name, WylType.Int, isParameter: false) { IsMutable = indexSyntax.IsMutable };
            Declare(index, indexSyntax.NameLocation);
            declarations.Add(new BoundVariableDeclaration(indexSyntax.Location, index, Counter()));
        }

        var elementSyntax = s.Element;
        var declared = LoopVariableType(elementSyntax) is { } typeSyntax ? BindType(typeSyntax, allowVoid: false) : null;
        var type = declared ?? element?.Type ?? WylType.Error;
        BoundExpression? value = element;
        if (element is null || type == WylType.Error)
            value = null;
        else if (elementSyntax.RefKind == RefKind.None)
            value = Convert(element, type);
        else if (!CheckReference(element, elementSyntax.RefKind, type, $"'{FunctionSymbol.RefPrefix(elementSyntax.RefKind).Trim()}' variable"))
            value = null;
        var local = new LocalSymbol(elementSyntax.Name, type, isParameter: false, elementSyntax.RefKind)
        {
            LoopElement = elementSyntax.RefKind == RefKind.None ? elementSyntax.Location : null,
            IsMutable = elementSyntax.IsMutable,
        };
        Declare(local, elementSyntax.NameLocation);
        declarations.Add(new BoundVariableDeclaration(elementSyntax.Location, local, value ?? new BoundErrorExpression(elementSyntax.Location)));

        var body = BindLoopBody(s.Body);
        var start = new BoundVariableDeclaration(location, counter, new BoundLiteral(location, WylType.Int, 0));
        var condition = new BoundBinary(location, Counter(), BoundBinaryOperator.Less, length, WylType.Bool);
        next ??= new BoundBinary(location, Counter(), BoundBinaryOperator.Add, new BoundLiteral(location, WylType.Int, 1), WylType.Int);
        var update = new BoundAssignment(location, Counter(), next);
        return new BoundFor(s.Location, start, condition, update, new BoundBlock(s.Body.Location, [.. declarations, body]));
    }
}
