using System.Globalization;
using System.Text;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.CodeGen;

public sealed partial class CppEmitter
{
    /// <summary>
    /// What matching a pattern against <paramref name="value"/> takes: C++ conditions that are all
    /// true when it matches, and the value each name is bound to. None of it runs VPL code.
    /// </summary>
    void PatternParts(BoundPattern pattern, string value, List<string> tests, List<(LocalSymbol Local, string Value)> bindings)
    {
        switch (pattern)
        {
            case BoundBindingPattern binding:
                bindings.Add((binding.Local, value));
                break;
            case BoundTypePattern { Type.IsNullableValue: true } test:
                tests.Add($"vpl::has_value({value})");
                if (test.Binding is { } valueBinding)
                    bindings.Add((valueBinding, $"vpl::assume_value({value})"));
                break;
            case BoundTypePattern test:
            {
                bool always = !test.Type.IsNullable && Conversions.IsSubtype(test.Type, test.Target);
                if (!always)
                    tests.Add($"vpl::is<{ObjectType(test.Target)}>({value})");
                if (test.Binding is { } local)
                    bindings.Add((local, always ? value : $"vpl::downcast<{ObjectType(test.Target)}>({value})"));
                break;
            }
            case BoundConstantPattern { Value.Value: bool b }:
                tests.Add(b ? value : $"!{value}");
                break;
            case BoundConstantPattern constant:
                tests.Add($"{value} == {Literal(constant.Value)}");
                break;
            case BoundVariantPattern variant:
                tests.Add($"{value}.tag == {TagValue(variant.Variant)}");
                for (int i = 0; i < variant.Fields.Count; i++)
                    PatternParts(variant.Fields[i], $"{value}.{UnionMember(variant.Variant)}.{FieldName(variant.Variant.Fields[i])}", tests, bindings);
                break;
        }
    }

    static string Conjunction(List<string> tests) => tests.Count == 0 ? "true" : string.Join(" && ", tests);

    /// <summary>
    /// The declaration of a reference to a value being matched. Matching runs no VPL code, so an
    /// object reached through places stays alive; one held only by a temporary is pinned first.
    /// </summary>
    string MatchedValue(BoundExpression value, string name)
    {
        if (PinRoot(value) is not { } root || IsPlace(root))
            return $"auto&& {name} = {Expression(value)};";
        var pin = NewTemp();
        var rootText = Expression(root);
        replacements[root] = pin;
        var valueText = Expression(value);
        replacements.Remove(root);
        return $"auto {pin} = {rootText}; auto&& {name} = {valueText};";
    }

    /// <summary>
    /// A top-level type test keeps the form <c>vpl::is</c>. Other patterns that bind names copy
    /// every bound value before storing any: a store releases the local's old value, which may run a
    /// destructor that changes the matched value.
    /// </summary>
    string Is(BoundIs pattern)
    {
        if (pattern.Pattern is BoundTypePattern { Type.IsNullableValue: false } test)
        {
            var target = ObjectType(test.Target);
            var operand = Expression(pattern.Operand);
            return test.Binding is { } binding
                ? $"vpl::is<{target}>({operand}, {LocalName(binding)})"
                : $"vpl::is<{target}>({operand})";
        }

        var tests = new List<string>();
        var bindings = new List<(LocalSymbol Local, string Value)>();
        if (IsStable(pattern.Operand))
        {
            PatternParts(pattern.Pattern, Expression(pattern.Operand), tests, bindings);
            if (bindings.Count == 0)
                return $"({Conjunction(tests)})";
        }
        var matched = NewTemp();
        var body = new StringBuilder($"[&] {{ {MatchedValue(pattern.Operand, matched)} ");
        tests.Clear();
        bindings.Clear();
        PatternParts(pattern.Pattern, matched, tests, bindings);
        if (bindings.Count == 0)
            return body.Append($"return {Conjunction(tests)}; }}()").ToString();

        body.Append($"if (!({Conjunction(tests)})) {{ return false; }} ");
        var copies = bindings.Select(b => (b.Local, Temp: NewTemp(), b.Value)).ToList();
        foreach (var (local, temp, value) in copies)
            body.Append($"{CppType(local.Type)} {temp} = {value}; ");
        foreach (var (local, temp, _) in copies)
            body.Append($"{LocalName(local)} = std::move({temp}); ");
        return body.Append("return true; }()").ToString();
    }

    /// <summary>
    /// Tests the cases in order. The value is evaluated once and the matching case's names are
    /// declared fresh, copied from it, so nothing is released while it is read; a temporary value
    /// lives until the switch ends.
    /// </summary>
    void Switch(BoundSwitch switchStatement)
    {
        DeclarePatternLocals(switchStatement.Value);
        Line("{");
        indent++;
        var matched = NewTemp();
        Line(MatchedValue(switchStatement.Value, matched));
        var caseTests = switchStatement.Cases.Select(c => CaseTest(c.Patterns, matched)).ToList();
        if (caseTests.All(t => t.Condition is null && t.Bindings.Count == 0))
            Line($"static_cast<void>({matched});");

        var keyword = "if";
        bool exhausted = false;
        foreach (var (switchCase, (condition, bindings)) in switchStatement.Cases.Zip(caseTests))
        {
            if (condition is null)
            {
                Line(keyword == "if" ? "{" : "else");
                if (keyword != "if")
                    Line("{");
                exhausted = true;
            }
            else
            {
                Line($"{keyword} ({condition})");
                Line("{");
            }
            indent++;
            foreach (var (local, value) in bindings)
                Line($"[[maybe_unused]] {CppType(local.Type)} {LocalName(local)} = {value};");
            var statements = switchCase.Body is BoundBlock block ? block.Statements : [switchCase.Body];
            foreach (var statement in statements)
                Statement(statement);
            indent--;
            Line("}");
            if (exhausted)
                break;
            keyword = "else if";
        }
        if (!exhausted)
        {
            Line("else");
            Line("{");
            Line($"    vpl::no_match({SourceLocation(switchStatement.Location)});");
            Line("}");
        }
        indent--;
        Line("}");
    }

    /// <summary>
    /// The condition under which a case matches, or null if it always does, and the values of the
    /// names it binds. Only a case with one pattern binds names.
    /// </summary>
    (string? Condition, List<(LocalSymbol Local, string Value)> Bindings) CaseTest(IReadOnlyList<BoundPattern> patterns, string value)
    {
        var bindings = new List<(LocalSymbol Local, string Value)>();
        if (patterns.Count == 0)
            return (null, bindings);
        var alternatives = new List<string>();
        foreach (var pattern in patterns)
        {
            var tests = new List<string>();
            PatternParts(pattern, value, tests, bindings);
            if (tests.Count == 0)
                return (null, bindings);
            alternatives.Add(Conjunction(tests));
        }
        return (alternatives.Count == 1 ? alternatives[0] : string.Join(" || ", alternatives.Select(a => $"({a})")), bindings);
    }

    string SwitchExpression(BoundSwitchExpression switchExpression)
    {
        var matched = NewTemp();
        var body = new StringBuilder($"[&]() -> {CppType(switchExpression.Type)} {{ {MatchedValue(switchExpression.Value, matched)} ");
        var caseTests = switchExpression.Cases.Select(c => CaseTest(c.Patterns, matched)).ToList();
        if (caseTests.All(t => t.Condition is null && t.Bindings.Count == 0))
            body.Append($"static_cast<void>({matched}); ");
        bool exhausted = false;
        foreach (var (switchCase, (condition, bindings)) in switchExpression.Cases.Zip(caseTests))
        {
            body.Append(condition is null ? "{ " : $"if ({condition}) {{ ");
            foreach (var (local, value) in bindings)
                body.Append($"[[maybe_unused]] {CppType(local.Type)} {LocalName(local)} = {value}; ");
            foreach (var local in PatternLocals(switchCase.Body))
                body.Append($"{CppType(local.Type)} {LocalName(local)}{{}}; ");
            var caseValue = Expression(switchCase.Body);
            body.Append(switchCase.Body.Type == VplType.Void ? $"{caseValue}; }} " : $"return {caseValue}; }} ");
            if (condition is null)
            {
                exhausted = true;
                break;
            }
        }
        if (!exhausted)
            body.Append($"vpl::no_match({SourceLocation(switchExpression.Location)}); ");
        return body.Append("}()").ToString();
    }
}
