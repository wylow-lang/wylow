using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Semantics;

public sealed partial class Binder
{
    /// <summary><c>$"a{b}c"</c>, as a call of <see cref="Builtins.Interpolate"/> with the nonempty texts and the values in order.</summary>
    BoundExpression BindInterpolatedString(InterpolatedStringExpr e)
    {
        var parts = new List<BoundExpression>();
        bool failed = false;
        for (int i = 0; i < e.Texts.Count; i++)
        {
            if (e.Texts[i] != "")
                parts.Add(new BoundLiteral(e.Location, VplType.String, e.Texts[i]));
            if (i < e.Values.Count)
            {
                var value = BindInterpolatedValue(e.Values[i]);
                failed |= value.Type == VplType.Error;
                parts.Add(value);
            }
        }
        if (failed)
            return new BoundErrorExpression(e.Location);
        var interpolate = Builtins.Interpolate(parts.Select(p => p.Type));
        return new BoundCall(e.Location, new MethodReference(interpolate, null, []), null, parts);
    }

    /// <summary>A value the runtime can format: the value itself if it is a formatted type, otherwise its <c>Printable.toString()</c>.</summary>
    BoundExpression BindInterpolatedValue(Expr syntax)
    {
        var value = BindExpression(syntax);
        if (value.Type == VplType.Error || Builtins.FormattedTypes.Contains(value.Type))
            return value;

        var location = syntax.Location;
        var type = value.Type.NonNull;
        if (!stdlib.IsPrintable(type))
        {
            var hint = type.Symbol is { } s && MethodCandidates(type, "toString").Any(c => c.Symbol.Parameters.Count == 0)
                ? $"; its 'toString()' is used once '{s.Name}' declares 'implements Printable'"
                : "";
            return ErrorExpression(location, $"cannot interpolate a value of type '{value.Type}', which does not implement 'Printable'{hint}");
        }
        if (!value.Type.IsNullable)
            return BindMethodCall(location, value, "toString", location, null, []);
        var nonNull = new LocalSymbol("value", type, isParameter: false) { IsTemporary = true };
        var call = BindMethodCall(location, new BoundLocalAccess(location, nonNull), "toString", location, null, []);
        return MakeConditionalAccess(location, value, nonNull, call);
    }
}
