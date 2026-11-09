using System.Text;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.CodeGen;

public sealed partial class CppEmitter
{
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
        computedLambdas++;
        var body = IndentedBody(() =>
        {
            IrStatements(block);
            Line($"return {ReturnedText(store.Value)};");
        });
        computedLambdas--;
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
                IrElement element when plan.Fused.TryGetValue(element, out var check) => $"wyl::At({text}, {OperandText(element.Index)}, {LocText(check.Loc!)})",
                IrElement { Index: IrConstant constant } => $"{text}[{ConstantText(constant)}]",
                IrElement element => $"{text}[static_cast<size_t>({OperandText(element.Index)})]",
                IrVariantField variant => $"{text}.{UnionMember(variant.Variant)}.{FieldName(variant.Field)}",
                IrAssumed => $"wyl::AssumeValue({text})",
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
    /// Whether a read of a whole place is a copied argument whose value the statement keeps: nesting
    /// it prints <c>wyl::Keep</c>, whose temporary lives to the end of the C++ statement.
    /// </summary>
    static bool KeptCopy(IrRead read, IrPlace place) =>
        read.Ownership == IrOwnership.Copy && place is { IsDeref: false, Projections.Count: 0 } && place.Local.Type.MayOwnObjects;

    /// <summary>
    /// A by-value argument. A copy of a temporary the statement keeps is passed through <c>wyl::Keep</c>,
    /// which makes the temporary the caller's, released when the statement completes.
    /// </summary>
    string ArgumentText(IrArgument argument) => argument switch
    {
        IrRefArgument reference => PlaceText(reference.Place),
        IrAddrArgument address => $"wyl::AddressOf({PlaceText(address.Place)})",
        IrRead read when KeptCopy(read, read.Place) && plan.Nested.ContainsKey(read.Place.Local) =>
            plan.Nested[read.Place.Local] is IrLet { Value: IrUse { Operand: IrRead place } }
                ? $"wyl::Keep({CppType(read.Type)}({PlaceText(place.Place)}))"
                : $"wyl::Keep({NestedText(read.Place.Local)})",
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
        IrChoose choice => $"wyl::Choose({string.Join(", ", [OperandText(choice.Index), .. choice.Options.Select(OptionText)])})",
        IrNew { Slot: null } creation => $"wyl::Adopt(new {ObjectType(creation.Type)}({string.Join(", ", ArgumentsText(creation.Constructor, creation.Arguments, creation.Loc))}))",
        IrNew { Slot: { } slot } creation => $"{Name(slot)}.Make({string.Join(", ", ArgumentsText(creation.Constructor, creation.Arguments, creation.Loc))})",
        IrConstruct construction => ConstructText(construction),
        IrVariant variant => $"{CppType(variant.Type)}::{VariantFactory(variant.Variant)}({string.Join(", ", variant.Fields.Select(OperandText))})",
        // Braces evaluate the elements left to right.
        IrArray array => $"{CppType(array.Type)}{{{string.Join(", ", array.Elements.Select(OperandText))}}}",
        IrDefault defaulted => DefaultValue(defaulted.Type),
        IrZeroed zeroed => Storage(zeroed.Type),
        IrRef reference => PlaceText(reference.Place),
        IrAddr address => $"wyl::AddressOf({PlaceText(address.Place)})",
        IrData data => $"const_cast<{CppType(data.Type)}>({PlaceText(data.Place)}.data())",
        IrTest test => TestText(test),
        // The function value is converted where it is used, which a template parameter may take as it is.
        IrConvert { Type.IsNonEscapingFunction: true } conversion => OperandText(conversion.Operand),
        IrConvert conversion when IsDowncast(conversion) => $"wyl::Downcast<{ObjectType(conversion.Type)}>({OperandText(conversion.Operand)})",
        IrConvert conversion => ConversionText(conversion.Operand.Type, conversion.Type, OperandText(conversion.Operand), PanicText(conversion.Loc)),
        IrHold hold => $"wyl::{(hold.Borrows ? "MakeBorrow" : "Pin")}({PlaceText(hold.Place)})",
        _ => throw new InvalidOperationException($"the IR emitter can't print {value.GetType().Name} yet"),
    };

    string TestText(IrTest test)
    {
        var operand = OperandText(test.Operand);
        return test.Kind switch
        {
            IrTestKind.NotNull when test.Operand.Type.IsPointer || test.Operand.Type.IsNullableReference => $"({operand} != nullptr)",
            IrTestKind.NotNull or IrTestKind.HasValue => $"wyl::HasValue({operand})",
            IrTestKind.Tag => $"({operand}.tag == {TagValue(test.Variant!)})",
            IrTestKind.Class => $"wyl::Is<{ObjectType(test.Target!)}>({operand})",
            _ => throw new InvalidOperationException($"the IR emitter can't print a {test.Kind} test yet"),
        };
    }

    /// <summary>A conversion of a reference to a class it may not refer to, which a type test has checked.</summary>
    static bool IsDowncast(IrConvert conversion) =>
        conversion.Type.IsReference && conversion.Operand.Type.NonNull.IsReference && !Conversions.IsSubtype(conversion.Operand.Type.NonNull, conversion.Type.NonNull, function: null);

    string CheckText(IrCheck check)
    {
        var operands = check.Operands.Select(OperandText).Append(LocText(check.Loc!));
        var name = check.Kind switch
        {
            IrCheckKind.Index => "CheckIndex",
            IrCheckKind.NotNull => "CheckNotNull",
            IrCheckKind.HasValue => "CheckHasValue",
            IrCheckKind.NoMatch => "NoMatch",
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
        bool isThis = receiverPlace is { IsDeref: false, Projections.Count: 0, Local.IsThis: true };
        var target = isThis ? ThisReceiverText(method, call.IsSuperCall) : ReceiverText(receiverType, method, receiverText);
        // An extern struct's instance methods are members of its C++ class, and a primitive's are
        // members of the wyl::Builtin that wraps it.
        if (function.IsExtern)
            return StdlibSymbols.IsHostedPrimitive(receiverType) ? $"wyl::Builtin({receiverText}).{function.BuiltinCppName}({arguments})" : $"{target}{function.BuiltinCppName}({arguments})";
        return $"{target}{memberName}({arguments})";
    }

    /// <summary>A lambda's body as a C++ lambda's, printed at the indentation of the statement it is in.</summary>
    string LambdaText(IrFunction lambda, string introducer, IEnumerable<string> leading)
    {
        var parameters = leading.Concat(lambda.Parameters.Select(p => $"{ParameterType(p.Type, p.RefKind)} {Name(p)}"));
        var body = IndentedBody(() =>
        {
            EmitParameterReleases(lambda.Parameters);
            IrStatements(lambda.Body);
        });
        return $"{introducer}({string.Join(", ", parameters)}) -> {CppType(lambda.Symbol.ReturnType)} {{\n{body}{new string(' ', indent * 4)}}}";
    }

    void EmitParameterReleases(IEnumerable<IrLocal> parameters)
    {
        foreach (var parameter in parameters.Where(HeldByValue))
        {
            if (parameter.Type.MayOwnObjects || parameter.Type.RunsDestructor)
                Line($"wyl::ReleaseAtEnd<{CppType(parameter.Type)}> {NewTemp()}{{{Name(parameter)}}};");
        }
    }

    static bool HeldByValue(IrLocal local) => local.Kind == IrLocalKind.Value
        || (local.Kind == IrLocalKind.Reference && local.Type.IsNullableReference);

    /// <summary>
    /// A <c>wyl::Function</c> made by <c>wyl::MakeClosure</c>, which copies the captured values into a
    /// closure object and passes them to the body ahead of the arguments, or by
    /// <c>wyl::MakeClosureIn</c>, which makes the closure in its stack slot. The body is a C++ lambda
    /// capturing nothing, so it can't use anything it wasn't given.
    /// </summary>
    string ClosureText(IrClosure closure)
    {
        var function = LambdaText(closure.Function, "[]", closure.Function.Captures.Select(c => $"{ConstReferenceType(c.Type)} {Name(c)}"));
        var values = closure.Captures.Select(OperandText);
        var signature = FunctionSignature((FunctionType)closure.Type);
        if (closure.Slot is { } slot)
            return $"wyl::MakeClosureIn<{signature}>({string.Join(", ", [Name(slot), function, .. values])})";
        if (closure.Type.IsShared)
            return $"wyl::MakeSharedClosure<{signature}, {CppStringLiteral(closure.Name)}>({string.Join(", ", [function, .. values])})";
        return $"wyl::MakeClosure<{signature}, {CppStringLiteral(closure.Name)}>({string.Join(", ", [cycles.IsCyclicClosure(closure.Function.Symbol) ? "true" : "false", function, .. values])})";
    }

    string ClosureObjectType(IrClosure closure) =>
        $"wyl::{(closure.Type.IsShared ? "SharedClosure" : "Closure")}<{string.Join(", ", [CppStringLiteral(closure.Name), .. closure.Captures.Select(c => CppType(c.Type))])}>";

    /// <summary>
    /// A choice's option: a non-escaping local or parameter it refers to, which outlives it, or a
    /// value it holds. A narrowed nullable one stays wrapped, since an option that isn't chosen may
    /// still be null.
    /// </summary>
    string OptionText(IrOperand option) => option switch
    {
        IrRead { Ownership: IrOwnership.Borrow, Type.IsNullableNonEscapingFunction: true } => $"wyl::MakeNarrowed({OperandText(option)})",
        IrRead { Ownership: IrOwnership.Borrow } => $"wyl::MakeBorrowed({OperandText(option)})",
        _ => OperandText(option),
    };
}
