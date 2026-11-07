using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

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
                parts.Add(new BoundLiteral(e.Location, WylType.String, e.Texts[i]));
            if (i < e.Values.Count)
            {
                var value = BindInterpolatedValue(e.Values[i]);
                failed |= value.Type == WylType.Error;
                parts.Add(value);
            }
        }
        if (failed)
            return new BoundErrorExpression(e.Location);
        var interpolate = Builtins.Interpolate(parts.Select(p => p.Type));
        return new BoundCall(e.Location, new MethodReference(interpolate, null, []), null, parts);
    }

    /// <summary>A hole's value, with its format specifier and alignment applied by <see cref="Builtins.Formatted"/>.</summary>
    BoundExpression BindInterpolatedValue(InterpolatedValue hole)
    {
        var value = BindExpression(hole.Value);
        if (value.Type == WylType.Error)
            return value;
        var specifier = hole.Format is { } format ? BindFormatSpecifier(hole.FormatLocation, format, value.Type) : (Kind: '\0', Precision: -1);
        var text = BindPrintable(hole.Value.Location, value);
        if (text.Type == WylType.Error || specifier is null)
            return new BoundErrorExpression(hole.Location);
        if (hole.Format is null && hole.Alignment is null)
            return text;
        return new BoundCall(hole.Location, new MethodReference(Builtins.Formatted(text.Type), null, []), null, [
            text,
            new BoundLiteral(hole.Location, WylType.Int, (int)specifier.Value.Kind),
            new BoundLiteral(hole.Location, WylType.Int, specifier.Value.Precision),
            new BoundLiteral(hole.Location, WylType.Int, hole.Alignment ?? 0),
        ]);
    }

    /// <summary>
    /// A format specifier, a letter and an optional precision of up to two digits:
    /// <c>D</c>, <c>X</c>, and <c>B</c> for integers, <c>F</c> and <c>E</c> for floating point.
    /// </summary>
    (char Kind, int Precision)? BindFormatSpecifier(Location location, string format, WylType type)
    {
        var target = type.NonNull;
        if (format.Length is < 1 or > 3 || !char.IsAsciiLetter(format[0]) || !format[1..].All(char.IsAsciiDigit))
        {
            Error(Catalog.FormatSpecifierSyntax(location, format));
            return null;
        }
        var kind = format[0];
        var applies = char.ToUpperInvariant(kind) switch
        {
            'D' or 'X' or 'B' => (Holds: target.IsInteger, Known: true, Integers: true),
            'F' or 'E' => (Holds: target.IsFloatingPoint, Known: true, Integers: false),
            _ => (Holds: false, Known: false, Integers: false),
        };
        if (!applies.Known)
        {
            Error(Catalog.UnknownFormatSpecifier(location, kind));
            return null;
        }
        if (!applies.Holds)
        {
            Error(Catalog.FormatSpecifierType(location, format, applies.Integers, type.ToString()));
            return null;
        }
        return (kind, format.Length > 1 ? int.Parse(format[1..]) : -1);
    }

    /// <summary>A value the runtime can format: the value itself if it is a formatted type, otherwise its <c>Printable.toString()</c>.</summary>
    BoundExpression BindPrintable(Location location, BoundExpression value)
    {
        if (value.Type.IsFormatted)
            return value;

        var type = value.Type.NonNull;
        if (!stdlib.IsPrintable(type, currentFunction))
        {
            var hasToString = type.Symbol is { } s && MethodCandidates(type, "toString").Any(c => c.Symbol.Parameters.Count == 0) ? s.Name : null;
            return ErrorExpression(Catalog.NotPrintable(location, value.Type.ToString(), hasToString));
        }
        if (!value.Type.IsNullable)
            return BindMethodCall(location, value, "toString", location, null, []);
        var nonNull = new LocalSymbol("value", type, isParameter: false) { IsTemporary = true };
        var call = BindMethodCall(location, new BoundLocalAccess(location, nonNull), "toString", location, null, []);
        return MakeConditionalAccess(location, value, nonNull, call);
    }
}
