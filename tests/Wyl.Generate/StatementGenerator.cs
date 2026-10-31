using Wyl.Compiler.Semantics;

namespace Wyl.Generate;

/// <summary>The prints a whole program may make, shared by main and its helpers.</summary>
sealed class PrintBudget(int lines)
{
    public int Left { get; private set; } = lines;

    public bool TryUse()
    {
        if (Left <= 0)
            return false;
        Left--;
        return true;
    }
}

/// <summary>
/// Statements: declarations, assignments, prints, ifs, bounded range loops, and helper calls. Every
/// integer operation is guarded by <see cref="IntegerFacts"/>, every loop has a literal bound, and a
/// block is bounded in depth, so a generated program terminates and never panics.
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

    public void WriteMain(Scope scope, ExpressionGenerator expressions)
    {
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
                "print" => budget.Left > 0,
                "if" => depth < MaxBlockDepth,
                "for" => depth < MaxBlockDepth && loopDepth < options.LoopDepth,
                "call" => helpers.Count > 0 && budget.Left > 0,
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
        Print(counter);
        loopDepth++;
        for (int i = 0, statements = 1 + rng.Next(4); i < statements; i++)
            WriteStatement(scope, expressions, depth + 1);
        loopDepth--;
        scope.Pop();
        writer.Close();
    }

    void Call(ExpressionGenerator expressions)
    {
        var helper = rng.Pick(helpers);
        var arguments = helper.Parameters.Select(type => expressions.Slot(type, options.Depth - 1).Text);
        Print($"{helper.Name}({string.Join(", ", arguments)})");
    }

    void Print(string text)
    {
        if (!budget.TryUse())
            return;
        writer.Line($"print({text});");
    }
}
