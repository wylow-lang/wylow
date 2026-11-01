namespace Wyl.Compiler.Semantics;

/// <summary>
/// Tracks which values a switch's cases have matched so far, to find cases that can never match and
/// values no case handles. This is the usefulness algorithm from Maranget's "Warnings for pattern
/// matching", extended to produce an example of an unmatched value.
/// </summary>
/// <remarks>
/// A type test on a reference that may fail is opaque: it is treated as matching no value of the
/// constructors it is checked against, so it never hides a later case or makes a switch exhaustive.
/// </remarks>
public sealed class MatchSpace(WylType type)
{
    abstract record Pattern;

    sealed record Wildcard : Pattern
    {
        public static readonly Wildcard Instance = new();
    }

    /// <summary><see cref="Key"/> is a <see cref="VariantSymbol"/>, a struct's <see cref="TypeSymbol"/>, a bool, or a long.</summary>
    sealed record Constructor(object Key, Pattern[] Arguments) : Pattern;

    sealed record Opaque : Pattern
    {
        public static readonly Opaque Instance = new();
    }

    readonly List<Pattern[]> rows = [];

    /// <summary>Adds a case, or <c>default</c> for null, and returns whether it matches anything the earlier ones don't.</summary>
    public bool Add(BoundPattern? pattern)
    {
        Pattern[] row = [pattern is null ? Wildcard.Instance : Simplify(pattern)];
        bool useful = Useful(rows, row, [type]) is not null;
        rows.Add(row);
        return useful;
    }

    /// <summary>An unmatched value written as a pattern, or null if every value is matched.</summary>
    public string? Missing() => Useful(rows, [Wildcard.Instance], [type]) is { } witness ? Format(witness[0]) : null;

    static Pattern Simplify(BoundPattern pattern) => pattern switch
    {
        _ when Patterns.AlwaysMatches(pattern) => Wildcard.Instance,
        BoundTypePattern => Opaque.Instance,
        BoundVariantPattern variant => new Constructor(variant.Variant, [.. variant.Fields.Select(Simplify)]),
        BoundStructPattern { Type.IsNullable: true } => Opaque.Instance,
        BoundStructPattern structPattern => new Constructor(structPattern.Type.Symbol!, [.. structPattern.Fields.Select(Simplify)]),
        BoundConstantPattern { Value.Value: bool b } => new Constructor(b, []),
        BoundConstantPattern { Value.Integer: { } integer } => new Constructor(integer, []),
        BoundConstantPattern constant => new Constructor(constant.Value.Value, []),
        _ => throw new InvalidOperationException($"unexpected pattern {pattern.GetType().Name}"),
    };

    /// <summary>Every constructor of a type with finitely many, or null.</summary>
    static IReadOnlyList<object>? Constructors(WylType type) =>
        type.IsEnum ? type.Symbol!.Variants
        : type.IsStruct ? [type.Symbol!]
        : type == WylType.Bool ? [true, false]
        : null;

    static WylType[] ArgumentTypes(WylType type, object key)
    {
        IReadOnlyList<FieldSymbol> fields = key switch
        {
            VariantSymbol variant => variant.Fields,
            TypeSymbol structSymbol => structSymbol.Fields,
            _ => [],
        };
        var map = TypeMap.For(type);
        return [.. fields.Select(f => map.Apply(f.Type))];
    }

    static Pattern[] Wildcards(int count) => [.. Enumerable.Repeat<Pattern>(Wildcard.Instance, count)];

    /// <summary>
    /// A value matched by <paramref name="candidate"/> and by none of <paramref name="matrix"/>, as
    /// a pattern per column, or null if there is none.
    /// </summary>
    static Pattern[]? Useful(List<Pattern[]> matrix, Pattern[] candidate, WylType[] types)
    {
        if (candidate.Length == 0)
            return matrix.Count == 0 ? [] : null;
        var (head, rest, restTypes) = (candidate[0], candidate[1..], types[1..]);

        switch (head)
        {
            case Constructor constructor:
            {
                var argumentTypes = ArgumentTypes(types[0], constructor.Key);
                var witness = Useful(Specialize(matrix, constructor.Key, argumentTypes.Length),
                    [.. constructor.Arguments, .. rest], [.. argumentTypes, .. restTypes]);
                return witness is null ? null : Rebuild(constructor.Key, argumentTypes.Length, witness);
            }
            case Opaque:
            {
                var witness = Useful(Default(matrix), rest, restTypes);
                return witness is null ? null : [Wildcard.Instance, .. witness];
            }
            default:
            {
                var all = Constructors(types[0]);
                var used = matrix.Select(r => r[0]).OfType<Constructor>().Select(c => c.Key).ToHashSet();
                if (all is not null && all.All(used.Contains))
                {
                    foreach (var key in all)
                    {
                        var argumentTypes = ArgumentTypes(types[0], key);
                        var witness = Useful(Specialize(matrix, key, argumentTypes.Length),
                            [.. Wildcards(argumentTypes.Length), .. rest], [.. argumentTypes, .. restTypes]);
                        if (witness is not null)
                            return Rebuild(key, argumentTypes.Length, witness);
                    }
                    return null;
                }

                var defaultWitness = Useful(Default(matrix), rest, restTypes);
                if (defaultWitness is null)
                    return null;
                Pattern missing = all?.FirstOrDefault(k => !used.Contains(k)) is { } missingKey
                    ? new Constructor(missingKey, Wildcards(ArgumentTypes(types[0], missingKey).Length))
                    : Wildcard.Instance;
                return [missing, .. defaultWitness];
            }
        }
    }

    static Pattern[] Rebuild(object key, int arity, Pattern[] witness) =>
        [new Constructor(key, witness[..arity]), .. witness[arity..]];

    /// <summary>The rows that match <paramref name="key"/>, with its arguments in place of the first column.</summary>
    static List<Pattern[]> Specialize(List<Pattern[]> matrix, object key, int arity)
    {
        var result = new List<Pattern[]>();
        foreach (var row in matrix)
        {
            if (row[0] is Constructor c && c.Key.Equals(key))
                result.Add([.. c.Arguments, .. row[1..]]);
            else if (row[0] is Wildcard)
                result.Add([.. Wildcards(arity), .. row[1..]]);
        }
        return result;
    }

    /// <summary>The rows that match any value in the first column, without that column.</summary>
    static List<Pattern[]> Default(List<Pattern[]> matrix) => [.. matrix.Where(r => r[0] is Wildcard).Select(r => r[1..])];

    static string Format(Pattern pattern) => pattern switch
    {
        Constructor { Key: VariantSymbol variant } c when c.Arguments.Length > 0 =>
            $"{variant.Name}({string.Join(", ", c.Arguments.Select(Format))})",
        Constructor { Key: VariantSymbol variant } => variant.Name,
        // A struct none of whose fields is constrained stands for any value of it.
        Constructor { Key: TypeSymbol } c when c.Arguments.All(a => Format(a) == "_") => "_",
        Constructor { Key: TypeSymbol structSymbol } c => $"{structSymbol.Name}({string.Join(", ", c.Arguments.Select(Format))})",
        Constructor { Key: bool b } => b ? "true" : "false",
        Constructor c => c.Key.ToString()!,
        _ => "_",
    };
}
