using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

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
        var declared = element.Type is null ? null : BindType(element.Type, allowVoid: false);
        if (s.Index is not null)
            Error(s.Index.Location, "a range has no index; loop over its values alone");
        if (element.RefKind != RefKind.None)
            Error(element.Location, $"the values of a range cannot be borrowed with '{FunctionSymbol.RefPrefix(element.RefKind).Trim()}'");

        var type = declared;
        if (start.Type != VplType.Error && end.Type != VplType.Error && type is null)
        {
            type = start.Type.IsInteger && end.Type.IsInteger ? CommonNumericType(start, end) : null;
            if (type is null)
                Error(s.Iterable.Location, $"a range must be of integers, not '{start.Type}' and '{end.Type}'");
        }
        else if (type is not null && type != VplType.Error && !type.IsInteger)
        {
            Error(element.Type!.Location, $"a range's values are integers, not '{type}'");
            type = VplType.Error;
        }
        type ??= VplType.Error;

        var local = new LocalSymbol(element.Name, type, isParameter: false);
        Declare(local, element.NameLocation);
        var body = BindLoopBody(s.Body);
        return new BoundForRange(s.Location, local, Convert(start, type), Convert(end, type), s.IsInclusive, body);
    }

    BoundStatement BindCollectionLoop(ForEachStmt s)
    {
        var iterable = BindExpression(s.Iterable);
        if (iterable.Type == VplType.Error)
        {
            DeclareLoopVariables(s, VplType.Error);
            BindLoopBody(s.Body);
            return ErrorStatement(s.Location);
        }

        if (iterable.Type.IsFixedArray)
            return BindFixedArrayLoop(s, iterable);
        if (BindSpanLoop(s, iterable) is { } spanLoop)
            return spanLoop;

        DeclareLoopVariables(s, VplType.Error);
        BindLoopBody(s.Body);
        Error(s.Iterable.Location, $"cannot loop over '{iterable.Type}'");
        return ErrorStatement(s.Location);
    }

    void DeclareLoopVariables(ForEachStmt s, VplType elementType)
    {
        if (s.Index is { } index)
            Declare(new LocalSymbol(index.Name, VplType.Int, isParameter: false), index.NameLocation);
        Declare(new LocalSymbol(s.Element.Name, elementType, isParameter: false, s.Element.RefKind), s.Element.NameLocation);
    }

    /// <summary>
    /// Loops over a fixed array through a reference to it, or through a copy of a temporary one,
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
        var counter = new LocalSymbol("i", VplType.Int, isParameter: false) { IsTemporary = true };
        var element = new BoundIndex(location, new BoundLocalAccess(location, array), new BoundLocalAccess(location, counter));
        bool readOnly = kind == PlaceKind.ReadOnly && s.Element.RefKind == RefKind.Ref;
        if (readOnly)
            Error(location, $"cannot loop over '{TextOf(location)}' by 'ref' because {ClassifyPlace(iterable).Reason}; use 'in'");

        var loop = BindCountingLoop(s, counter, new BoundLiteral(location, VplType.Int, iterable.Type.FixedLength), readOnly ? null : element);
        return new BoundBlock(s.Location, [new BoundVariableDeclaration(location, array, iterable), loop]);
    }

    /// <summary>
    /// <c>for (int counter = 0; counter &lt; length; counter++)</c>, whose body declares the loop's
    /// variables from <paramref name="element"/> and the counter, then runs the loop's body.
    /// </summary>
    BoundStatement BindCountingLoop(ForEachStmt s, LocalSymbol counter, BoundExpression length, BoundExpression? element)
    {
        var location = s.Iterable.Location;
        BoundExpression Counter() => new BoundLocalAccess(location, counter);
        var declarations = new List<BoundStatement>();

        if (s.Index is { } indexSyntax)
        {
            if (indexSyntax.RefKind != RefKind.None)
                Error(indexSyntax.Location, "the index cannot be borrowed; it is a copy of the position");
            if (indexSyntax.Type is { } indexType && BindType(indexType, allowVoid: false) is var bound && bound != VplType.Int && bound != VplType.Error)
                Error(indexType.Location, $"the index of a loop is an 'int', not '{bound}'");
            var index = new LocalSymbol(indexSyntax.Name, VplType.Int, isParameter: false);
            Declare(index, indexSyntax.NameLocation);
            declarations.Add(new BoundVariableDeclaration(indexSyntax.Location, index, Counter()));
        }

        var elementSyntax = s.Element;
        var declared = elementSyntax.Type is null ? null : BindType(elementSyntax.Type, allowVoid: false);
        var type = declared ?? element?.Type ?? VplType.Error;
        BoundExpression? value = element;
        if (element is null || type == VplType.Error)
            value = null;
        else if (elementSyntax.RefKind == RefKind.None)
            value = Convert(element, type);
        else if (!CheckReference(element, elementSyntax.RefKind, type, $"a '{FunctionSymbol.RefPrefix(elementSyntax.RefKind).Trim()}' variable"))
            value = null;
        var local = new LocalSymbol(elementSyntax.Name, type, isParameter: false, elementSyntax.RefKind);
        Declare(local, elementSyntax.NameLocation);
        declarations.Add(new BoundVariableDeclaration(elementSyntax.Location, local, value ?? new BoundErrorExpression(elementSyntax.Location)));

        var body = BindLoopBody(s.Body);
        var start = new BoundVariableDeclaration(location, counter, new BoundLiteral(location, VplType.Int, 0));
        var condition = new BoundBinary(location, Counter(), BoundBinaryOperator.Less, length, VplType.Bool);
        var next = new BoundBinary(location, Counter(), BoundBinaryOperator.Add, new BoundLiteral(location, VplType.Int, 1), VplType.Int);
        var update = new BoundAssignment(location, Counter(), next);
        return new BoundFor(s.Location, start, condition, update, new BoundBlock(s.Body.Location, [.. declarations, body]));
    }
}
