using System.Globalization;
using Wyl.Compiler.Semantics;

namespace Wyl.Generate;

/// <summary>A helper an expression may call: declared earlier, so the call graph is acyclic.</summary>
sealed record Helper(string Name, WylType Return, IReadOnlyList<WylType> Parameters);

/// <summary>
/// Gives an expression of exactly one type by trying that type's constructors in a seeded order.
/// Every constructor either yields the requested type or is rejected before it is written, and
/// <see cref="Ground"/> is total, so generation cannot fail and emits no text the binder rejects.
/// </summary>
sealed class ExpressionGenerator(
    Rng rng,
    GenerateOptions options,
    Scope scope,
    IReadOnlyList<Helper> helpers,
    List<string> shapes,
    List<WylType> types)
{
    int nodesLeft = options.NodeBudget;

    static readonly string[] IntegerConstructors =
    [
        "literal", "local", "add", "subtract", "multiply", "and", "or", "xor",
        "divide", "remainder", "shift-left", "shift-right", "negate", "complement",
        "cast", "call", "conditional",
    ];

    /// <summary>Unary minus is an error on unsigned types (docs/spec/02-types.md §2.4).</summary>
    static readonly string[] UnsignedIntegerConstructors = [.. IntegerConstructors.Where(name => name != "negate")];

    static readonly string[] BoolConstructors =
    [
        "literal", "local", "not", "and", "or",
        "compare-integer", "compare-char", "compare-float", "compare-bool", "compare-string",
        "call", "conditional",
    ];

    static readonly string[] CharConstructors = ["literal", "local", "call", "conditional"];

    static readonly string[] FloatConstructors =
    [
        "literal", "local", "add", "subtract", "multiply", "divide", "negate",
        "cast", "call", "conditional",
    ];

    static readonly string[] StringConstructors = ["literal", "local", "interpolation", "call", "conditional"];

    static readonly string[] CharLiterals =
        ["'a'", "'b'", "'Z'", "'0'", "' '", "'~'", "'\\\\'", "'\\''", "'\\u{e9}'"];

    static readonly string[] StringLiterals = ["\"hello\"", "\"a\"", "\"xyz\"", "\"\"", "\"Wylow\"", "\"test\""];

    static readonly WylType[] Interpolated =
        [WylType.Int, WylType.Long, WylType.Bool, WylType.Char, WylType.Float, WylType.Double];

    public static IReadOnlyList<string> ConstructorsFor(WylType type)
    {
        if (type.IsInteger)
            return type.IsSigned ? IntegerConstructors : UnsignedIntegerConstructors;
        if (type == WylType.Bool)
            return BoolConstructors;
        if (type == WylType.Char)
            return CharConstructors;
        if (type == WylType.Float || type == WylType.Double)
            return FloatConstructors;
        if (type == WylType.String)
            return StringConstructors;
        throw new ArgumentException($"stage 1 has no constructors for '{type.Name}'");
    }

    /// <summary>An expression of <paramref name="type"/>; each top-level call gets a fresh node budget.</summary>
    public GenExpr Generate(WylType type, int depth)
    {
        nodesLeft = options.NodeBudget;
        return Build(type, depth);
    }

    /// <summary>A non-zero expression, for a divisor or a compound division.</summary>
    public GenExpr GenerateNonZero(WylType type, int depth)
    {
        nodesLeft = options.NodeBudget;
        return NonZero(type, depth);
    }

    /// <summary>One constructor, or null when it cannot make this type (the self-test forces these).</summary>
    public GenExpr? TryConstruct(string name, WylType type, int depth)
    {
        if (!Applicable(name, type) || nodesLeft <= 0)
            return null;
        nodesLeft--;
        if (name == "conditional")
            return Conditional(type, depth);
        if (name == "call")
            return Call(type, depth);
        if (type.IsInteger)
            return Integer(name, type, depth);
        if (type == WylType.Bool)
            return Boolean(name, type, depth);
        if (type == WylType.Char)
            return Character(name, type);
        if (type == WylType.Float || type == WylType.Double)
            return Floating(name, type, depth);
        if (type == WylType.String)
            return Text(name, type, depth);
        throw new ArgumentException($"stage 1 has no constructors for '{type.Name}'");
    }

    GenExpr Build(WylType type, int depth)
    {
        if (depth <= 0 || nodesLeft <= 0)
            return Ground(type);
        foreach (var name in rng.Permute(ConstructorsFor(type)))
        {
            int saved = nodesLeft;
            if (TryConstruct(name, type, depth) is { } expression)
            {
                shapes.Add(name);
                types.Add(expression.Type);
                return expression;
            }
            nodesLeft = saved;
        }
        return Ground(type);
    }

    bool Applicable(string name, WylType type)
    {
        if (!ConstructorsFor(type).Contains(name))
            return false;
        return name switch
        {
            "local" => scope.OfType(type).Count > 0,
            "call" => helpers.Any(helper => helper.Return == type),
            _ => true,
        };
    }

    /// <summary>Totality: a literal, or a local of exactly this type, for every stage-1 type.</summary>
    GenExpr Ground(WylType type)
    {
        var locals = scope.OfType(type);
        if (locals.Count > 0 && rng.Chance(50))
        {
            var local = rng.Pick(locals);
            return new(local.Name, type, type.IsInteger ? local.Range ?? IntegerFacts.Full(type) : null, local.Exact);
        }
        if (type.IsInteger)
        {
            // A curated edge, not a uniform value: the fallback is also where an operation's
            // operands come from at the depth limit, and an edge is likelier to keep it in range.
            var value = rng.Pick(IntegerFacts.Literals(type));
            return new(IntegerFacts.Literal(type, value), type, new IntegerRange(value, value));
        }
        if (type == WylType.Bool)
            return new(rng.NextBool() ? "true" : "false", type);
        if (type == WylType.Char)
            return new(rng.Pick(CharLiterals), type);
        if (type == WylType.Float || type == WylType.Double)
        {
            var literal = rng.Pick(FloatFacts.Literals(type));
            return new(literal.Text, type, null, literal.Value);
        }
        if (type == WylType.String)
            return new(rng.Pick(StringLiterals), type);
        throw new ArgumentException($"stage 1 has no ground value for '{type.Name}'");
    }

    GenExpr? Integer(string name, WylType type, int depth)
    {
        switch (name)
        {
            case "literal":
            {
                var value = rng.Chance(25) ? IntegerFacts.Random(type, rng) : rng.Pick(IntegerFacts.Literals(type));
                return new(IntegerFacts.Literal(type, value), type, new IntegerRange(value, value));
            }
            case "local":
            {
                var local = rng.Pick(scope.OfType(type));
                return new(local.Name, type, local.Range ?? IntegerFacts.Full(type));
            }
            case "add":
                return Arithmetic(type, "+", depth);
            case "subtract":
                return Arithmetic(type, "-", depth);
            case "multiply":
                return Arithmetic(type, "*", depth);
            case "and":
                return Bitwise(type, "&", depth);
            case "or":
                return Bitwise(type, "|", depth);
            case "xor":
                return Bitwise(type, "^", depth);
            case "divide":
                return Division(type, remainder: false, depth);
            case "remainder":
                return Division(type, remainder: true, depth);
            case "shift-left":
                return Shift(type, left: true, depth);
            case "shift-right":
                return Shift(type, left: false, depth);
            case "negate":
                return Negation(type, depth);
            case "complement":
                return Complement(type, depth);
            case "cast":
                return IntegerCast(type, depth);
            default:
                return null;
        }
    }

    GenExpr? Arithmetic(WylType type, string op, int depth)
    {
        var left = Build(type, depth - 1);
        var right = Build(type, depth - 1);
        if (left.Range is not { } a || right.Range is not { } b)
            return null;
        if (IntegerFacts.Apply(op, type, a, b) is not { } range || !IntegerFacts.Fits(type, range))
            return null;
        return new($"({left.Text} {op} {right.Text})", type, range);
    }

    GenExpr? Bitwise(WylType type, string op, int depth)
    {
        var left = Build(type, depth - 1);
        var right = Build(type, depth - 1);
        return new($"({left.Text} {op} {right.Text})", type, IntegerFacts.Full(type));
    }

    GenExpr? Division(WylType type, bool remainder, int depth)
    {
        var numerator = Build(type, depth - 1);
        var divisor = NonZero(type, depth - 1);
        if (numerator.Range is not { } a || divisor.Range is not { } b)
            return null;
        // MIN / -1 panics in every build (docs/spec/02-types.md §2.4).
        if (a.Contains(IntegerFacts.Full(type).Min) && b.Contains(-1))
            return null;
        var result = remainder ? IntegerFacts.Remainder(a, b) : IntegerFacts.Divide(a, b);
        if (result is not { } range || !IntegerFacts.Fits(type, range))
            return null;
        return new($"({numerator.Text} {(remainder ? "%" : "/")} {divisor.Text})", type, range);
    }

    GenExpr NonZero(WylType type, int depth)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var value = Build(type, depth);
            if (value.Range is { } range && !range.Contains(0))
                return value;
        }
        var fallback = rng.Pick([.. IntegerFacts.Literals(type).Where(value => value != 0)]);
        return new(IntegerFacts.Literal(type, fallback), type, new IntegerRange(fallback, fallback));
    }

    GenExpr? Shift(WylType type, bool left, int depth)
    {
        var value = Build(type, depth - 1);
        if (value.Range is not { } a)
            return null;
        var count = rng.Next(type.ShiftWidth);
        var result = left ? IntegerFacts.ShiftLeft(a, count) : IntegerFacts.ShiftRight(a, count);
        if (result is not { } range || !IntegerFacts.Fits(type, range))
            return null;
        var op = left ? "<<" : ">>";
        return new($"({value.Text} {op} {count.ToString(CultureInfo.InvariantCulture)})", type, range);
    }

    GenExpr? Negation(WylType type, int depth)
    {
        if (!type.IsSigned)
            return null;
        var value = Build(type, depth - 1);
        if (value.Range is not { } a)
            return null;
        if (IntegerFacts.Negate(a) is not { } range || !IntegerFacts.Fits(type, range))
            return null;
        return new($"(-({value.Text}))", type, range);
    }

    GenExpr? Complement(WylType type, int depth)
    {
        var value = Build(type, depth - 1);
        if (value.Range is not { } a)
            return null;
        if (IntegerFacts.Not(type, a) is not { } range || !IntegerFacts.Fits(type, range))
            return null;
        return new($"((~{value.Text}))", type, range);
    }

    GenExpr? IntegerCast(WylType type, int depth)
    {
        var source = rng.Pick([.. IntegerFacts.Types.Where(other => other != type)]);
        var value = Build(source, depth - 1);
        if (value.Range is not { } a)
            return null;
        // Narrowing wraps and signed/unsigned reinterprets bits, so a value outside the target's
        // range is still defined; its range becomes the target's whole range.
        var range = IntegerFacts.Fits(type, a) ? a : IntegerFacts.Full(type);
        return new($"(({type.Name})({value.Text}))", type, range);
    }

    GenExpr? Boolean(string name, WylType type, int depth)
    {
        switch (name)
        {
            case "literal":
                return new(rng.NextBool() ? "true" : "false", type);
            case "local":
                return new(rng.Pick(scope.OfType(type)).Name, type);
            case "not":
                return new($"(!({Build(type, depth - 1).Text}))", type);
            case "and":
            {
                var left = Build(type, depth - 1);
                var right = Build(type, depth - 1);
                return new($"({left.Text} && {right.Text})", type);
            }
            case "or":
            {
                var left = Build(type, depth - 1);
                var right = Build(type, depth - 1);
                return new($"({left.Text} || {right.Text})", type);
            }
            case "compare-integer":
                return Compare(rng.Pick(IntegerFacts.Types), depth, ordering: true);
            case "compare-char":
                return Compare(WylType.Char, depth, ordering: true);
            case "compare-float":
                return Compare(rng.NextBool() ? WylType.Float : WylType.Double, depth, ordering: true);
            case "compare-bool":
                return Compare(WylType.Bool, depth, ordering: false);
            case "compare-string":
                return Compare(WylType.String, depth, ordering: false);
            default:
                return null;
        }
    }

    GenExpr Compare(WylType type, int depth, bool ordering)
    {
        string[] operators = ordering ? ["<", "<=", ">", ">=", "==", "!="] : ["==", "!="];
        var op = rng.Pick(operators);
        var left = Build(type, depth - 1);
        var right = Build(type, depth - 1);
        return new($"({left.Text} {op} {right.Text})", WylType.Bool);
    }

    GenExpr? Character(string name, WylType type) => name switch
    {
        "literal" => new(rng.Pick(CharLiterals), type),
        "local" => new(rng.Pick(scope.OfType(type)).Name, type),
        _ => null,
    };

    GenExpr? Floating(string name, WylType type, int depth)
    {
        switch (name)
        {
            case "literal":
            {
                var literal = rng.Pick(FloatFacts.Literals(type));
                return new(literal.Text, type, null, literal.Value);
            }
            case "local":
            {
                var local = rng.Pick(scope.OfType(type));
                return new(local.Name, type, null, local.Exact);
            }
            case "add":
                return FloatArithmetic(type, "+", depth);
            case "subtract":
                return FloatArithmetic(type, "-", depth);
            case "multiply":
                return FloatArithmetic(type, "*", depth);
            case "divide":
                return FloatDivision(type, depth);
            case "negate":
            {
                var value = Build(type, depth - 1);
                var exact = value.Exact is { } number ? new ExactFloat(-number.Mantissa, number.Exponent) : (ExactFloat?)null;
                return new($"(-({value.Text}))", type, null, exact);
            }
            case "cast":
                return FloatCast(type, depth);
            default:
                return null;
        }
    }

    GenExpr? FloatArithmetic(WylType type, string op, int depth)
    {
        var left = Build(type, depth - 1);
        var right = Build(type, depth - 1);
        if (left.Exact is not { } a || right.Exact is not { } b)
            return null;
        var result = op switch
        {
            "+" => a.Add(b),
            "-" => a.Subtract(b),
            _ => a.Multiply(b),
        };
        if (!result.Fits(type))
            return null;
        return new($"({left.Text} {op} {right.Text})", type, null, result);
    }

    GenExpr? FloatDivision(WylType type, int depth)
    {
        var left = Build(type, depth - 1);
        if (left.Exact is not { } a)
            return null;
        var (text, shift) = rng.Pick(FloatFacts.Divisors);
        var result = a.DivideByPowerOfTwo(shift);
        if (!result.Fits(type))
            return null;
        var divisor = text + (type == WylType.Double ? "d" : "");
        return new($"({left.Text} / {divisor})", type, null, result);
    }

    GenExpr FloatCast(WylType type, int depth)
    {
        var source = type == WylType.Float ? WylType.Double : WylType.Float;
        var value = Build(source, depth - 1);
        var exact = value.Exact is { } number && number.Fits(type) ? number : (ExactFloat?)null;
        return new($"(({type.Name})({value.Text}))", type, null, exact);
    }

    GenExpr? Text(string name, WylType type, int depth)
    {
        switch (name)
        {
            case "literal":
                return new(rng.Pick(StringLiterals), type);
            case "local":
                return new(rng.Pick(scope.OfType(type)).Name, type);
            case "interpolation":
            {
                // Only a ground value: any constructor could embed a string literal, and a '"' in a
                // hole ends the interpolated string.
                var holes = new List<string>();
                for (int i = 0, count = 1 + rng.Next(2); i < count; i++)
                    holes.Add(Ground(rng.Pick(Interpolated)).Text);
                return new($"$\"value {string.Join(" ", holes)}\"", type);
            }
            default:
                return null;
        }
    }

    GenExpr Conditional(WylType type, int depth)
    {
        var condition = Build(WylType.Bool, depth - 1);
        var whenTrue = Build(type, depth - 1);
        var whenFalse = Build(type, depth - 1);
        IntegerRange? range = null;
        if (type.IsInteger && whenTrue.Range is { } a && whenFalse.Range is { } b)
            range = new IntegerRange(a.Min < b.Min ? a.Min : b.Min, a.Max > b.Max ? a.Max : b.Max);
        return new($"({condition.Text} ? {whenTrue.Text} : {whenFalse.Text})", type, range);
    }

    GenExpr? Call(WylType type, int depth)
    {
        var candidates = helpers.Where(helper => helper.Return == type).ToList();
        if (candidates.Count == 0)
            return null;
        var helper = rng.Pick(candidates);
        var arguments = helper.Parameters.Select(parameter => Build(parameter, depth - 1).Text).ToList();
        return new($"{helper.Name}({string.Join(", ", arguments)})", type, type.IsInteger ? IntegerFacts.Full(type) : null);
    }
}
