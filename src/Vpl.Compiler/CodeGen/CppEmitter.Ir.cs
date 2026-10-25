using System.Text;
using Vpl.Compiler.Ir;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.CodeGen;

/// <summary>
/// Prints a lowered function's body. Every decision about order, holds, and copies was made by
/// lowering; this only translates each instruction, nesting temporaries back into their uses as
/// <see cref="NestingPlan"/> allows.
/// </summary>
public sealed partial class CppEmitter
{
    readonly Dictionary<IrLocal, string> irNames = [];

    /// <summary>The closure each slot holds, whose type the slot is declared with before it.</summary>
    readonly Dictionary<IrLocal, IrClosure> closureSlots = [];

    void EmitIrBody(IrFunction function)
    {
        irNames.Clear();
        continueLabels.Clear();
        closureSlots.Clear();
        foreach (var closure in IrWalk.Descendants(function.Body).OfType<IrLet>().Select(l => l.Value).OfType<IrClosure>())
        {
            if (closure.Slot is { } slot)
                closureSlots[slot] = closure;
        }
        PlanNesting(function, nest);
        IrStatements(function.Body);
    }

    void IrStatements(IrBlock block)
    {
        IrStatement? previous = null;
        foreach (var statement in block.Statements.Where(s => !plan.Consumed.Contains(s)))
        {
            IrStatement(statement, previous);
            previous = statement;
        }
    }

    void IrStatement(IrStatement statement, IrStatement? previous)
    {
        switch (statement)
        {
            case var definition when DefinedBy(definition) is { } local:
                Line(Declaration(local, InitializedText(local)));
                break;
            case IrBlock block:
                bool braces = TopLevel(block).Any(s => s is IrLet or IrDeclare || DefinedBy(s) is not null);
                if (braces)
                {
                    Line("{");
                    indent++;
                }
                IrStatements(block);
                if (braces)
                {
                    indent--;
                    Line("}");
                }
                break;
            case IrLet let:
                Line(Declaration(let.Local, RvalueText(let.Value), let.Value));
                break;
            case IrDeclare { Local.Kind: IrLocalKind.Slot } declare:
                var held = closureSlots.TryGetValue(declare.Local, out var closure) ? ClosureObjectType(closure) : ObjectType(declare.Local.Type);
                Line($"vpl::StackSlot<{held}> {Name(declare.Local)};");
                break;
            case IrDeclare declare when plan.Evaluated.TryGetValue(declare.Local, out var evaluated):
                Line($"auto {Name(declare.Local)} = [&] {{");
                indent++;
                IrStatements(evaluated.Block);
                Line($"return {OperandText(evaluated.Store.Value)};");
                indent--;
                Line("}();");
                break;
            case IrDeclare declare:
                Line($"{CppType(declare.Local.Type)} {Name(declare.Local)}{{}};");
                break;
            case IrEnd { Local.Kind: IrLocalKind.Slot } end:
                Line($"{Name(end.Local)}.end();");
                break;
            case IrStore or IrDo:
                Line(StatementText(statement) + ";");
                break;
            case IrCheck check:
                Line(CheckText(check) + ";");
                break;
            case IrReturn ret:
                Line(ret.Value switch
                {
                    null => "return;",
                    // C++ moves a returned local itself, and `std::move` would prevent eliding the move.
                    IrRead { Ownership: IrOwnership.Move, Place: { IsDeref: false, Projections.Count: 0, Local: { Kind: IrLocalKind.Value } local } }
                        when !plan.Nested.ContainsKey(local) => $"return {Name(local)};",
                    var value => $"return {OperandText(value)};",
                });
                break;
            case IrIf conditional:
                If(conditional, "if");
                break;
            case IrLoop loop:
                Loop(loop);
                break;
            case IrBreak:
                Line("break;");
                break;
            case IrContinue jump:
                Line(continueLabels.TryGetValue(jump.Loop, out var label) ? $"goto {label};" : "continue;");
                break;
            case IrUnreachable unreachable:
                // A call that doesn't return is [[noreturn]] in C++ too, and so is `vpl::no_match`.
                if (previous is not (IrDo { Value: IrCall { Method.Symbol.IsNoReturn: true } } or IrCheck { Kind: IrCheckKind.NoMatch }))
                    Line($"vpl::unreachable({LocText(unreachable.Loc)});");
                break;
            default:
                throw new InvalidOperationException($"the IR emitter can't print {statement.GetType().Name} yet");
        }
    }

    /// <summary>The value a local declared without one is defined with: a block's last store, or a choice between two.</summary>
    string InitializedText(IrLocal local)
    {
        IrOperand Stored(IrBlock block) => ((IrStore)block.Statements.Last(s => s is IrStore store && IsWholeStore(store, local))).Value;
        switch (plan.Initialized[local].Definition)
        {
            case IrBlock block:
                return OperandText(Stored(block));
            case IrIf conditional:
                var condition = OperandText(conditional.Condition);
                var (then, otherwise) = (Stored(conditional.Then), Stored(conditional.Else!));
                if (otherwise is IrConstant { Value: false })
                    return $"({condition} && {OperandText(then)})";
                if (then is IrConstant { Value: true })
                    return $"({condition} || {OperandText(otherwise)})";
                return $"({condition} ? {OperandText(then)} : {OperandText(otherwise)})";
            default:
                throw new InvalidOperationException("unexpected definition");
        }
    }

    void Braced(IrBlock block)
    {
        Line("{");
        indent++;
        IrStatements(block);
        indent--;
        Line("}");
    }

    void If(IrIf conditional, string keyword)
    {
        var condition = OperandText(conditional.Condition);
        if (TopLevel(conditional.Then).Count == 0 && conditional.Else is { } only)
        {
            Line($"{keyword} (!{condition})");
            Braced(only);
            return;
        }
        Line($"{keyword} {Parenthesized(condition)}");
        Braced(conditional.Then);
        if (conditional.Else is not { } otherwise || TopLevel(otherwise).Count == 0)
            return;
        if (TopLevel(otherwise) is [IrIf chained] && DefinedBy(chained) is null)
            If(chained, "else if");
        else
        {
            Line("else");
            Braced(otherwise);
        }
    }

    /// <summary>Loops whose update a <c>continue</c> jumps to with <c>goto</c>, since it isn't a <c>for</c> loop's.</summary>
    readonly Dictionary<IrLoop, string> continueLabels = [];

    /// <summary>
    /// A <c>while</c> or <c>for</c> loop when the body starts by breaking unless a condition holds
    /// and the update is one statement, and otherwise a <c>while (true)</c> whose <c>continue</c>s
    /// jump to the update after the body's block, which skips no declaration.
    /// </summary>
    void Loop(IrLoop loop)
    {
        var body = TopLevel(loop.Body);
        string? condition = null;
        if (body.FirstOrDefault() is IrIf head && TopLevel(head.Then).Count == 0 && head.Else is { } exit
            && TopLevel(exit) is [IrBreak { Loop: var exited }] && exited == loop && DefinedBy(head) is null)
        {
            condition = OperandText(head.Condition);
            body.RemoveAt(0);
        }
        var update = loop.Continue is { } next ? TopLevel(next) : [];
        bool continues = IrWalk.Descendants(loop.Body).OfType<IrContinue>().Any(c => c.Loop == loop);
        if (condition is not null && update is [IrStore or IrDo])
        {
            var text = StatementText(update[0]);
            Line($"for (; {Unparenthesized(condition)}; {text})");
            update = [];
        }
        else
        {
            Line($"while {Parenthesized(condition ?? "true")}");
            if (continues && update.Count > 0)
                continueLabels[loop] = $"vpl_continue_{tempCounter++}";
        }
        // The body is a block of its own, which the loop's braces can be once nothing follows it.
        if (body is [IrBlock only] && update.Count == 0 && !continueLabels.ContainsKey(loop))
            body = TopLevel(only);
        Line("{");
        indent++;
        IrStatement? previous = null;
        foreach (var statement in body)
        {
            IrStatement(statement, previous);
            previous = statement;
        }
        if (continueLabels.TryGetValue(loop, out var label))
            Line($"{label}:;");
        foreach (var statement in update)
            IrStatement(statement, null);
        indent--;
        Line("}");
    }

    /// <summary>The text in parentheses, which it may already be in as a whole.</summary>
    static string Parenthesized(string text) => IsParenthesized(text) ? text : $"({text})";

    static string Unparenthesized(string text) => IsParenthesized(text) ? text[1..^1] : text;

    static bool IsParenthesized(string text)
    {
        if (!text.StartsWith('(') || !text.EndsWith(')'))
            return false;
        int depth = 0;
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (quoted)
            {
                if (text[i] == '\\')
                    i++;
                else if (text[i] == '"')
                    quoted = false;
                continue;
            }
            quoted = text[i] == '"';
            depth += text[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0 && i < text.Length - 1)
                return false;
        }
        return true;
    }

    /// <summary>A store or evaluation as C++ without its semicolon, as a <c>for</c> loop's update.</summary>
    string StatementText(IrStatement statement) => statement switch
    {
        IrStore store => StoreText(store.Target.Type, PlaceText(store.Target), OperandText(store.Value)),
        IrDo run => run.Value is IrCall or IrNew or IrInvoke ? RvalueText(run.Value) : $"static_cast<void>({RvalueText(run.Value)})",
        _ => throw new InvalidOperationException($"unexpected statement {statement.GetType().Name}"),
    };

    string Name(IrLocal local)
    {
        if (irNames.TryGetValue(local, out var name))
            return name;
        // A closure's copies get new names, so they don't shadow the variables they copy.
        name = local switch
        {
            { IsCapture: true, Symbol: { } captured } => $"{LocalName(captured)}_{NewTemp()}",
            { IsCapture: true } => $"vpl_this_{NewTemp()}",
            { Symbol: { } symbol } => LocalName(symbol),
            _ => NewTemp(),
        };
        return irNames[local] = name;
    }

    string Declaration(IrLocal local, string value, IrRvalue? rvalue = null)
    {
        var name = Name(local);
        bool mayBeUnused = local.Kind is IrLocalKind.Reference or IrLocalKind.Borrowed || local.Type.IsRefStruct || plan.Uses.GetValueOrDefault(local) == 0;
        var unused = local.Symbol is not null && mayBeUnused ? "[[maybe_unused]] " : "";
        return local.Kind switch
        {
            // A non-escaping local owns the lambda or choice it is made with, or copies a stored function. Another
            // non-escaping local or parameter can't be reassigned and outlives it, so it is referred to instead.
            IrLocalKind.Value when IsNonEscaping(local.Type) => rvalue is IrUse { Operand: IrRead { Place: { IsDeref: false, Projections.Count: 0, Local: { IsTemporary: false } aliased } } } && IsNonEscaping(aliased.Type)
                ? $"{unused}const auto& {name} = {value};"
                : $"{unused}auto {name} = {value};",
            IrLocalKind.Value => $"{unused}{CppType(local.Type)} {name} = {value};",
            IrLocalKind.Reference when local.Type.IsNullableReference => $"{unused}{CppType(local.Type)} {name} = {value};",
            IrLocalKind.Reference => $"{unused}{(local.RefKind == RefKind.In ? "const " : "")}{CppType(local.Type)}& {name} = {value};",
            IrLocalKind.Borrowed => $"{unused}const {CppType(local.Type)}& {name} = {value};",
            IrLocalKind.Hold => $"{(local.Type.IsTypeParameter ? "auto&&" : "auto")} {name} = {value};",
            _ => throw new InvalidOperationException($"the IR emitter can't declare a {local.Kind} local yet"),
        };
    }

    /// <summary>The value of a temporary printed where it is used.</summary>
    string NestedText(IrLocal local) => plan.Nested[local] switch
    {
        IrLet let => RvalueText(let.Value),
        IrBlock or IrIf => InitializedText(local),
        var other => throw new InvalidOperationException($"unexpected definition {other.GetType().Name}"),
    };

    string BaseText(IrLocal local) => plan.Nested.ContainsKey(local) ? NestedText(local) : Name(local);

    static bool IsObjectLocal(IrLocal local) => local.Kind != IrLocalKind.Reference && local.Type.NonNull.IsReference;

    string PlaceText(IrPlace place)
    {
        var local = place.Local;
        var projections = place.Projections;
        string text;
        int start = 0;
        if (!place.IsDeref)
        {
            if (local.IsThis && local.Type.IsReference)
                text = $"vpl::Ref<{ObjectType(local.Type)}>(this)";
            else if (local.IsThis && projections is [IrField first, ..])
                (text, start) = ($"this->{FieldName(first.Field)}", 1);
            else
                text = local.IsThis ? "(*this)" : BaseText(local);
        }
        else
        {
            var holder = local.IsThis ? "this" : BaseText(local);
            if (IsObjectLocal(local) && projections is [IrField field, ..])
                (text, start) = ($"{holder}->{FieldName(field.Field)}", 1);
            else if ((local.Kind == IrLocalKind.Reference && !local.Type.IsNullableReference) || local.Type.IsTypeParameter)
                text = holder;
            else if (local.Type.IsPointer && local.Kind != IrLocalKind.Reference && projections is [IrElement element, ..])
                (text, start) = ($"{holder}[{OperandText(element.Index)}]", 1);
            else
                text = $"(*{holder})";
        }
        foreach (var projection in projections.Skip(start))
        {
            text = projection switch
            {
                IrField field => $"{text}.{FieldName(field.Field)}",
                IrElement element when plan.Fused.TryGetValue(element, out var check) => $"vpl::at({text}, {OperandText(element.Index)}, {LocText(check.Loc)})",
                IrElement { Index: IrConstant constant } => $"{text}[{ConstantText(constant)}]",
                IrElement element => $"{text}[static_cast<size_t>({OperandText(element.Index)})]",
                IrVariantField variant => $"{text}.{UnionMember(variant.Variant)}.{FieldName(variant.Field)}",
                IrAssumed => $"vpl::assume_value({text})",
                _ => throw new InvalidOperationException($"unexpected projection {projection.GetType().Name}"),
            };
        }
        return text;
    }

    string OperandText(IrOperand operand)
    {
        if (operand is IrConstant constant)
            return ConstantText(constant);
        var read = (IrRead)operand;
        if (read.Place is { IsDeref: false, Projections.Count: 0, Local: var local } && !local.IsThis)
        {
            if (plan.Nested.ContainsKey(local))
                return NestedText(local);
            if (read.Ownership == IrOwnership.Move && HasCountedParts(read.Type))
                return $"std::move({Name(local)})";
        }
        return PlaceText(read.Place);
    }

    string ConstantText(IrConstant constant) => constant.Value switch
    {
        null when constant.Type == VplType.Null => "nullptr",
        null when constant.Type.IsPointer || constant.Type.IsNullableReference => $"static_cast<{CppType(constant.Type)}>(nullptr)",
        null => $"{CppType(constant.Type)}()",
        var value => Literal(constant.Type, value),
    };

    string LocText(IrLoc loc) => loc.Source is { } source ? SourceLocation(source) : CallerLocationParameter;

    /// <summary>
    /// A by-value argument. A copy of a temporary the statement keeps is passed through <c>vpl::keep</c>,
    /// which makes the temporary the caller's, released when the statement completes.
    /// </summary>
    string ArgumentText(IrArgument argument) => argument switch
    {
        IrRefArgument reference => PlaceText(reference.Place),
        IrAddrArgument address => $"vpl::address_of({PlaceText(address.Place)})",
        IrRead { Ownership: IrOwnership.Copy, Place: { IsDeref: false, Projections.Count: 0, Local: var local } } when plan.Nested.ContainsKey(local) && local.Type.MayHoldReferences =>
            plan.Nested[local] is IrLet { Value: IrUse { Operand: IrRead place } }
                ? $"vpl::keep({CppType(local.Type)}({PlaceText(place.Place)}))"
                : $"vpl::keep({NestedText(local)})",
        IrOperand operand => OperandText(operand),
        _ => throw new InvalidOperationException($"unexpected argument {argument.GetType().Name}"),
    };

    /// <summary>A call's arguments, with what the C++ form of each parameter needs, and the call site's location if the callee takes it.</summary>
    List<string> ArgumentsText(MethodReference method, IReadOnlyList<IrArgument> arguments, IrLoc? loc)
    {
        var texts = new List<string>();
        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] is not IrFieldDefault)
                texts.Add(FunctionArgument(method, i, ArgumentText(arguments[i])));
        }
        if (loc is not null)
            texts.Add(LocText(loc));
        return texts;
    }

    string RvalueText(IrRvalue value) => value switch
    {
        IrUse use => OperandText(use.Operand),
        IrUnary unary => UnaryText(unary.Operator, unary.Type, OperandText(unary.Operand), unary.Loc is { } loc ? LocText(loc) : ""),
        IrBinary binary => BinaryText(binary.Operator, binary.Left.Type, binary.Type, OperandText(binary.Left), OperandText(binary.Right), () => LocText(binary.Loc!)),
        IrCall call => CallText(call),
        IrInvoke invoke => $"{OperandText(invoke.Callee)}({string.Join(", ", invoke.Arguments.Select((a, i) => OptionalFunctionArgument(invoke.Signature.ParameterType(i), ArgumentText(a))))})",
        IrLambda lambda => LambdaText(lambda.Function, "[&]", []),
        IrClosure closure => ClosureText(closure),
        IrChoose choice => $"vpl::choose({string.Join(", ", [OperandText(choice.Index), .. choice.Options.Select(OptionText)])})",
        IrNew { Slot: null } creation => $"vpl::adopt(new {ObjectType(creation.Type)}({string.Join(", ", ArgumentsText(creation.Constructor, creation.Arguments, creation.Loc))}))",
        IrNew { Slot: { } slot } creation => $"{Name(slot)}.make({string.Join(", ", ArgumentsText(creation.Constructor, creation.Arguments, creation.Loc))})",
        IrConstruct construction => ConstructText(construction),
        IrVariant variant => $"{CppType(variant.Type)}::{VariantFactory(variant.Variant)}({string.Join(", ", variant.Fields.Select(OperandText))})",
        // Braces evaluate the elements left to right.
        IrArray array => $"{CppType(array.Type)}{{{string.Join(", ", array.Elements.Select(OperandText))}}}",
        IrDefault defaulted => DefaultValue(defaulted.Type),
        IrRef reference => PlaceText(reference.Place),
        IrAddr address => $"vpl::address_of({PlaceText(address.Place)})",
        IrData data => $"const_cast<{CppType(data.Type)}>({PlaceText(data.Place)}.data())",
        IrTest test => TestText(test),
        // The function value is converted where it is used, which a template parameter may take as it is.
        IrConvert { Type.IsFunction: true, Type.IsEscaping: false } conversion => OperandText(conversion.Operand),
        IrConvert conversion when IsDowncast(conversion) => $"vpl::downcast<{ObjectType(conversion.Type)}>({OperandText(conversion.Operand)})",
        IrConvert conversion => ConversionText(conversion.Operand.Type, conversion.Type, OperandText(conversion.Operand), () => LocText(conversion.Loc!)),
        IrHold hold => $"vpl::{(hold.Borrows ? "borrow" : "pin")}({PlaceText(hold.Place)})",
        _ => throw new InvalidOperationException($"the IR emitter can't print {value.GetType().Name} yet"),
    };

    string TestText(IrTest test)
    {
        var operand = OperandText(test.Operand);
        return test.Kind switch
        {
            IrTestKind.NotNull when test.Operand.Type.IsPointer || test.Operand.Type.IsNullableReference => $"({operand} != nullptr)",
            IrTestKind.NotNull or IrTestKind.HasValue => $"vpl::has_value({operand})",
            IrTestKind.Tag => $"({operand}.tag == {TagValue(test.Variant!)})",
            IrTestKind.Class => $"vpl::is<{ObjectType(test.Target!)}>({operand})",
            _ => throw new InvalidOperationException($"the IR emitter can't print a {test.Kind} test yet"),
        };
    }

    /// <summary>A conversion of a reference to a class it may not refer to, which a type test has checked.</summary>
    static bool IsDowncast(IrConvert conversion) =>
        conversion.Type.IsReference && conversion.Operand.Type.NonNull.IsReference && !Conversions.IsSubtype(conversion.Operand.Type.NonNull, conversion.Type.NonNull);

    string CheckText(IrCheck check)
    {
        var operands = check.Operands.Select(OperandText).Append(LocText(check.Loc));
        var name = check.Kind switch
        {
            IrCheckKind.Index => "check_index",
            IrCheckKind.NotNull => "check_not_null",
            IrCheckKind.HasValue => "check_has_value",
            IrCheckKind.NoMatch => "no_match",
            _ => throw new InvalidOperationException($"unexpected check {check.Kind}"),
        };
        return $"vpl::{name}({string.Join(", ", operands)})";
    }

    string ConstructText(IrConstruct construction)
    {
        var constructor = construction.Constructor;
        if (constructor.Symbol.IsImplicit)
            return DefaultValue(construction.Type);
        var name = constructor.Symbol.IsMemberwise ? MemberwiseName([.. construction.Arguments.Select(a => a is not IrFieldDefault)]) : "make";
        return $"{CppType(construction.Type)}::{name}({string.Join(", ", ArgumentsText(constructor, construction.Arguments, construction.Loc))})";
    }

    string CallText(IrCall call)
    {
        var method = call.Method;
        var function = method.Symbol;
        var arguments = string.Join(", ", ArgumentsText(method, call.Arguments, call.Loc));
        var typeArguments = TypeArgumentList(method.TypeArguments);
        // `.template` is required for a member template of a dependent type and harmless otherwise.
        var memberName = (typeArguments.Length > 0 ? "template " : "") + MethodName(function) + typeArguments;
        if (call.Receiver is not { } receiver)
        {
            if (function.BuiltinCppName is { } builtin)
                return $"{builtin}{typeArguments}({arguments})";
            if (function.ContainingType is null)
                return $"{FunctionName(function)}{typeArguments}({arguments})";
            return $"{ObjectType(method.Owner!)}::{memberName}({arguments})";
        }

        var receiverPlace = receiver switch
        {
            IrRead read => read.Place,
            IrRefArgument reference => reference.Place,
            _ => throw new InvalidOperationException($"unexpected receiver {receiver.GetType().Name}"),
        };
        var receiverType = receiverPlace.Type;
        var receiverText = receiver is IrRead value ? OperandText(value) : PlaceText(receiverPlace);
        // Formatted types satisfy a Printable bound without implementing it, so the runtime writes those itself.
        if (receiverType.IsTypeParameter && stdlib.IsPrintableToString(function))
            return $"vpl::to_string<{ObjectType(method.Owner!)}>({receiverText})";
        // So are the numbers, char, and String a Comparable one.
        if (receiverType.IsTypeParameter && stdlib.IsComparableCompareTo(function))
            return $"vpl::compare_to<{ObjectType(method.Owner!)}>({receiverText}, {arguments})";
        bool isThis = receiverPlace is { IsDeref: false, Projections.Count: 0, Local.IsThis: true };
        var target = isThis ? ThisReceiverText(method, call.IsSuperCall) : ReceiverText(receiverType, method, receiverText);
        // An extern struct's instance methods are members of its C++ class.
        if (function.IsExtern)
            return $"{target}{function.BuiltinCppName}({arguments})";
        return $"{target}{memberName}({arguments})";
    }

    /// <summary>A lambda's body as a C++ lambda's, printed at the indentation of the statement it is in.</summary>
    string LambdaText(IrFunction lambda, string introducer, IEnumerable<string> leading)
    {
        var parameters = leading.Concat(lambda.Parameters.Select(p => $"{ParameterType(p.Type, p.RefKind)} {Name(p)}"));
        var (enclosingOutput, enclosingIndent) = (output, indent);
        output = new StringBuilder();
        indent = enclosingIndent + 1;
        IrStatements(lambda.Body);
        var body = output.ToString();
        (output, indent) = (enclosingOutput, enclosingIndent);
        return $"{introducer}({string.Join(", ", parameters)}) -> {CppType(lambda.Symbol.ReturnType)} {{\n{body}{new string(' ', indent * 4)}}}";
    }

    /// <summary>
    /// A <c>vpl::Function</c> made by <c>vpl::closure</c>, which copies the captured values into a
    /// closure object and passes them to the body ahead of the arguments, or by
    /// <c>vpl::closure_in</c>, which makes the closure in its stack slot. The body is a C++ lambda
    /// capturing nothing, so it can't use anything it wasn't given.
    /// </summary>
    string ClosureText(IrClosure closure)
    {
        var function = LambdaText(closure.Function, "[]", closure.Function.Captures.Select(c => $"const {CppType(c.Type)}& {Name(c)}"));
        var values = closure.Captures.Select(OperandText);
        var signature = FunctionSignature(closure.Type);
        if (closure.Slot is { } slot)
            return $"vpl::closure_in<{signature}>({string.Join(", ", [Name(slot), function, .. values])})";
        return $"vpl::closure<{signature}, {CppStringLiteral(closure.Name)}>({string.Join(", ", [closure.IsCyclic ? "true" : "false", function, .. values])})";
    }

    string ClosureObjectType(IrClosure closure) =>
        $"vpl::Closure<{string.Join(", ", [CppStringLiteral(closure.Name), .. closure.Captures.Select(c => CppType(c.Type))])}>";

    /// <summary>A choice's option: a non-escaping local or parameter it refers to, which outlives it, or a value it holds.</summary>
    string OptionText(IrOperand option) =>
        option is IrRead { Ownership: IrOwnership.Borrow } ? $"vpl::borrowed({OperandText(option)})" : OperandText(option);
}
