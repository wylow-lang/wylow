using System.Text;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.CodeGen;

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

    /// <summary>The hold making each hold declared before it, which an <c>is ref</c> name's holder is.</summary>
    readonly Dictionary<IrLocal, IrHold> declaredHolds = [];

    /// <summary>A lowered function's mem-initializer, if it is a class constructor calling its base's, and its body.</summary>
    void EmitIrFunction(IrFunction function)
    {
        // A name is chosen against the function it is used in: lowering puts one source local in
        // several functions, as a field initializer's binding is in every constructor form that leaves
        // its field out, and one form's parameter may have the name another form gave the binding.
        localNames.Clear();
        usedLocalNames.Clear();
        usedLocalNames.UnionWith(function.Symbol.Parameters.Select(LocalName));
        currentType = function.Symbol.ContainingType;
        irNames.Clear();
        continueLabels.Clear();
        closureSlots.Clear();
        declaredHolds.Clear();
        var statements = IrWalk.Descendants(function.Body).ToList();
        var declared = statements.OfType<IrDeclare>().Select(d => d.Local).Where(l => l.Kind == IrLocalKind.Hold).ToHashSet();
        foreach (var let in statements.OfType<IrLet>().Where(l => declared.Contains(l.Local)))
            declaredHolds[let.Local] = (IrHold)let.Value;
        foreach (var closure in IrWalk.Descendants(function.Body).OfType<IrLet>().Select(l => l.Value).OfType<IrClosure>())
        {
            if (closure.Slot is { } slot)
                closureSlots[slot] = closure;
        }
        if (function.Struct is { } made)
            irNames[made] = "self";
        PlanNesting(function, nest);
        if (function.ConstructorCall is { } constructorCall)
            Line($"    : {(NeedsArgumentsConstructor(function) ? ArgumentsConstructorText(function, constructorCall) : ConstructorCallText(constructorCall))}");
        Line("{");
        indent++;
        if (function.Symbol.IsConstructor && function.This is { } self && !function.Chains)
            Line($"this->wyl_set_cyclic({CyclicFlag(self.Type.Symbol!)});");
        IrStatements(function.Body);
        indent--;
        Line("}");
    }

    void IrStatements(IrBlock block) => IrStatements(block.Statements);

    void IrStatements(IReadOnlyList<IrStatement> statements)
    {
        IrStatement? previous = null;
        foreach (var statement in statements.Where(s => !plan.Consumed.Contains(s)))
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
            case IrLet { Value: IrHold hold } let when declaredHolds.ContainsKey(let.Local):
                Line($"wyl::hold({Name(let.Local)}, {PlaceText(hold.Place)});");
                break;
            case IrLet let:
                Line(Declaration(let.Local, RvalueText(let.Value), let.Value));
                break;
            case IrDeclare { Local.Kind: IrLocalKind.Slot } declare:
                var held = closureSlots.TryGetValue(declare.Local, out var closure) ? ClosureObjectType(closure) : ObjectType(declare.Local.Type);
                Line($"wyl::StackSlot<{held}> {Name(declare.Local)};");
                break;
            case IrDeclare { Local.Kind: IrLocalKind.Hold } declare:
                var type = declare.Local.Type;
                Line($"{(declaredHolds[declare.Local].Borrows ? $"std::optional<wyl::Borrow<{ObjectType(type)}>>" : CppType(type))} {Name(declare.Local)};");
                break;
            case IrDeclare declare when plan.Evaluated.TryGetValue(declare.Local, out var evaluated):
                Line($"auto {Name(declare.Local)} = [&] {{");
                indent++;
                IrStatements(evaluated.Block);
                Line($"return {ReturnedText(evaluated.Store.Value)};");
                indent--;
                Line("}();");
                break;
            case IrDeclare declare:
                Line($"{CppType(declare.Local.Type)} {Name(declare.Local)}{{}};");
                break;
            case IrEnd { Local.Kind: IrLocalKind.Slot } end:
                Line($"{Name(end.Local)}.end();");
                break;
            case IrEnd end:
                Line($"wyl::unhold({Name(end.Local)});");
                break;
            case IrStore or IrDo:
                Line(StatementText(statement) + ";");
                break;
            case IrCheck { NeverRuns: true }:
                break;
            case IrCheck check:
                Line(CheckText(check) + ";");
                break;
            case IrReturn ret:
                Line(ret.Value is { } value ? $"return {ReturnedText(value)};" : "return;");
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
                // A call that doesn't return is [[noreturn]] in C++ too, and so is `wyl::no_match`.
                if (previous is not (IrDo { Value: IrCall { Method.Symbol.IsNoReturn: true } } or IrCheck { Kind: IrCheckKind.NoMatch }))
                    Line($"wyl::unreachable({LocText(unreachable.Loc)});");
                break;
            default:
                throw new InvalidOperationException($"the IR emitter can't print {statement.GetType().Name} yet");
        }
    }

    /// <summary>
    /// A returned value. C++ moves a returned local itself, and <c>std::move</c> would prevent
    /// eliding the move, so a move that nesting forwards from a local is printed as that local.
    /// </summary>
    string ReturnedText(IrOperand value)
    {
        var current = value;
        while (current is IrRead { Ownership: IrOwnership.Move, Place: { IsDeref: false, Projections.Count: 0, Local: var local } })
        {
            if (!plan.Nested.TryGetValue(local, out var definition))
            {
                if (ValueVariable(local))
                    return Name(local);
                break;
            }
            // The temporary prints as what defines it, so the move it holds is a move of that. A
            // conversion that prints as its operand forwards one too.
            if (definition switch
                {
                    IrLet { Value: IrUse use } => use.Operand,
                    IrLet { Value: IrConvert conversion } when IsPassThrough(conversion.Operand.Type, conversion.Type) => conversion.Operand,
                    IrBlock block when !plan.Evaluated.ContainsKey(local) => StoredValue(block, local),
                    _ => null,
                } is not { } inner)
                break;
            current = inner;
        }
        return OperandText(current);
    }

    /// <summary>The value a block stores whole into a local it defines, if the last statement does.</summary>
    static IrOperand? StoredValue(IrBlock block, IrLocal local) =>
        block.Statements.LastOrDefault(s => s is IrStore store && IsWholeStore(store, local)) is IrStore store ? store.Value : null;

    /// <summary>The value a local declared without one is defined with: a block's last store, or a choice between two.</summary>
    string InitializedText(IrLocal local)
    {
        IrOperand Stored(IrBlock block) => StoredValue(block, local)!;
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
        if (body.FirstOrDefault() is IrIf head && TopLevel(head.Then).Count == 0)
        {
            bool exits = head.Else is { } exit && TopLevel(exit) is [IrBreak { Loop: var exited }] && exited == loop && DefinedBy(head) is null;
            if (exits)
            {
                condition = OperandText(head.Condition);
                body.RemoveAt(0);
            }
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
                continueLabels[loop] = $"wyl_continue_{tempCounter++}";
        }
        // The body is a block of its own, which the loop's braces can be once nothing follows it.
        if (body is [IrBlock only] && update.Count == 0 && !continueLabels.ContainsKey(loop))
            body = TopLevel(only);
        Line("{");
        indent++;
        IrStatements(body);
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
        if (!(text.StartsWith('(') && text.EndsWith(')')))
            return false;
        int depth = 0;
        bool quoted = false;
        bool parenthesized = true;
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
            {
                parenthesized = false;
                break;
            }
        }
        return parenthesized;
    }

    /// <summary>A store or evaluation as C++ without its semicolon, as a <c>for</c> loop's update.</summary>
    string StatementText(IrStatement statement) => statement switch
    {
        IrStore store => StoreText(store.Target.Type, PlaceText(store.Target), OperandText(store.Value)),
        IrDo run => run.Value is IrCall or IrNew or IrInvoke ? RvalueText(run.Value) : $"static_cast<void>({RvalueText(run.Value)})",
        _ => throw new InvalidOperationException($"unexpected statement {statement.GetType().Name}"),
    };

    static string StoreText(WylType type, string target, string value)
    {
        bool aggregate = type.IsAggregate || type.IsTypeParameter;
        bool plain = type.IsReference || !aggregate;
        return plain ? $"{target} = {value}" : $"wyl::assign({target}, {value})";
    }

    string Name(IrLocal local)
    {
        if (irNames.TryGetValue(local, out var name))
            return name;
        // A closure's copies get new names, so they don't shadow the variables they copy.
        name = local switch
        {
            { Role: IrLocalRole.Capture, Symbol: { } captured } => $"{LocalName(captured)}_{NewTemp()}",
            { Role: IrLocalRole.Capture } => $"wyl_this_{NewTemp()}",
            { Symbol: { } symbol } => LocalName(symbol),
            _ => NewTemp(),
        };
        return irNames[local] = name;
    }

    /// <summary>
    /// Whether the local is a C++ variable holding its value, not a reference to it. C++ moves such a
    /// returned local itself, so a move that a return forwards from one is printed as the local.
    /// </summary>
    static bool ValueVariable(IrLocal local) => local.Kind switch
    {
        // A nullable reference is held by value; other references are C++ references.
        IrLocalKind.Reference => local.Type.IsNullableReference,
        // A hold of a type parameter is an `auto&&`, which binds like a reference.
        IrLocalKind.Hold => !local.Type.IsTypeParameter,
        IrLocalKind.Value => true,
        _ => false,
    };

    string Declaration(IrLocal local, string value, IrRvalue? rvalue = null)
    {
        var name = Name(local);
        bool unusedByKind = local.Kind is IrLocalKind.Reference or IrLocalKind.Borrowed || local.Type.IsRefStruct;
        bool mayBeUnused = unusedByKind || plan.Uses.GetValueOrDefault(local) == 0;
        // A temporary nothing reads is still emitted for what making it does, as a switch's value is.
        var unused = mayBeUnused ? "[[maybe_unused]] " : "";
        // A reference local is a C++ reference to its value; an `in` one refers to it without changing it.
        var reference = local.RefKind == RefKind.In ? ConstReferenceType(local.Type) : CppType(local.Type) + "&";
        return local.Kind switch
        {
            // A non-escaping local owns the lambda or choice it is made with, or copies a stored function. Another
            // non-escaping local or parameter can't be reassigned and outlives it, so it is referred to instead.
            IrLocalKind.Value when local.Type.IsNonEscapingFunction => AliasOf(rvalue) is { } aliased && aliased.Type.IsNonEscapingFunction
                ? $"{unused}const auto& {name} = {value};"
                : $"{unused}auto {name} = {value};",
            IrLocalKind.Value => $"{unused}{CppType(local.Type)} {name} = {value};",
            IrLocalKind.Reference when local.Type.IsNullableReference => $"{unused}{CppType(local.Type)} {name} = {value};",
            IrLocalKind.Reference => $"{unused}{reference} {name} = {value};",
            IrLocalKind.Borrowed => $"{unused}{ConstReferenceType(local.Type)} {name} = {value};",
            IrLocalKind.Hold => $"{(local.Type.IsTypeParameter ? "auto&&" : "auto")} {name} = {value};",
            _ => throw new InvalidOperationException($"the IR emitter can't declare a {local.Kind} local yet"),
        };
    }

    /// <summary>The local a read refers to directly, if the value is that read and the local outlives it.</summary>
    static IrLocal? AliasOf(IrRvalue? rvalue)
    {
        if (rvalue is not IrUse { Operand: IrRead { Place: { IsDeref: false, Projections.Count: 0 } place } })
            return null;
        if (place.Local.IsTemporary)
            return null;
        return place.Local;
    }

    /// <summary>The value of a temporary printed where it is used.</summary>
    string NestedText(IrLocal local) => plan.Nested[local] switch
    {
        IrLet let => RvalueText(let.Value),
        IrBlock block when plan.Evaluated.TryGetValue(local, out var evaluated) => EvaluatedText(local, block, evaluated.Store),
        IrBlock or IrIf => InitializedText(local),
        var other => throw new InvalidOperationException($"unexpected definition {other.GetType().Name}"),
    };

    /// <summary>An immediately invoked lambda running the block, which returns what it stores last.</summary>
    string EvaluatedText(IrLocal local, IrBlock block, IrStore store)
    {
        var body = IndentedBody(() =>
        {
            IrStatements(block);
            Line($"return {ReturnedText(store.Value)};");
        });
        var type = local.Type.IsNonEscapingFunction ? "auto" : CppType(local.Type);
        return $"[&]() -> {type} {{\n{body}{new string(' ', indent * 4)}}}()";
    }

    /// <summary>A body's statements printed one indentation level deeper than the statement it is nested in.</summary>
    string IndentedBody(Action emit)
    {
        var (enclosingOutput, enclosingIndent) = (output, indent);
        output = new StringBuilder();
        indent = enclosingIndent + 1;
        emit();
        var body = output.ToString();
        (output, indent) = (enclosingOutput, enclosingIndent);
        return body;
    }

    string BaseText(IrLocal local) =>
        plan.Nested.ContainsKey(local) ? NestedText(local)
        : declaredHolds.TryGetValue(local, out var hold) && hold.Borrows ? $"(*{Name(local)})"
        : Name(local);

    string PlaceText(IrPlace place)
    {
        var local = place.Local;
        var projections = place.Projections;
        string text;
        int start = 0;
        if (!place.IsDeref)
        {
            if (local.IsThis && local.Type.IsReference)
                text = $"wyl::Ref<{ObjectType(local.Type)}>(this)";
            else if (local.IsThis && projections is [IrField first, ..])
                (text, start) = ($"this->{FieldName(first.Field)}", 1);
            else
                text = local.IsThis ? "(*this)" : BaseText(local);
        }
        else
        {
            var holder = local.IsThis ? "this" : BaseText(local);
            switch (local.Deref)
            {
                case IrDeref.Object when projections is [IrField field, ..]:
                    (text, start) = ($"{holder}->{FieldName(field.Field)}", 1);
                    break;
                case IrDeref.Referenced when !local.Type.IsNullableReference:
                case IrDeref.Generic:
                    text = holder;
                    break;
                case IrDeref.Pointee when projections is [IrElement element, ..]:
                    (text, start) = ($"{holder}[{OperandText(element.Index)}]", 1);
                    break;
                default:
                    text = $"(*{holder})";
                    break;
            }
        }
        foreach (var projection in projections.Skip(start))
        {
            text = projection switch
            {
                IrField field => $"{text}.{FieldName(field.Field)}",
                IrElement element when plan.Fused.TryGetValue(element, out var check) => $"wyl::at({text}, {OperandText(element.Index)}, {LocText(check.Loc!)})",
                IrElement { Index: IrConstant constant } => $"{text}[{ConstantText(constant)}]",
                IrElement element => $"{text}[static_cast<size_t>({OperandText(element.Index)})]",
                IrVariantField variant => $"{text}.{UnionMember(variant.Variant)}.{FieldName(variant.Field)}",
                IrAssumed => $"wyl::assume_value({text})",
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
            if (read.Ownership == IrOwnership.Move && read.Type.IsCounted)
                return $"std::move({Name(local)})";
        }
        if (read.Ownership == IrOwnership.Move && read.Type.IsCounted)
            return $"std::move({PlaceText(read.Place)})";
        return PlaceText(read.Place);
    }

    string ConstantText(IrConstant constant) => constant.Value switch
    {
        null when constant.Type == WylType.Null => "nullptr",
        null when constant.Type.IsPointer || constant.Type.IsNullableReference => $"static_cast<{CppType(constant.Type)}>(nullptr)",
        null => $"{CppType(constant.Type)}()",
        var value => Literal(constant.Type, value),
    };

    string LocText(IrLoc loc) => loc.Source is { } source ? SourceLocation(source) : CallerLocationParameter;

    string? PanicText(IrLoc? loc) => loc is null ? null : LocText(loc);

    /// <summary>
    /// A by-value argument. A copy of a temporary the statement keeps is passed through <c>wyl::keep</c>,
    /// which makes the temporary the caller's, released when the statement completes.
    /// </summary>
    string ArgumentText(IrArgument argument) => argument switch
    {
        IrRefArgument reference => PlaceText(reference.Place),
        IrAddrArgument address => $"wyl::address_of({PlaceText(address.Place)})",
        IrRead { Ownership: IrOwnership.Copy, Place: { IsDeref: false, Projections.Count: 0, Local: var local } } when plan.Nested.ContainsKey(local) && local.Type.MayOwnObjects =>
            plan.Nested[local] is IrLet { Value: IrUse { Operand: IrRead place } }
                ? $"wyl::keep({CppType(local.Type)}({PlaceText(place.Place)}))"
                : $"wyl::keep({NestedText(local)})",
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
        IrUnary unary => UnaryText(unary.Operator, OperandText(unary.Operand), PanicText(unary.Loc)),
        IrBinary binary => BinaryText(binary.Operator, binary.Left.Type, binary.Type, OperandText(binary.Left), OperandText(binary.Right), PanicText(binary.Loc)),
        IrCall call => CallText(call),
        IrInvoke invoke => $"{OperandText(invoke.Callee)}({string.Join(", ", invoke.Arguments.Select((a, i) => OptionalFunctionArgument(invoke.Signature.ParameterType(i), ArgumentText(a))))})",
        IrLambda lambda => LambdaText(lambda.Function, "[&]", []),
        IrClosure closure => ClosureText(closure),
        IrChoose choice => $"wyl::choose({string.Join(", ", [OperandText(choice.Index), .. choice.Options.Select(OptionText)])})",
        IrNew { Slot: null } creation => $"wyl::adopt(new {ObjectType(creation.Type)}({string.Join(", ", ArgumentsText(creation.Constructor, creation.Arguments, creation.Loc))}))",
        IrNew { Slot: { } slot } creation => $"{Name(slot)}.make({string.Join(", ", ArgumentsText(creation.Constructor, creation.Arguments, creation.Loc))})",
        IrConstruct construction => ConstructText(construction),
        IrVariant variant => $"{CppType(variant.Type)}::{VariantFactory(variant.Variant)}({string.Join(", ", variant.Fields.Select(OperandText))})",
        // Braces evaluate the elements left to right.
        IrArray array => $"{CppType(array.Type)}{{{string.Join(", ", array.Elements.Select(OperandText))}}}",
        IrDefault defaulted => DefaultValue(defaulted.Type),
        IrZeroed zeroed => $"{CppType(zeroed.Type)}{{}}",
        IrRef reference => PlaceText(reference.Place),
        IrAddr address => $"wyl::address_of({PlaceText(address.Place)})",
        IrData data => $"const_cast<{CppType(data.Type)}>({PlaceText(data.Place)}.data())",
        IrTest test => TestText(test),
        // The function value is converted where it is used, which a template parameter may take as it is.
        IrConvert { Type.IsNonEscapingFunction: true } conversion => OperandText(conversion.Operand),
        IrConvert conversion when IsDowncast(conversion) => $"wyl::downcast<{ObjectType(conversion.Type)}>({OperandText(conversion.Operand)})",
        IrConvert conversion => ConversionText(conversion.Operand.Type, conversion.Type, OperandText(conversion.Operand), PanicText(conversion.Loc)),
        IrHold hold => $"wyl::{(hold.Borrows ? "borrow" : "pin")}({PlaceText(hold.Place)})",
        _ => throw new InvalidOperationException($"the IR emitter can't print {value.GetType().Name} yet"),
    };

    string TestText(IrTest test)
    {
        var operand = OperandText(test.Operand);
        return test.Kind switch
        {
            IrTestKind.NotNull when test.Operand.Type.IsPointer || test.Operand.Type.IsNullableReference => $"({operand} != nullptr)",
            IrTestKind.NotNull or IrTestKind.HasValue => $"wyl::has_value({operand})",
            IrTestKind.Tag => $"({operand}.tag == {TagValue(test.Variant!)})",
            IrTestKind.Class => $"wyl::is<{ObjectType(test.Target!)}>({operand})",
            _ => throw new InvalidOperationException($"the IR emitter can't print a {test.Kind} test yet"),
        };
    }

    /// <summary>A conversion of a reference to a class it may not refer to, which a type test has checked.</summary>
    static bool IsDowncast(IrConvert conversion) =>
        conversion.Type.IsReference && conversion.Operand.Type.NonNull.IsReference && !Conversions.IsSubtype(conversion.Operand.Type.NonNull, conversion.Type.NonNull);

    string CheckText(IrCheck check)
    {
        var operands = check.Operands.Select(OperandText).Append(LocText(check.Loc!));
        var name = check.Kind switch
        {
            IrCheckKind.Index => "check_index",
            IrCheckKind.NotNull => "check_not_null",
            IrCheckKind.HasValue => "check_has_value",
            IrCheckKind.NoMatch => "no_match",
            _ => throw new InvalidOperationException($"unexpected check {check.Kind}"),
        };
        return $"wyl::{name}({string.Join(", ", operands)})";
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
            return $"wyl::to_string<{ObjectType(method.Owner!)}>({receiverText})";
        // So are the numbers, char, and String a Comparable one.
        if (receiverType.IsTypeParameter && stdlib.IsComparableCompareTo(function))
            return $"wyl::compare_to<{ObjectType(method.Owner!)}>({receiverText}, {arguments})";
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
        var body = IndentedBody(() => IrStatements(lambda.Body));
        return $"{introducer}({string.Join(", ", parameters)}) -> {CppType(lambda.Symbol.ReturnType)} {{\n{body}{new string(' ', indent * 4)}}}";
    }

    /// <summary>
    /// A <c>wyl::Function</c> made by <c>wyl::closure</c>, which copies the captured values into a
    /// closure object and passes them to the body ahead of the arguments, or by
    /// <c>wyl::closure_in</c>, which makes the closure in its stack slot. The body is a C++ lambda
    /// capturing nothing, so it can't use anything it wasn't given.
    /// </summary>
    string ClosureText(IrClosure closure)
    {
        var function = LambdaText(closure.Function, "[]", closure.Function.Captures.Select(c => $"{ConstReferenceType(c.Type)} {Name(c)}"));
        var values = closure.Captures.Select(OperandText);
        var signature = FunctionSignature((FunctionType)closure.Type);
        if (closure.Slot is { } slot)
            return $"wyl::closure_in<{signature}>({string.Join(", ", [Name(slot), function, .. values])})";
        return $"wyl::closure<{signature}, {CppStringLiteral(closure.Name)}>({string.Join(", ", [cycles.IsCyclicClosure(closure.Function.Symbol) ? "true" : "false", function, .. values])})";
    }

    string ClosureObjectType(IrClosure closure) =>
        $"wyl::Closure<{string.Join(", ", [CppStringLiteral(closure.Name), .. closure.Captures.Select(c => CppType(c.Type))])}>";

    /// <summary>
    /// A choice's option: a non-escaping local or parameter it refers to, which outlives it, or a
    /// value it holds. A narrowed nullable one stays wrapped, since an option that isn't chosen may
    /// still be null.
    /// </summary>
    string OptionText(IrOperand option) => option switch
    {
        IrRead { Ownership: IrOwnership.Borrow, Type.IsNullableNonEscapingFunction: true } => $"wyl::narrowed({OperandText(option)})",
        IrRead { Ownership: IrOwnership.Borrow } => $"wyl::borrowed({OperandText(option)})",
        _ => OperandText(option),
    };
}
