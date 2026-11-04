using Wyl.Compiler.Semantics;

namespace Wyl.Generate;

/// <summary>
/// Constructors that mix types (docs/spec/05-conversions.md#conversions.numeric and
/// 13-expressions.md#expressions.arithmetic.operands): an operator on two types the spec
/// converts to one, an unsuffixed constant that takes its partner's type, casts between integers,
/// floats, and <c>char</c>, and values for a slot that converts what it is given.
/// </summary>
sealed partial class ExpressionGenerator
{
    static readonly string[] MixedOperators = ["+", "-", "*", "&", "|", "^", "/", "%"];

    /// <summary>A <c>char</c> divisor would need its own non-zero guard, so a char operand only takes these.</summary>
    static readonly string[] UndividedOperators = ["+", "-", "*", "&", "|", "^"];

    /// <summary>Float operators whose exactness <see cref="ExactFloat"/> can decide.</summary>
    static readonly string[] FloatOperators = ["+", "-", "*"];

    /// <summary>Constants every float and double holds exactly, so they always take the partner's type.</summary>
    static readonly Int128[] FloatConstants = [0, 1, -1, 2, -2, 3, 4, -4, 8, 16];

    /// <summary>
    /// Whether the binder would read one operand as a constant that takes the other's integer type
    /// (spec expressions.arithmetic.operands), which turns a mixed operation into a same-type one in the narrower type. A constant
    /// beside a <c>double</c> always converts exactly, so it changes no type there.
    /// </summary>
    static bool Adopts(GenExpr left, GenExpr right) =>
        (left.Constant && right.Type.IsInteger) || (right.Constant && left.Type.IsInteger);

    /// <summary>
    /// An expression for a slot of exactly <paramref name="type"/> (an initializer, an assignment, an
    /// argument, or a return), which converts what it holds: the text may have a type that widens to
    /// it, or be a constant it adopts, so only such a slot may hold it. The facts are the slot's.
    /// </summary>
    /// <param name="compound">
    /// The right side of a compound assignment, which is an operand: a <c>char</c> there is an
    /// <c>int</c> first, so only a source whose arithmetic with the variable stays in its type fits.
    /// </param>
    public GenExpr Slot(WylType type, int depth, bool compound = false)
    {
        nodesLeft = options.NodeBudget;
        return SlotValue(type, depth, compound);
    }

    GenExpr SlotValue(WylType type, int depth, bool compound = false)
    {
        var sources = compound
            ? [.. ConversionFacts.SourcesOf(type).Where(source => ConversionFacts.Arithmetic(type, source) == type)]
            : ConversionFacts.SourcesOf(type);
        if (sources.Count > 0 && rng.Chance(20))
            return Widened(Build(rng.Pick(sources), depth), type);
        if (type.IsInteger && rng.Chance(10))
        {
            var value = ConstantOf(type, nonZero: false);
            return new(IntegerFacts.Constant(value), type, new IntegerRange(value, value), Constant: true);
        }
        if ((type == WylType.Float || type == WylType.Double) && rng.Chance(10))
        {
            var value = rng.Pick(FloatConstants);
            return new(IntegerFacts.Constant(value), type, null, new ExactFloat(value, 0), Constant: true);
        }
        return Build(type, depth);
    }

    /// <summary>A value's facts once an implicit conversion has made it a <paramref name="type"/>.</summary>
    static GenExpr Widened(GenExpr value, WylType type) => type.IsInteger
        ? new(value.Text, type, value.Range ?? FullRange(value.Type))
        : new(value.Text, type, null, value.Type.IsInteger ? ConversionFacts.Exact(value.Range, type) : value.Exact);

    /// <summary>An unsuffixed constant of <paramref name="type"/>: an <c>int</c> literal, so its value must fit both.</summary>
    Int128 ConstantOf(WylType type, bool nonZero)
    {
        var both = IntegerFacts.Literals(type).Where(IntegerFacts.Full(WylType.Int).Contains).Where(value => !nonZero || value != 0).ToList();
        return rng.Pick(both);
    }

    /// <summary>Arithmetic whose operands the spec converts first: a widening, two integers' meet, or a char's code point.</summary>
    GenExpr? MixedInteger(WylType type, int depth)
    {
        var pairs = ConversionFacts.ArithmeticPairs(type);
        if (pairs.Count == 0)
            return null;
        var (leftType, rightType) = rng.Pick(pairs);
        var op = rng.Pick(rightType == WylType.Char ? UndividedOperators : MixedOperators);
        bool divides = op is "/" or "%";
        var left = Build(leftType, depth - 1);
        var right = divides ? NonZero(rightType, depth - 1) : Build(rightType, depth - 1);
        if (Adopts(left, right) || left.Range is not { } a || right.Range is not { } b)
            return null;
        if (divides && (b.Contains(0) || (a.Contains(IntegerFacts.Full(type).Min) && b.Contains(-1))))
            return null;
        if (IntegerFacts.Apply(op, type, a, b) is not { } range || !IntegerFacts.Fits(type, range))
            return null;
        return new($"({left.Text} {op} {right.Text})", type, range);
    }

    /// <summary>An unsuffixed constant beside a narrower or unsigned integer takes that integer's type: <c>light - 1</c> is a byte.</summary>
    GenExpr? AdoptInteger(WylType type, int depth)
    {
        if (type == WylType.Int)
            return null;
        var value = Build(type, depth - 1);
        if (value.Range is not { } a)
            return null;
        var op = rng.Pick(MixedOperators);
        bool divides = op is "/" or "%";
        var constant = ConstantOf(type, nonZero: divides);
        var k = new IntegerRange(constant, constant);
        // The constant is the divisor, so the guard never depends on a computed value being non-zero.
        bool constantFirst = !divides && rng.NextBool();
        var (left, right) = constantFirst ? (k, a) : (a, k);
        if (divides && left.Contains(IntegerFacts.Full(type).Min) && right.Contains(-1))
            return null;
        if (IntegerFacts.Apply(op, type, left, right) is not { } range || !IntegerFacts.Fits(type, range))
            return null;
        var text = IntegerFacts.Constant(constant);
        return new(constantFirst ? $"({text} {op} {value.Text})" : $"({value.Text} {op} {text})", type, range);
    }

    /// <summary>Toward zero and saturated (spec conversions.casts); the range is one value when the float's is known.</summary>
    GenExpr IntegerFromFloat(WylType type, int depth)
    {
        var value = Build(rng.NextBool() ? WylType.Float : WylType.Double, depth - 1);
        var range = value.Exact is { } exact
            ? new IntegerRange(ConversionFacts.Truncate(exact, type), ConversionFacts.Truncate(exact, type))
            : IntegerFacts.Full(type);
        return new($"(({type.Name})({value.Text}))", type, range);
    }

    /// <summary>The code point, wrapped below 32 bits like any integer narrowing.</summary>
    GenExpr IntegerFromChar(WylType type, int depth)
    {
        var value = Build(WylType.Char, depth - 1);
        var source = value.Range ?? ConversionFacts.CodePoints;
        return new($"(({type.Name})({value.Text}))", type, IntegerFacts.Fits(type, source) ? source : IntegerFacts.Full(type));
    }

    /// <summary>Rounds to nearest when the value doesn't fit (spec conversions.casts), so only a known, fitting value stays exact.</summary>
    GenExpr FloatFromInteger(WylType type, int depth)
    {
        var value = Build(rng.Pick(IntegerFacts.Types), depth - 1);
        return new($"(({type.Name})({value.Text}))", type, null, ConversionFacts.Exact(value.Range, type));
    }

    /// <summary>The cast panics on a value that is not a scalar value, so only a range of them is cast.</summary>
    GenExpr? CharFromInteger(int depth)
    {
        var value = Build(rng.Pick(IntegerFacts.Types), depth - 1);
        if (value.Range is not { } range || !ConversionFacts.AreScalars(range))
            return null;
        return new($"((char)({value.Text}))", WylType.Char, range);
    }

    /// <summary>A float operator with a converted operand: an integer of at most 16 bits, or a float beside a double.</summary>
    GenExpr? MixedFloat(WylType type, int depth)
    {
        var converted = Build(rng.Pick(ConversionFacts.SourcesOf(type)), depth - 1);
        var same = Build(type, depth - 1);
        var exactConverted = converted.Type.IsInteger ? ConversionFacts.Exact(converted.Range, type) : converted.Exact;
        if (exactConverted is not { } x || same.Exact is not { } y)
            return null;
        return FloatOperation(type, (converted.Text, x), (same.Text, y));
    }

    /// <summary>An integer constant beside a float takes its type: <c>scale * 2</c> (spec conversions.constants).</summary>
    GenExpr? AdoptFloat(WylType type, int depth)
    {
        var value = Build(type, depth - 1);
        if (value.Exact is not { } y)
            return null;
        var constant = rng.Pick(FloatConstants);
        return FloatOperation(type, (IntegerFacts.Constant(constant), new ExactFloat(constant, 0)), (value.Text, y));
    }

    /// <summary>Either order, emitted only when the exact result fits, like every float operation.</summary>
    GenExpr? FloatOperation(WylType type, (string Text, ExactFloat Value) one, (string Text, ExactFloat Value) other)
    {
        var (left, right) = rng.NextBool() ? (one, other) : (other, one);
        var op = rng.Pick(FloatOperators);
        var result = op switch
        {
            "+" => left.Value.Add(right.Value),
            "-" => left.Value.Subtract(right.Value),
            _ => left.Value.Multiply(right.Value),
        };
        if (!result.Fits(type))
            return null;
        return new($"({left.Text} {op} {right.Text})", type, null, result);
    }

    /// <summary>Two numbers of different types, compared in the type the spec converts them to.</summary>
    GenExpr? CompareMixed(int depth)
    {
        var (leftType, rightType) = rng.Pick(ConversionFacts.ComparisonPairs);
        var left = Build(leftType, depth - 1);
        var right = Build(rightType, depth - 1);
        if (Adopts(left, right))
            return null;
        string[] operators = ["<", "<=", ">", ">=", "==", "!="];
        return new($"({left.Text} {rng.Pick(operators)} {right.Text})", WylType.Bool);
    }
}
