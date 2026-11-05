using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;
using Wyl.Compiler;
using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;
using Wyl.Harness;

namespace Wyl.Generate;

/// <summary>
/// The generator's own checks, run under <c>--self-test</c> and before every generation run: a
/// generator that silently stopped making one constructor, one type, or a deterministic program
/// must say so before it fuzzes, not keep passing and finding nothing.
/// </summary>
static class GeneratorSelfTests
{
    /// <summary>Text that changes what the oracles compare, which a generated program must never contain.</summary>
    static readonly string[] Forbidden =
        ["unsafe", "// mode: optimized", "collectCycles", "cycleStats", "panic("];

    public static IReadOnlyList<(string Name, string? Failure)> Run(GenerateOptions options)
    {
        try
        {
            return
            [
                ("determinism", Determinism(options)),
                ("forbidden-text", ForbiddenText(options)),
                ("integer-facts", IntegerFactsSound()),
                ("conversion-facts", ConversionFactsSound()),
                ("coverage", Coverage(options)),
                ("bound-conversions", BoundConversions(options)),
                ("budgets", Budgets(options)),
            ];
        }
        catch (Exception exception)
        {
            return [("self-test", exception.ToString())];
        }
    }

    static string? Determinism(GenerateOptions options)
    {
        var generator = new ProgramGenerator(options);
        for (int seed = 1; seed <= 8; seed++)
        {
            for (int index = 0; index < 4; index++)
            {
                if (generator.Generate(seed, index).Text != generator.Generate(seed, index).Text)
                    return $"seed {seed} program {index} differs between two generations";
            }
        }
        return null;
    }

    static string? ForbiddenText(GenerateOptions options)
    {
        var generator = new ProgramGenerator(options);
        for (int index = 0; index < 64; index++)
        {
            var text = generator.Generate(1, index).Text;
            foreach (var forbidden in Forbidden)
            {
                if (text.Contains(forbidden, StringComparison.Ordinal))
                    return $"program {index} contains '{forbidden}'";
            }
        }
        return null;
    }

    /// <summary>
    /// Every constructor of every stage-1 type must be able to make exactly that type, and the random
    /// corpus must reach every type; forcing is what makes a degenerate generator visible.
    /// </summary>
    static string? Coverage(GenerateOptions options)
    {
        var failures = new List<string>();
        foreach (var type in GenerateOptions.Types)
        {
            foreach (var constructor in ExpressionGenerator.ConstructorsFor(type))
            {
                if (!Force(options, type, constructor))
                    failures.Add($"{type.Name}/{constructor}");
            }
        }
        if (failures.Count > 0)
            return "made no expression: " + string.Join(", ", failures);

        var generator = new ProgramGenerator(options);
        var types = new HashSet<WylType>();
        for (int index = 0; index < 64; index++)
            types.UnionWith(generator.Generate(7, index).Types);
        var missing = GenerateOptions.Types.Where(type => !types.Contains(type)).Select(type => type.Name).ToList();
        if (missing.Count > 0)
            return "the corpus never made " + string.Join(", ", missing);
        return Parsed(generator, 7, 64, node => node is InterpolatedStringExpr { Values.Count: > 0 })
            ? null
            : "the corpus never made an interpolated string with a hole";
    }

    /// <summary>
    /// Whether the parser finds a node in the corpus: a constructor is checked by what the compiler
    /// reads, not by the text the generator meant, which a quoting slip silently changes.
    /// </summary>
    static bool Parsed(ProgramGenerator generator, int seed, int programs, Func<Node, bool> found)
    {
        for (int index = 0; index < programs; index++)
        {
            var source = new SourceText("<generate>", generator.Generate(seed, index).Text);
            var unit = new Parser(source, new DiagnosticBag()).ParseCompilationUnit();
            if (AstIndex.Of(unit).Of(SiteKind.Expression).Any(site => found(site.Node)))
                return true;
        }
        return false;
    }

    static bool Force(GenerateOptions options, WylType type, string constructor)
    {
        // Shallow children (depth 1 makes them ground values) and a few draws: the question is
        // whether the constructor can make this type at all, not whether one draw happened to fit.
        for (int attempt = 0; attempt < 32; attempt++)
        {
            var scope = new Scope();
            if (constructor == "local")
                scope.Declare("probe", type);
            IReadOnlyList<Helper> helpers = constructor == "call" ? [new Helper("probe", type, [])] : [];
            var expressions = new ExpressionGenerator(new Rng((ulong)(attempt + 1)), options, scope, helpers, new PrintBudget(options.PrintLines), topLevel: true, [], []);
            if (expressions.TryConstruct(constructor, type, depth: 1) is { } expression && expression.Type == type)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Every operation's range must contain the result when it fits the type and must not fit when
    /// it does not: a guard is only sound if the fact it reads is.
    /// </summary>
    static string? IntegerFactsSound()
    {
        foreach (var type in IntegerFacts.Types)
        {
            var full = IntegerFacts.Full(type);
            var values = Sample(type);
            foreach (var a in values)
            {
                var single = new IntegerRange(a, a);
                string? failure =
                    Expect(type, "negate", IntegerFacts.Negate(single), -(BigInteger)a)
                    ?? Expect(type, "complement", IntegerFacts.Not(type, single), type.IsSigned ? -(BigInteger)a - 1 : (BigInteger)full.Max - a);
                if (failure is not null)
                    return failure;
                for (int count = 0; count < type.ShiftWidth; count++)
                {
                    failure =
                        Expect(type, "shift-left", IntegerFacts.ShiftLeft(single, count), (BigInteger)a << count)
                        ?? Expect(type, "shift-right", IntegerFacts.ShiftRight(single, count), (BigInteger)a >> count);
                    if (failure is not null)
                        return failure;
                }
                foreach (var b in values)
                {
                    // A divisor is never zero: the constructor asks for a non-zero one.
                    if (b == 0)
                        continue;
                    var pair = new IntegerRange(b, b);
                    failure =
                        Expect(type, "add", IntegerFacts.Add(single, pair), (BigInteger)a + b)
                        ?? Expect(type, "subtract", IntegerFacts.Subtract(single, pair), (BigInteger)a - b)
                        ?? Expect(type, "multiply", IntegerFacts.Multiply(single, pair), (BigInteger)a * b)
                        ?? Expect(type, "divide", IntegerFacts.Divide(single, pair), (BigInteger)a / b)
                        ?? Expect(type, "remainder", IntegerFacts.Remainder(single, pair), (BigInteger)a % b);
                    if (failure is not null)
                        return failure;
                }
            }
        }
        return null;
    }

    static string? Expect(WylType type, string operation, IntegerRange? range, BigInteger value)
    {
        var full = IntegerFacts.Full(type);
        bool fits = value >= (BigInteger)full.Min && value <= (BigInteger)full.Max;
        if (fits)
            return range is { } result && result.Contains((Int128)value)
                ? null
                : $"{type.Name}/{operation}: {value} is not in {range?.ToString() ?? "null"}";
        return range is { } overflowing && IntegerFacts.Fits(type, overflowing)
            ? $"{type.Name}/{operation}: {value} overflows, but {overflowing} fits"
            : null;
    }

    static IReadOnlyList<Int128> Sample(WylType type)
    {
        var full = IntegerFacts.Full(type);
        var span = full.Max - full.Min;
        if (span <= 255)
            return [.. Enumerable.Range(0, (int)(span + 1)).Select(offset => full.Min + offset)];
        List<Int128> values = [.. IntegerFacts.Literals(type), full.Min, full.Min + 1, full.Max, full.Max - 1, -1, 0, 1, 2, -2];
        for (int i = 1; i < 12; i++)
            values.Add(full.Min + span / 13 * i);
        return [.. values.Where(full.Contains).Distinct()];
    }

    /// <summary>
    /// The spec's conversion table agrees with the compiler's, and the facts the conversion
    /// constructors read are sound: a disagreement in the table is a spec question to settle, not a
    /// program to generate.
    /// </summary>
    static string? ConversionFactsSound()
    {
        foreach (var from in ConversionFacts.Numeric)
        {
            foreach (var to in ConversionFacts.Numeric)
            {
                if (from != to && ConversionFacts.Widens(from, to) != Conversions.IsImplicit(from, to))
                    return $"the spec's table says '{from.Name}' {(ConversionFacts.Widens(from, to) ? "converts" : "does not convert")} implicitly to '{to.Name}', and the compiler disagrees";
            }
        }
        Int128[] integers = [0, 1, -1, 1 << 24, (1 << 24) + 1, -(1 << 24) - 1, 1L << 53, (1L << 53) + 1, long.MaxValue, ulong.MaxValue];
        foreach (var value in integers)
        {
            foreach (var type in new[] { WylType.Float, WylType.Double })
            {
                bool exact = ConversionFacts.Exact(new IntegerRange(value, value), type) is not null;
                if (exact != Conversions.IsExactlyRepresentable(value, type))
                    return $"{value} as a {type.Name}: exact is {exact}";
            }
        }
        if (ConversionFacts.Exact(new IntegerRange(1, 2), WylType.Double) is not null)
            return "a range of two values has one exact value";
        foreach (var (_, value) in FloatFacts.Literals(WylType.Double))
        {
            foreach (var type in IntegerFacts.Types)
            {
                foreach (var scaled in new[] { value, value.Multiply(new ExactFloat(1, 70)), value.DivideByPowerOfTwo(3) })
                {
                    var number = (double)scaled.Mantissa * Math.Pow(2, scaled.Exponent);
                    var full = IntegerFacts.Full(type);
                    var truncated = Math.Truncate(number);
                    Int128 expected = truncated <= (double)full.Min ? full.Min : truncated >= (double)full.Max ? full.Max : (Int128)truncated;
                    if (ConversionFacts.Truncate(scaled, type) != expected)
                        return $"({type.Name}){number} is {expected}, not {ConversionFacts.Truncate(scaled, type)}";
                }
            }
        }
        (Int128 Min, Int128 Max, bool Scalars)[] ranges =
            [(0, 0xD7FF, true), (0xD7FF, 0xD800, false), (0xDFFF, 0xE000, false), (0xE000, 0x10FFFF, true), (0x10FFFF, 0x110000, false), (-1, 0, false)];
        foreach (var (min, max, scalars) in ranges)
        {
            if (ConversionFacts.AreScalars(new IntegerRange(min, max)) != scalars)
                return $"{min}..{max} are {(scalars ? "" : "not ")}all scalar values";
        }
        return null;
    }

    /// <summary>
    /// What the binder made of the conversions the corpus meant: a constant whose spelling gained a
    /// suffix, or an operand that is already the other's type, still binds, but tests a same-type
    /// operation instead. Each construct must be found as the compiler reads it: an implicit
    /// conversion (which the binder gives its operand's location), an adopted constant (a literal
    /// spelled without a suffix whose type is not an <c>int</c>), or a cast.
    /// </summary>
    static string? BoundConversions(GenerateOptions options)
    {
        (string Name, Func<BoundNode, BoundNode?, string, bool> Found)[] constructs =
        [
            ("an integer operand widened", (node, parent, _) => parent is BoundBinary && Implicit(node) is ({ IsInteger: true }, { IsInteger: true })),
            ("two integer operands meeting in a third type", (node, _, _) => node is BoundBinary { Left: var left, Right: var right } && Implicit(left) is ({ IsInteger: true }, _) && Implicit(right) is ({ IsInteger: true }, _)),
            ("a char operand's code point", (node, parent, _) => parent is BoundBinary && Implicit(node) is var (from, to) && from == WylType.Char && to == WylType.Int),
            ("an integer operand widened to a float", (node, parent, _) => parent is BoundBinary && Implicit(node) is ({ IsInteger: true }, { IsFloatingPoint: true })),
            ("a float operand widened to a double", (node, parent, _) => parent is BoundBinary && Implicit(node) is var (from, to) && from == WylType.Float && to == WylType.Double),
            ("an argument widened", (node, parent, _) => parent is BoundCall && Implicit(node) is not null),
            ("an initializer widened", (node, parent, _) => parent is BoundVariableDeclaration && Implicit(node) is not null),
            ("a returned value widened", (node, parent, _) => parent is BoundReturn && Implicit(node) is not null),
            ("an integer constant adopting an operand's type", (node, parent, text) => parent is BoundBinary && Adopted(node, text) is { IsInteger: true } type && type != WylType.Int),
            ("an integer constant adopting a float operand's type", (node, parent, text) => parent is BoundBinary && Adopted(node, text) is { IsFloatingPoint: true }),
            ("an integer constant adopting a slot's type", (node, parent, text) => parent is BoundVariableDeclaration or BoundCall or BoundReturn or BoundAssignment && Adopted(node, text) is { } type && type != WylType.Int),
            ("a float cast to an integer", (node, _, _) => Cast(node) is ({ IsFloatingPoint: true }, { IsInteger: true })),
            ("an integer cast to a float", (node, _, _) => Cast(node) is ({ IsInteger: true }, { IsFloatingPoint: true })),
            ("an integer cast to a char", (node, _, _) => Cast(node) is var (from, to) && from.IsInteger && to == WylType.Char),
            ("a char cast to an integer", (node, _, _) => Cast(node) is var (from, to) && from == WylType.Char && to.IsInteger),
        ];
        var missing = constructs.Select(construct => construct.Name).ToHashSet();
        var generator = new ProgramGenerator(options);
        for (int index = 0; index < 48 && missing.Count > 0; index++)
        {
            var text = generator.Generate(7, index).Text;
            var source = new SourceText("<generate>", text);
            var program = Compilation.Lower(source, new CompileOptions(Nest: true, Optimize: false)).Program;
            if (program is null)
                return $"program {index} did not bind";
            foreach (var function in program.Functions.Where(function => function.Body.Location.Source == source))
            {
                foreach (var (node, parent) in Nodes(function.Body))
                {
                    foreach (var (name, found) in constructs)
                    {
                        if (missing.Contains(name) && found(node, parent, text))
                            missing.Remove(name);
                    }
                }
            }
        }
        return missing.Count == 0 ? null : "the bound corpus has no " + string.Join("; no ", missing);
    }

    static (WylType From, WylType To)? Implicit(BoundNode node) =>
        node is BoundConversion conversion && conversion.Location == conversion.Operand.Location ? (conversion.Operand.Type, conversion.Type) : null;

    static (WylType From, WylType To)? Cast(BoundNode node) =>
        node is BoundConversion conversion && conversion.Location != conversion.Operand.Location ? (conversion.Operand.Type, conversion.Type) : null;

    static readonly Regex UnsuffixedInteger = new(@"^-?[0-9]+$", RegexOptions.CultureInvariant);

    /// <summary>The type an unsuffixed integer literal was given, when it is a literal at all.</summary>
    static WylType? Adopted(BoundNode node, string text) =>
        node is BoundLiteral literal && UnsuffixedInteger.IsMatch(text.Substring(literal.Location.Span.Start, literal.Location.Span.Length)) ? literal.Type : null;

    /// <summary>
    /// Every node of a bound tree with its parent, found through the records' properties, so a new
    /// kind of node needs no case here.
    /// </summary>
    static IEnumerable<(BoundNode Node, BoundNode? Parent)> Nodes(BoundNode root)
    {
        var pending = new Stack<(BoundNode Node, BoundNode? Parent)>();
        pending.Push((root, null));
        while (pending.Count > 0)
        {
            var (node, parent) = pending.Pop();
            yield return (node, parent);
            foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0)
                    continue;
                switch (property.GetValue(node))
                {
                    case BoundNode child:
                        pending.Push((child, node));
                        break;
                    case IEnumerable children and not string:
                        foreach (var child in children.OfType<BoundNode>())
                            pending.Push((child, node));
                        break;
                }
            }
        }
    }

    static string? Budgets(GenerateOptions options)
    {
        var generator = new ProgramGenerator(options);
        for (int index = 0; index < 32; index++)
        {
            var program = generator.Generate(3, index);
            int prints = program.Text.Split("print(").Length - 1;
            if (prints > options.PrintLines)
                return $"program {index} printed {prints} times, over {options.PrintLines}";
            // Loops and calls make a program print more than its text does, which the budget bounds too.
            if (generator.RunPrints > options.PrintLines)
                return $"program {index} prints {generator.RunPrints} times when it runs, over {options.PrintLines}";
            if (!program.Text.Contains("void main()", StringComparison.Ordinal))
                return $"program {index} has no main";
            if (program.Text.Length > 200_000)
                return $"program {index} is {program.Text.Length} characters";
        }
        return null;
    }
}
