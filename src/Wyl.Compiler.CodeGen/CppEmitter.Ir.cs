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
    /// <summary>The function whose body is being printed, for the rules a return needs.</summary>
    IrFunction? emitting;
    int computedLambdas;
    /// <summary>The hold making each hold declared before it, which an <c>is ref</c> name's holder is.</summary>
    readonly Dictionary<IrLocal, IrHold> declaredHolds = [];
    /// <summary>The in-use guards declared before the <c>let</c> that makes them, for a binding whose scope is an enclosing block.</summary>
    readonly HashSet<IrLocal> declaredGuards = [];

    /// <summary>A lowered function's mem-initializer, if it is a class constructor calling its base's, and its body.</summary>
    void EmitIrFunction(IrFunction function)
    {
        emitting = function;
        // A name is chosen against the function it is used in: lowering puts one source local in
        // several functions, as a field initializer's binding is in every constructor form that leaves
        // its field out, and one form's parameter may have the name another form gave the binding.
        localNames.Clear();
        usedLocalNames.Clear();
        usedLocalNames.UnionWith(function.Symbol.Parameters.Select(LocalName));
        if (function.Symbol.ReceiverParameter is { } receiver)
            usedLocalNames.Add(LocalName(receiver));
        currentType = function.Symbol.ContainingType;
        irNames.Clear();
        continueLabels.Clear();
        closureSlots.Clear();
        declaredHolds.Clear();
        declaredGuards.Clear();
        var statements = IrWalk.Descendants(function.Body).ToList();
        var declared = statements.OfType<IrDeclare>().Select(d => d.Local).Where(l => l.Kind == IrLocalKind.Hold).ToHashSet();
        foreach (var let in statements.OfType<IrLet>().Where(l => declared.Contains(l.Local)))
            declaredHolds[let.Local] = (IrHold)let.Value;
        declaredGuards.UnionWith(statements.OfType<IrDeclare>().Select(d => d.Local).Where(l => l.Kind == IrLocalKind.InUse));
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
        EmitParameterReleases(function.Parameters);
        // A moved-from or discarded unique struct has no value for its destructor body to run on.
        if (function.Symbol is { IsDestructor: true, ContainingType.Kind: TypeKind.Struct })
        {
            Line("if ((wyl_flags_ & wyl::ENGAGED) == 0) return;");
            Line("if (GetInUse()) wyl::RaisePanic(\"cannot release a value that is in use\", \"<runtime>\");");
        }
        if (function.Symbol.IsConstructor && function.This is { } self && !function.Chains && self.Type.Symbol is { IsShared: false })
            Line($"this->SetCyclic({CyclicFlag(self.Type.Symbol!)});");
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
            case IrLet { Local.Kind: IrLocalKind.InUse } let:
                var mark = PlaceText(((IrInUse)let.Value).Place);
                Line(declaredGuards.Contains(let.Local)
                    ? $"{Name(let.Local)}.Begin({mark});"
                    : $"wyl::InUseMark {Name(let.Local)}({mark});");
                break;
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
                Line($"wyl::Hold({Name(let.Local)}, {PlaceText(hold.Place)});");
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
            case IrDeclare { Local.Kind: IrLocalKind.InUse } declare:
                Line($"wyl::InUseMark {Name(declare.Local)};");
                break;
            case IrDeclare declare when plan.Evaluated.TryGetValue(declare.Local, out var evaluated):
                Line($"auto {Name(declare.Local)} = [&] {{");
                indent++;
                computedLambdas++;
                IrStatements(evaluated.Block);
                Line($"return {ReturnedText(evaluated.Store.Value)};");
                computedLambdas--;
                indent--;
                Line("}();");
                break;
            case IrDeclare declare:
                bool storagePlaceholder = declare.Local.Kind == IrLocalKind.Value
                    && (!declare.Local.Type.IsDefaultable || declare.Local.Type.RunsDestructor || declare.Local.Type.IsTypeParameter);
                Line($"{CppType(declare.Local.Type)} {Name(declare.Local)}{(storagePlaceholder ? $" = {Storage(declare.Local.Type)}" : "{}")};");
                break;
            case IrEnd { Local.Kind: IrLocalKind.Slot } end:
                Line($"{Name(end.Local)}.End();");
                break;
            case IrEnd { Local.Kind: IrLocalKind.InUse } end:
                Line($"{Name(end.Local)}.End();");
                break;
            case IrEnd end:
                Line($"wyl::Unhold({Name(end.Local)});");
                break;
            case IrStore or IrDo:
                Line(StatementText(statement) + ";");
                break;
            case IrCheck { NeverRuns: true }:
                break;
            case IrCheck check:
                Line(CheckText(check) + ";");
                break;
            case IrDiscard discard:
                Line($"{Name(discard.Local)}.Discard();");
                break;
            case IrReturn ret:
                // A struct constructor's value is storage until it is returned, so its destructor
                // body may not run before then.
                if (emitting is { Symbol.IsConstructor: true, Struct: { } made } && made.Type.Symbol?.Destructor is not null
                    && ret.Value is IrRead { Ownership: IrOwnership.Move, Place: { IsDeref: false, Projections.Count: 0 } place } && place.Local == made)
                    Line($"{Name(made)}.Engage();");
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
                // A call that doesn't return is [[noreturn]] in C++ too, and so is `wyl::NoMatch`.
                if (previous is not (IrDo { Value: IrCall { Method.Symbol.IsNoReturn: true } } or IrCheck { Kind: IrCheckKind.NoMatch }))
                    Line($"wyl::Unreachable({LocText(unreachable.Loc)});");
                break;
            default:
                throw new InvalidOperationException($"the IR emitter can't print {statement.GetType().Name} yet");
        }
    }

    /// <summary>
    /// A returned value. C++ moves a returned local or parameter itself, and <c>std::move</c>
    /// would prevent eliding the move, so a move that nesting forwards from one is printed as it.
    /// </summary>
    string ReturnedText(IrOperand value)
    {
        var current = value;
        while (current is IrRead { Ownership: IrOwnership.Move, Place: { IsDeref: false, Projections.Count: 0, Local: var local } })
        {
            if (!plan.Nested.TryGetValue(local, out var definition))
            {
                bool declaredHere = local.Role is IrLocalRole.Variable or IrLocalRole.Temporary or IrLocalRole.Parameter;
                if (ValueVariable(local) && declaredHere && computedLambdas == 0)
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
        return plain ? $"{target} = {value}" : $"wyl::Assign({target}, {value})";
    }

    string Name(IrLocal local)
    {
        if (staticNames.TryGetValue(local, out var staticName))
            return staticName;
        if (irNames.TryGetValue(local, out var name))
            return name;
        // A closure's copies get new names, so they don't shadow the variables they copy.
        name = local switch
        {
            { Role: IrLocalRole.Capture, Symbol: { } captured } => $"{LocalName(captured)}_{NewTemp()}",
            { Role: IrLocalRole.Capture } => $"wyl_this_{NewTemp()}",
            // A name a guard moves from is a reference while the guard runs and a value when it
            // matches, so its reference needs a C++ name of its own.
            { Symbol: { } guarded, Kind: IrLocalKind.Borrowed or IrLocalKind.Reference } => FreshLocalName(guarded),
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
}
