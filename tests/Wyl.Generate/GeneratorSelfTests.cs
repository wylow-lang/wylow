using System.Numerics;
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
        ["unsafe", "@Unchecked", "// mode: optimized", "Memory.collectCycles", "cycleStats", "panic("];

    public static IReadOnlyList<(string Name, string? Failure)> Run(GenerateOptions options)
    {
        try
        {
            return
            [
                ("determinism", Determinism(options)),
                ("forbidden-text", ForbiddenText(options)),
                ("integer-facts", IntegerFactsSound()),
                ("coverage", Coverage(options)),
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
            var expressions = new ExpressionGenerator(new Rng((ulong)(attempt + 1)), options, scope, helpers, [], []);
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

    static string? Budgets(GenerateOptions options)
    {
        var generator = new ProgramGenerator(options);
        for (int index = 0; index < 32; index++)
        {
            var program = generator.Generate(3, index);
            int prints = program.Text.Split("print(").Length - 1;
            if (prints > options.PrintLines)
                return $"program {index} printed {prints} times, over {options.PrintLines}";
            if (!program.Text.Contains("void main()", StringComparison.Ordinal))
                return $"program {index} has no main";
            if (program.Text.Length > 200_000)
                return $"program {index} is {program.Text.Length} characters";
        }
        return null;
    }
}
