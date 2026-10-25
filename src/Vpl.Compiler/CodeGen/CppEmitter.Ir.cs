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

    void EmitIrBody(IrFunction function)
    {
        irNames.Clear();
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
            case IrBlock block when InitializedBy(block) is { } local:
                Line(Declaration(local, OperandText(plan.Initialized[local].Value)));
                break;
            case IrBlock block:
                bool braces = TopLevel(block).Any(s => s is IrLet or IrDeclare || s is IrBlock inner && InitializedBy(inner) is not null);
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
                Line(Declaration(let.Local, RvalueText(let.Value)));
                break;
            case IrDeclare declare:
                Line($"{CppType(declare.Local.Type)} {Name(declare.Local)}{{}};");
                break;
            case IrStore store:
                Line(StoreText(store.Target.Type, PlaceText(store.Target), OperandText(store.Value)) + ";");
                break;
            case IrDo run:
                Line((run.Value is IrCall or IrNew or IrInvoke ? RvalueText(run.Value) : $"static_cast<void>({RvalueText(run.Value)})") + ";");
                break;
            case IrCheck check:
                Line(CheckText(check) + ";");
                break;
            case IrReturn ret:
                Line(ret.Value is null ? "return;" : $"return {OperandText(ret.Value)};");
                break;
            case IrUnreachable unreachable:
                // A call that doesn't return is [[noreturn]] in C++ too.
                if (previous is not IrDo { Value: IrCall { Method.Symbol.IsNoReturn: true } })
                    Line($"vpl::unreachable({LocText(unreachable.Loc)});");
                break;
            default:
                throw new InvalidOperationException($"the IR emitter can't print {statement.GetType().Name} yet");
        }
    }

    IrLocal? InitializedBy(IrBlock block) => plan.Initialized.FirstOrDefault(i => i.Value.Block == block).Key;

    string Name(IrLocal local)
    {
        if (irNames.TryGetValue(local, out var name))
            return name;
        name = local.Symbol is { } symbol ? LocalName(symbol) : NewTemp();
        return irNames[local] = name;
    }

    string Declaration(IrLocal local, string value)
    {
        var name = Name(local);
        var unused = local.Symbol is not null && (local.Kind is IrLocalKind.Reference or IrLocalKind.Borrowed || local.Type.IsRefStruct) ? "[[maybe_unused]] " : "";
        return local.Kind switch
        {
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
        IrBlock => OperandText(plan.Initialized[local].Value),
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
        IrNew { Slot: null } creation => $"vpl::adopt(new {ObjectType(creation.Type)}({string.Join(", ", ArgumentsText(creation.Constructor, creation.Arguments, creation.Loc))}))",
        IrConstruct construction => ConstructText(construction),
        IrVariant variant => $"{CppType(variant.Type)}::{VariantFactory(variant.Variant)}({string.Join(", ", variant.Fields.Select(OperandText))})",
        // Braces evaluate the elements left to right.
        IrArray array => $"{CppType(array.Type)}{{{string.Join(", ", array.Elements.Select(OperandText))}}}",
        IrDefault defaulted => DefaultValue(defaulted.Type),
        IrRef reference => PlaceText(reference.Place),
        IrAddr address => $"vpl::address_of({PlaceText(address.Place)})",
        IrData data => $"const_cast<{CppType(data.Type)}>({PlaceText(data.Place)}.data())",
        IrTest test => TestText(test),
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
            _ => throw new InvalidOperationException($"the IR emitter can't print a {test.Kind} test yet"),
        };
    }

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
}
