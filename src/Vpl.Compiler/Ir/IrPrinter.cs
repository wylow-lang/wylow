using System.Globalization;
using System.Text;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Ir;

/// <summary>
/// Prints the IR as <c>vpl ir</c> shows it, in the syntax of docs/design/ir.md. Temporaries are
/// numbered in order of appearance; a lambda's body is printed where it is made.
/// </summary>
public sealed class IrPrinter
{
    readonly StringBuilder text = new();
    readonly Dictionary<IrLocal, string> names = [];
    readonly Dictionary<string, int> sourceNames = [];
    readonly Dictionary<IrLoop, string> labels = [];
    int temporaries;
    int indent;

    public static string Print(IrProgram program)
    {
        var printer = new IrPrinter();
        foreach (var function in program.Functions.Where(f => !f.IsLambda))
            printer.Function(function);
        return printer.text.ToString();
    }

    public static string Print(IrFunction function)
    {
        var printer = new IrPrinter();
        printer.Function(function);
        return printer.text.ToString();
    }

    /// <summary>One statement, as a verifier error shows it; a compound statement shows only its head.</summary>
    public static string Print(IrFunction function, IrStatement statement)
    {
        var printer = new IrPrinter();
        printer.NameLocals(function);
        return printer.Head(statement);
    }

    /// <summary>The name each local of a function is printed with.</summary>
    public static IReadOnlyDictionary<IrLocal, string> Names(IrFunction function)
    {
        var printer = new IrPrinter();
        printer.NameLocals(function);
        return printer.names;
    }

    public static string Print(IrFunction function, IrPlace place)
    {
        var printer = new IrPrinter();
        printer.NameLocals(function);
        return printer.Place(place);
    }

    void NameLocals(IrFunction function)
    {
        // Naming every local up front gives a statement printed alone the names the whole function would.
        var whole = new IrPrinter();
        whole.Function(function);
        foreach (var (local, name) in whole.names)
            names[local] = name;
        foreach (var (loop, label) in whole.labels)
            labels[loop] = label;
    }

    void Function(IrFunction function)
    {
        names.Clear();
        sourceNames.Clear();
        labels.Clear();
        temporaries = 0;
        Line($"fn {function.Name}{Signature(function)} {{");
        indent++;
        Statements(function.Body);
        indent--;
        Line("}");
        Line("");
    }

    string Signature(IrFunction function)
    {
        IEnumerable<string> parameters = [.. function.This is { } self ? [Declaration(self)] : Array.Empty<string>(), .. function.Parameters.Select(Declaration)];
        var result = function.Symbol.ReturnType == VplType.Void ? "" : $" -> {FunctionSymbol.RefPrefix(function.Symbol.ReturnRefKind)}{function.Symbol.ReturnType}";
        var location = function.HasLocationParameter ? " @caller" : "";
        var captures = function.Captures.Count > 0 ? $" captures({string.Join(", ", function.Captures.Select(Declaration))})" : "";
        return $"({string.Join(", ", parameters)}){result}{location}{captures}";
    }

    void Line(string line) => text.Append(new string(' ', indent * 2)).Append(line).Append('\n');

    void Statements(IrBlock block)
    {
        foreach (var statement in block.Statements)
            Statement(statement);
    }

    void Statement(IrStatement statement)
    {
        switch (statement)
        {
            case IrBlock block:
                Line("{");
                Nested(block);
                Line("}");
                break;
            case IrIf conditional:
                Line(Head(conditional));
                Nested(conditional.Then);
                if (conditional.Else is { } otherwise)
                {
                    Line("} else {");
                    Nested(otherwise);
                }
                Line("}");
                break;
            case IrLoop loop:
                Line(Head(loop));
                Nested(loop.Body);
                if (loop.Continue is { } next)
                {
                    Line("} continue {");
                    Nested(next);
                }
                Line("}");
                break;
            case IrLet { Value: IrLambda or IrClosure } let:
                // The body is printed on the lines after the head, which ends with its opening brace.
                Line(Head(let));
                Nested(LambdaFunction(let.Value).Body);
                Line("}");
                break;
            default:
                Line(Head(statement));
                break;
        }
    }

    static IrFunction LambdaFunction(IrRvalue value) => value is IrLambda lambda ? lambda.Function : ((IrClosure)value).Function;

    void Nested(IrBlock block)
    {
        indent++;
        Statements(block);
        indent--;
    }

    string Head(IrStatement statement) => statement switch
    {
        IrBlock => "{",
        IrLet let => $"let {Declaration(let.Local)} = {Rvalue(let.Value)}",
        IrDeclare declare => $"declare{(declare.IsBinding ? ".binding" : "")} {Declaration(declare.Local)}",
        IrStore store => $"store {Place(store.Target)} = {Operand(store.Value)}",
        IrDo run => $"do {Rvalue(run.Value)}",
        IrCheck check => $"check.{CheckName(check.Kind)}{(check.Operands.Count > 0 ? " " : "")}{string.Join(", ", check.Operands.Select(Operand))} {Loc(check.Loc)}",
        IrIf conditional => $"if {Operand(conditional.Condition)} {{",
        IrLoop loop => $"loop {Label(loop)} {{",
        IrBreak jump => $"break {Label(jump.Loop)}",
        IrContinue jump => $"continue {Label(jump.Loop)}",
        IrReturn { Value: null } => "return",
        IrReturn ret => $"return {Operand(ret.Value!)}",
        IrEnd end => $"end {Name(end.Local)}",
        IrUnreachable unreachable => $"unreachable {Loc(unreachable.Loc)}",
        _ => throw new InvalidOperationException($"unexpected IR statement {statement.GetType().Name}"),
    };

    static string CheckName(IrCheckKind kind) => kind switch
    {
        IrCheckKind.Index => "index",
        IrCheckKind.NotNull => "not_null",
        IrCheckKind.HasValue => "has_value",
        IrCheckKind.NoMatch => "no_match",
        _ => throw new InvalidOperationException($"unexpected check {kind}"),
    };

    string Label(IrLoop loop)
    {
        if (!labels.TryGetValue(loop, out var label))
            labels[loop] = label = $"L{labels.Count}";
        return label;
    }

    string Name(IrLocal local)
    {
        if (names.TryGetValue(local, out var name))
            return name;
        if (local.IsTemporary)
            name = $"%{temporaries++}";
        else
        {
            int count = sourceNames[local.Name] = sourceNames.GetValueOrDefault(local.Name) + 1;
            name = count == 1 ? local.Name : $"{local.Name}#{count}";
        }
        return names[local] = name;
    }

    string Declaration(IrLocal local)
    {
        var kind = local.Kind switch
        {
            IrLocalKind.Reference when !local.Type.IsNullableReference => FunctionSymbol.RefPrefix(local.RefKind),
            IrLocalKind.Borrowed => "borrowed ",
            IrLocalKind.Hold => "hold ",
            IrLocalKind.Slot => "slot ",
            _ => "",
        };
        return $"{Name(local)}: {kind}{local.Type}";
    }

    string Place(IrPlace place)
    {
        var text = new StringBuilder(place.IsDeref ? $"*{Name(place.Local)}" : Name(place.Local));
        if (place.IsDeref && place.Projections.Count > 0)
            text.Insert(0, '(').Append(')');
        foreach (var projection in place.Projections)
        {
            text.Append(projection switch
            {
                IrField field => $".{field.Field.Name}",
                IrElement element => $"[{Operand(element.Index)}]",
                IrVariantField variant => $".{variant.Variant.Name}.{variant.Field.Name}",
                IrAssumed => "!",
                _ => throw new InvalidOperationException($"unexpected projection {projection.GetType().Name}"),
            });
        }
        return text.ToString();
    }

    string Operand(IrOperand operand) => operand switch
    {
        IrConstant constant => Constant(constant),
        IrRead read => $"{read.Ownership.ToString().ToLowerInvariant()} {Place(read.Place)}",
        _ => throw new InvalidOperationException($"unexpected operand {operand.GetType().Name}"),
    };

    static string Constant(IrConstant constant) => constant.Value switch
    {
        null => constant.Type == VplType.Null ? "null" : $"null:{constant.Type}",
        bool b => b ? "true" : "false",
        string s => $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")}\"",
        System.Text.Rune r => $"'{r}'",
        int i => i.ToString(CultureInfo.InvariantCulture),
        IFormattable number => $"{number.ToString(null, CultureInfo.InvariantCulture)}:{constant.Type}",
        var other => $"{other}:{constant.Type}",
    };

    string Argument(IrArgument argument) => argument switch
    {
        IrOperand operand => Operand(operand),
        IrRefArgument reference => $"{FunctionSymbol.RefPrefix(reference.Kind)}{Place(reference.Place)}",
        IrAddrArgument address => $"addr{(address.Kind == RefKind.In ? ".in" : "")} {Place(address.Place)}",
        IrFieldDefault field => $"default.{field.Field.Name}",
        _ => throw new InvalidOperationException($"unexpected argument {argument.GetType().Name}"),
    };

    string Arguments(IrArgument? receiver, IEnumerable<IrArgument> arguments) =>
        $"({string.Join(", ", (receiver is null ? arguments : arguments.Prepend(receiver)).Select(Argument))})";

    static string Loc(IrLoc loc) => loc.Source is { } source
        ? $"@{source.Source.FileName}:{source.LineColumn.Line}:{source.LineColumn.Column}"
        : "@caller";

    static string WithLoc(string text, IrLoc? loc) => loc is null ? text : $"{text} {Loc(loc)}";

    static string Callee(MethodReference method) =>
        method.Owner is { } owner ? $"{owner}.{method.Symbol.Name}" : method.Symbol.Name;

    string Rvalue(IrRvalue value) => value switch
    {
        IrUse use => Operand(use.Operand),
        IrUnary unary => WithLoc($"{UnaryName(unary.Operator)} {Operand(unary.Operand)}", unary.Loc),
        IrBinary binary => WithLoc($"{BinaryName(binary.Operator)} {Operand(binary.Left)}, {Operand(binary.Right)}", binary.Loc),
        IrCall call => WithLoc($"call{(call.IsSuperCall ? ".super" : "")} {Callee(call.Method)}{Arguments(call.Receiver, call.Arguments)}", call.Loc),
        IrInvoke invoke => $"invoke {Operand(invoke.Callee)}{Arguments(null, invoke.Arguments)}",
        IrNew creation => WithLoc($"new {creation.Type}{Arguments(null, creation.Arguments)}{Slot(creation.Slot)}", creation.Loc),
        IrConstruct construction => WithLoc($"construct {construction.Type}{Arguments(null, construction.Arguments)}", construction.Loc),
        IrVariant variant => $"variant {variant.Type}.{variant.Variant.Name}({string.Join(", ", variant.Fields.Select(Operand))})",
        IrArray array => $"array {array.Type} [{string.Join(", ", array.Elements.Select(Operand))}]",
        IrDefault defaulted => $"default {defaulted.Type}",
        IrClosure closure => $"closure {closure.Function.Name}{Signature(closure.Function)} [{string.Join(", ", closure.Captures.Select(Operand))}]{Slot(closure.Slot)} {{",
        IrLambda lambda => $"lambda {lambda.Function.Name}{Signature(lambda.Function)} {{",
        IrChoose choice => $"choose {Operand(choice.Index)} [{string.Join(", ", choice.Options.Select(Operand))}]",
        IrRef reference => $"{FunctionSymbol.RefPrefix(reference.Kind)}{Place(reference.Place)}",
        IrAddr address => $"addr{(address.Kind == RefKind.In ? ".in" : "")} {Place(address.Place)}",
        IrData data => $"data {Place(data.Place)}",
        IrTest test => test.Kind switch
        {
            IrTestKind.NotNull => $"test.not_null {Operand(test.Operand)}",
            IrTestKind.HasValue => $"test.has_value {Operand(test.Operand)}",
            IrTestKind.Tag => $"test.tag {Operand(test.Operand)}, {test.Variant!.Name}",
            IrTestKind.Class => $"test.class {Operand(test.Operand)}, {test.Target}",
            _ => throw new InvalidOperationException($"unexpected test {test.Kind}"),
        },
        IrConvert conversion => WithLoc($"convert {Operand(conversion.Operand)} to {conversion.Type}", conversion.Loc),
        IrHold hold => $"hold{(hold.Borrows ? ".borrow" : "")} {Place(hold.Place)}",
        _ => throw new InvalidOperationException($"unexpected rvalue {value.GetType().Name}"),
    };

    string Slot(IrLocal? slot) => slot is null ? "" : $" in slot {Name(slot)}";

    static string UnaryName(BoundUnaryOperator op) => op switch
    {
        BoundUnaryOperator.Negate => "neg",
        BoundUnaryOperator.LogicalNot => "not",
        BoundUnaryOperator.BitwiseNot => "bitnot",
        _ => throw new InvalidOperationException($"unexpected unary operator {op}"),
    };

    static string BinaryName(BoundBinaryOperator op) => op switch
    {
        BoundBinaryOperator.Add => "add",
        BoundBinaryOperator.Subtract => "sub",
        BoundBinaryOperator.Multiply => "mul",
        BoundBinaryOperator.Divide => "div",
        BoundBinaryOperator.Remainder => "rem",
        BoundBinaryOperator.BitwiseAnd => "bitand",
        BoundBinaryOperator.BitwiseOr => "bitor",
        BoundBinaryOperator.BitwiseXor => "bitxor",
        BoundBinaryOperator.ShiftLeft => "shl",
        BoundBinaryOperator.ShiftRight => "shr",
        BoundBinaryOperator.LogicalAnd => "and",
        BoundBinaryOperator.LogicalOr => "or",
        BoundBinaryOperator.Equal => "eq",
        BoundBinaryOperator.NotEqual => "ne",
        BoundBinaryOperator.Less => "lt",
        BoundBinaryOperator.LessOrEqual => "le",
        BoundBinaryOperator.Greater => "gt",
        BoundBinaryOperator.GreaterOrEqual => "ge",
        _ => throw new InvalidOperationException($"unexpected binary operator {op}"),
    };
}
