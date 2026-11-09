using Wyl.Compiler.Semantics;

namespace Wyl.Generate;

/// <summary>
/// The prints a whole program may make. <see cref="TextLeft"/> bounds the print statements in its
/// text, which main and its helpers share. <see cref="Left"/> bounds what main's run prints: a print
/// inside loops counts once for each time it runs, and a call counts the prints its body makes, so
/// the bound holds while the program runs and not only in its text. <see cref="Dynamic"/> counts
/// what the body being made prints when it is called once.
/// </summary>
sealed class PrintBudget(int lines)
{
    public int TextLeft { get; private set; } = lines;
    public int Left { get; private set; } = lines;

    /// <summary>How many times the statement or expression being made runs, from the loops around it.</summary>
    public int Multiplicity { get; set; } = 1;

    /// <summary>What one run of the body being made prints.</summary>
    public int Dynamic { get; private set; }

    public void ResetDynamic() => Dynamic = 0;

    /// <summary>Charges a print statement's own line, which every body shares.</summary>
    public bool TryText()
    {
        if (TextLeft <= 0)
            return false;
        TextLeft--;
        return true;
    }

    /// <summary>Charges what main's run makes, and fails where the program could not afford it.</summary>
    public bool TryRun(int count)
    {
        if (count > Left)
            return false;
        Left -= count;
        return true;
    }

    public void Ran(int count) => Dynamic += count;
}

/// <summary>
/// Statements: declarations, assignments, prints, ifs, bounded range loops, and helper calls. Every
/// integer operation is guarded by <see cref="IntegerFacts"/>, every loop has a literal bound, and a
/// block is bounded in depth, so a generated program terminates and never panics. Its prints are
/// bounded as it runs, not only in its text: see <see cref="PrintBudget"/>.
/// </summary>
sealed class StatementGenerator(
    Rng rng,
    GenerateOptions options,
    SourceWriter writer,
    PrintBudget budget,
    IReadOnlyList<Helper> helpers)
{
    const int MaxBlockDepth = 3;

    static readonly (string Kind, int Weight)[] Weights =
        [("declare", 30), ("assign", 25), ("print", 20), ("if", 15), ("for", 10), ("call", 10)];

    static readonly string[] CompoundOperators = ["+", "-", "*", "&", "|", "^", "/", "%"];

    int loopDepth;

    /// <summary>Whether this body is main's, whose run the program's budget bounds.</summary>
    bool topLevel;

    public void WriteMain(Scope scope, ExpressionGenerator expressions)
    {
        topLevel = true;
        writer.Open("void main() {");
        for (int i = 0; i < options.Size; i++)
            WriteStatement(scope, expressions, depth: 0);
        // The final sweep: every local main still has is observable one last time.
        foreach (var local in scope.Visible.ToList())
            Print(local.Name);
        writer.Close();
    }

    public void WriteHelper(Helper helper, Scope scope, ExpressionGenerator expressions)
    {
        topLevel = false;
        var parameters = helper.Parameters.Select((type, index) => $"{type.Name} a{index}");
        writer.Open($"{helper.Return.Name} {helper.Name}({string.Join(", ", parameters)}) {{");
        for (int i = 0, count = 1 + rng.Next(3); i < count; i++)
            WriteStatement(scope, expressions, depth: 0);
        writer.Line($"return {expressions.Slot(helper.Return, options.Depth).Text};");
        writer.Close();
    }

    void WriteStatement(Scope scope, ExpressionGenerator expressions, int depth)
    {
        var applicable = new List<(string Kind, int Weight)>();
        foreach (var (kind, weight) in Weights)
        {
            bool applies = kind switch
            {
                "assign" => scope.Visible.Any(local => local.Mutable && GenerateOptions.Types.Contains(local.Type)),
                "print" => budget.TextLeft > 0,
                "if" => depth < MaxBlockDepth,
                "for" => depth < MaxBlockDepth && loopDepth < options.LoopDepth,
                "call" => helpers.Any(Affordable),
                _ => true,
            };
            if (applies)
                applicable.Add((kind, weight));
        }

        int pick = rng.Next(applicable.Sum(pair => pair.Weight));
        var chosen = applicable[^1].Kind;
        foreach (var (kind, weight) in applicable)
        {
            if (pick < weight)
            {
                chosen = kind;
                break;
            }
            pick -= weight;
        }

        switch (chosen)
        {
            case "declare":
                Declare(scope, expressions);
                break;
            case "assign":
                Assign(scope, expressions);
                break;
            case "print":
                PrintExpression(expressions);
                break;
            case "if":
                If(scope, expressions, depth);
                break;
            case "for":
                For(scope, expressions, depth);
                break;
            case "call":
                Call(expressions);
                break;
        }
    }

    void Declare(Scope scope, ExpressionGenerator expressions)
    {
        var type = rng.Pick(GenerateOptions.Types);
        bool mutable = rng.Chance(30);
        var value = expressions.Slot(type, options.Depth);
        var local = scope.Declare(type, mutable, value.Range, value.Exact);
        writer.Line($"{(mutable ? "mut " : "")}{type.Name} {local.Name} = {value.Text};");
        Print(local.Name);
    }

    void Assign(Scope scope, ExpressionGenerator expressions)
    {
        var candidates = scope.Visible.Where(local => local.Mutable && GenerateOptions.Types.Contains(local.Type)).ToList();
        if (candidates.Count == 0)
            return;
        var local = rng.Pick(candidates);
        var type = local.Type;
        if (type.IsInteger && rng.Chance(50))
        {
            var op = rng.Pick(CompoundOperators);
            bool divides = op == "/" || op == "%";
            // `b += 1` stays a byte and `i += s` with a short is an int, so the right side is a slot too.
            var right = divides ? expressions.GenerateNonZero(type, options.Depth - 1) : expressions.Slot(type, options.Depth - 1, compound: true);
            var left = local.Range ?? IntegerFacts.Full(type);
            if (right.Range is { } divisor
                && (!divides || !(left.Contains(IntegerFacts.Full(type).Min) && divisor.Contains(-1)))
                && IntegerFacts.Apply(op, type, left, divisor) is { } range
                && IntegerFacts.Fits(type, range))
            {
                writer.Line($"{local.Name} {op}= {right.Text};");
                local.Range = range;
                scope.MarkWritten(local);
                Print(local.Name);
                return;
            }
        }
        var value = expressions.Slot(type, options.Depth);
        writer.Line($"{local.Name} = {value.Text};");
        local.Range = value.Range;
        local.Exact = value.Exact;
        scope.MarkWritten(local);
        Print(local.Name);
    }

    void PrintExpression(ExpressionGenerator expressions)
    {
        var type = rng.Pick(GenerateOptions.Types);
        Print(expressions.Generate(type, options.Depth).Text);
    }

    void If(Scope scope, ExpressionGenerator expressions, int depth)
    {
        var condition = expressions.Generate(WylType.Bool, options.Depth);
        writer.Open($"if ({condition.Text}) {{");
        scope.Push();
        for (int i = 0, count = 1 + rng.Next(4); i < count; i++)
            WriteStatement(scope, expressions, depth + 1);
        scope.Pop();
        if (rng.Chance(50))
        {
            writer.Continue("else {");
            scope.Push();
            for (int i = 0, count = 1 + rng.Next(4); i < count; i++)
                WriteStatement(scope, expressions, depth + 1);
            scope.Pop();
        }
        writer.Close();
    }

    void For(Scope scope, ExpressionGenerator expressions, int depth)
    {
        int count = 1 + rng.Next(loopDepth == 0 ? 4 : 3);
        var counter = $"i{loopDepth}";
        // A second iteration may see any assignment the body made.
        scope.WidenMutables();
        writer.Open($"for (int {counter} : 0..{count}) {{");
        scope.Push();
        scope.Declare(counter, WylType.Int, new IntegerRange(0, count - 1));
        budget.Multiplicity *= count;
        Print(counter);
        loopDepth++;
        for (int i = 0, statements = 1 + rng.Next(4); i < statements; i++)
            WriteStatement(scope, expressions, depth + 1);
        loopDepth--;
        budget.Multiplicity /= count;
        scope.Pop();
        writer.Close();
    }

    void Call(ExpressionGenerator expressions)
    {
        var affordable = helpers.Where(Affordable).ToList();
        if (affordable.Count == 0)
            return;
        var helper = rng.Pick(affordable);
        Run(Charge(helper));
        var arguments = helper.Parameters.Select(type => expressions.Slot(type, options.Depth - 1).Text);
        Print($"{helper.Name}({string.Join(", ", arguments)})");
    }

    /// <summary>The prints one call makes, each counted as many times as the statement runs.</summary>
    int Charge(Helper helper) => helper.Cost * budget.Multiplicity;

    /// <summary>Whether a call's prints fit what main may make, or what one run of this helper may.</summary>
    bool Affordable(Helper helper) => topLevel
        ? Charge(helper) <= budget.Left
        : budget.Dynamic + Charge(helper) <= options.PrintLines;

    /// <summary>
    /// Charges what the statement prints as it runs: against main's budget, which fails when the
    /// program cannot afford it, or against this helper's own cost, which a caller budgets for.
    /// </summary>
    void Run(int count)
    {
        if (topLevel)
            budget.TryRun(count);
        budget.Ran(count);
    }

    void Print(string text)
    {
        int count = budget.Multiplicity;
        if (topLevel && !budget.TryRun(count))
            return;
        if (!budget.TryText())
            return;
        budget.Ran(count);
        writer.Line($"std::print({text});");
    }
}
