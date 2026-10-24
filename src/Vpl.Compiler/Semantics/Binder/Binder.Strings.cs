using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

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

    /// <summary>A hole's value, with its format specifier and alignment applied by <see cref="Builtins.Formatted"/>.</summary>
    BoundExpression BindInterpolatedValue(InterpolatedValue hole)
    {
        var value = BindExpression(hole.Value);
        if (value.Type == VplType.Error)
            return value;
        var specifier = hole.Format is { } format ? BindFormatSpecifier(hole.FormatLocation, format, value.Type) : (Kind: '\0', Precision: -1);
        var text = BindPrintable(hole.Value.Location, value);
        if (text.Type == VplType.Error || specifier is null)
            return new BoundErrorExpression(hole.Location);
        if (hole.Format is null && hole.Alignment is null)
            return text;
        return new BoundCall(hole.Location, new MethodReference(Builtins.Formatted(text.Type), null, []), null, [
            text,
            new BoundLiteral(hole.Location, VplType.Int, (int)specifier.Value.Kind),
            new BoundLiteral(hole.Location, VplType.Int, specifier.Value.Precision),
            new BoundLiteral(hole.Location, VplType.Int, hole.Alignment ?? 0),
        ]);
    }

    /// <summary>
    /// A format specifier, a letter and an optional precision of up to two digits, as in C#:
    /// <c>D</c>, <c>X</c>, and <c>B</c> for integers, <c>F</c> and <c>E</c> for floating point.
    /// </summary>
    (char Kind, int Precision)? BindFormatSpecifier(Location location, string format, VplType type)
    {
        var target = type.NonNull;
        if (format.Length is < 1 or > 3 || !char.IsAsciiLetter(format[0]) || !format[1..].All(char.IsAsciiDigit))
        {
            Error(location, $"'{format}' is not a format specifier; write a letter and an optional precision of up to two digits, as in 'F2'");
            return null;
        }
        var kind = format[0];
        var applies = char.ToUpperInvariant(kind) switch
        {
            'D' or 'X' or 'B' => (Holds: target.IsInteger, Types: "integers"),
            'F' or 'E' => (Holds: target.IsFloatingPoint, Types: "'float' and 'double'"),
            _ => (Holds: false, Types: null),
        };
        if (applies.Types is null)
        {
            Error(location, $"unknown format specifier '{kind}'; the specifiers are D, X, and B for integers and F and E for floating point");
            return null;
        }
        if (!applies.Holds)
        {
            Error(location, $"format specifier '{format}' applies to {applies.Types}, not '{type}'");
            return null;
        }
        return (kind, format.Length > 1 ? int.Parse(format[1..]) : -1);
    }

    /// <summary>A value the runtime can format: the value itself if it is a formatted type, otherwise its <c>Printable.toString()</c>.</summary>
    BoundExpression BindPrintable(Location location, BoundExpression value)
    {
        if (Builtins.FormattedTypes.Contains(value.Type))
            return value;

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
