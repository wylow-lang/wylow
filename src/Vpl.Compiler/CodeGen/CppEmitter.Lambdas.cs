using System.Globalization;
using System.Text;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.CodeGen;

/// <remarks>
/// A non-escaping lambda is a C++ lambda capturing by reference, which is safe because it can't
/// outlive the call it is passed to; an escaping one is a <c>vpl::Function</c>, a reference to a
/// closure object holding copies of what it captures. A non-escaping function-typed parameter is
/// a template parameter, so the C++ optimizer
/// can inline each lambda into the function it is passed to. Where a template can't be used, it is
/// a <c>vpl::FunctionRef</c>, which calls the lambda indirectly: in a virtual method or a class's
/// implementation of an interface method, which C++ can't make a template; in a recursive function, which could instantiate itself without end by
/// passing itself a new lambda; in a constructor, whose name can't tell overloads apart; and in a
/// function type's own parameters.
/// </remarks>
public sealed partial class CppEmitter
{
    /// <summary>Functions with function-typed parameters that may call themselves.</summary>
    HashSet<FunctionSymbol> recursive = [];

    int lambdaDepth;

    /// <summary>Inside a closure's body, the parameter holding the <c>this</c> it captured.</summary>
    string? capturedThis;

    static bool IsNonEscaping(VplType type) => type.IsFunction && !type.IsEscaping;

    /// <summary>Whether the function's non-escaping function parameters are template parameters, rather than <c>vpl::FunctionRef</c>s.</summary>
    bool TakesCallables(FunctionSymbol function) =>
        function.Parameters.Any(p => IsNonEscaping(p.Type))
        && !function.IsDispatched && !function.ImplementsInterface
        && !function.IsConstructor && !function.IsLambda && !recursive.Contains(function);

    static string CallableTypeName(LocalSymbol parameter) => "vpl_F_" + parameter.Name;

    string FunctionTemplatePrefix(FunctionSymbol function)
    {
        IEnumerable<string> names = function.TypeParameters.Select(TypeParameterName);
        if (TakesCallables(function))
            names = names.Concat(function.Parameters.Where(p => IsNonEscaping(p.Type)).Select(CallableTypeName));
        var list = names.ToList();
        return list.Count == 0 ? "" : $"template <{string.Join(", ", list.Select(n => "class " + n))}> ";
    }

    string ParameterDeclaration(FunctionSymbol function, LocalSymbol parameter, VplType type) =>
        IsNonEscaping(type) && TakesCallables(function)
            ? $"const {CallableTypeName(parameter)}& {LocalName(parameter)}"
            : $"{ParameterType(type, parameter.RefKind)} {LocalName(parameter)}";

    static string FunctionSignature(VplType type) =>
        $"{CppType(type.FunctionReturnType!)}({string.Join(", ", type.FunctionParameters.Select(p => ParameterType(p.Type, p.RefKind)))})";

    /// <summary>
    /// Overloads that differ only in their function types would be the same C++ template, so a
    /// function with function-typed parameters gets a suffix that encodes them.
    /// </summary>
    static string OverloadSuffix(FunctionSymbol function)
    {
        var parts = function.Parameters.Select((p, i) => p.Type.IsFunction ? $"{i}{Mangle(p.Type)}" : "").ToList();
        return parts.Any(p => p.Length > 0) ? "_" + string.Concat(parts) : "";
    }

    /// <summary>A name for a type made only of identifier characters, which no other type has.</summary>
    static string Mangle(VplType type)
    {
        static string Name(string name) => $"{name.Length}{name}";
        static string Mode(RefKind kind) => kind switch { RefKind.Ref => "r", RefKind.In => "i", _ => "v" };
        if (type.IsNullable) return "N" + Mangle(type.NonNull);
        if (type.IsPointer) return "P" + Mangle(type.ElementType!);
        if (type.IsFixedArray) return $"A{type.FixedLength}_" + Mangle(type.ElementType!);
        if (type.IsFunction)
            return $"{(type.IsEscaping ? "E" : "")}F{type.FunctionParameters.Count}{string.Concat(type.FunctionParameters.Select(p => Mode(p.RefKind) + Mangle(p.Type)))}R{Mangle(type.FunctionReturnType!)}";
        if (type.TypeParameter is { } parameter) return "T" + Name(parameter.Name);
        if (type.Symbol is { } s)
            return Name(s.Name) + (type.TypeArguments.Count > 0 ? $"I{string.Concat(type.TypeArguments.Select(Mangle))}E" : "");
        return Name(type.Name);
    }

    /// <summary>An argument for a <c>vpl::FunctionRef</c> parameter, converted explicitly so that C++ picks the overload VPL did.</summary>
    string FunctionArgument(MethodReference method, int index, string argument)
    {
        var type = method.ParameterType(index);
        if (IsNullableNonEscaping(type))
            return OptionalFunctionArgument(type, argument);
        return IsNonEscaping(type) && !TakesCallables(method.Symbol) ? $"vpl::FunctionRef<{FunctionSignature(type)}>({argument})" : argument;
    }

    IEnumerable<string> FunctionArguments(MethodReference method, IEnumerable<string> arguments) =>
        arguments.Select((argument, i) => FunctionArgument(method, i, argument));

    static bool IsNullableNonEscaping(VplType type) => type.IsNullable && IsNonEscaping(type.NonNull);

    /// <summary>
    /// An argument for a nullable non-escaping parameter, a <c>vpl::Optional</c> of a
    /// <c>vpl::FunctionRef</c>. It is made here, from the value <see cref="PassedValue"/> gives,
    /// rather than where the argument is evaluated, which may be a statement of its own
    /// (<see cref="Ordered"/>) that a lambda made in it wouldn't outlive.
    /// </summary>
    string OptionalFunctionArgument(VplType type, string argument) =>
        $"vpl::optional_ref<{FunctionSignature(type.NonNull)}>({argument})";

    /// <summary>What is evaluated for an argument: for a nullable non-escaping parameter, the function or nullable before its conversion.</summary>
    static BoundExpression PassedValue(VplType parameter, BoundExpression argument) =>
        IsNullableNonEscaping(parameter) && argument is BoundConversion { Operand: var value } && value.Type != VplType.Null ? value : argument;

    /// <summary>The functions with function-typed parameters that can reach themselves through calls.</summary>
    static HashSet<FunctionSymbol> FindRecursive(BoundProgram program)
    {
        var calls = new Dictionary<FunctionSymbol, HashSet<FunctionSymbol>>();
        foreach (var function in program.Functions)
        {
            IEnumerable<BoundExpression> code = BoundTree.AllExpressions(function.Body);
            if (function.Symbol is { IsConstructor: true, ContainingType: { } owner })
                code = code.Concat(owner.Fields.Select(f => f.Initializer).OfType<BoundExpression>().SelectMany(BoundTree.AllExpressions));
            var callees = code.Select(e => e switch
            {
                BoundCall call => call.Method.Symbol,
                BoundNew creation => creation.Constructor.Symbol,
                BoundConstruction construction => construction.Constructor.Symbol,
                _ => null,
            }).OfType<FunctionSymbol>().ToHashSet();
            if (function.Body.Statements.FirstOrDefault() is BoundSuperCall super)
                callees.Add(super.Constructor.Symbol);
            calls[function.Symbol] = callees;
        }

        bool Reaches(FunctionSymbol from, FunctionSymbol target)
        {
            var seen = new HashSet<FunctionSymbol>();
            var pending = new Stack<FunctionSymbol>(calls.GetValueOrDefault(from) ?? []);
            while (pending.TryPop(out var next))
            {
                if (next == target)
                    return true;
                if (seen.Add(next))
                {
                    foreach (var callee in calls.GetValueOrDefault(next) ?? [])
                        pending.Push(callee);
                }
            }
            return false;
        }

        return [.. calls.Keys.Where(f => f.Parameters.Any(p => IsNonEscaping(p.Type)) && Reaches(f, f))];
    }

    string Lambda(BoundLambda lambda)
    {
        if (lambda.Function.IsEscaping)
            return Closure(lambda);
        return $"[&]({LambdaParameters(lambda.Function)}) -> {CppType(lambda.Function.ReturnType)} {LambdaBody(lambda)}";
    }

    string LambdaParameters(FunctionSymbol function) =>
        string.Join(", ", function.Parameters.Select(p => $"{ParameterType(p.Type, p.RefKind)} {LocalName(p)}"));

    string LambdaBody(BoundLambda lambda)
    {
        var function = lambda.Function;
        var (enclosingOutput, enclosingIndent, enclosingFunction) = (output, indent, currentFunction);
        output = new StringBuilder();
        indent = enclosingIndent + 1;
        currentFunction = function;
        lambdaDepth++;
        foreach (var statement in lambda.Body.Statements)
            Statement(statement);
        if (function.ReturnType != VplType.Void)
        {
            var body = lambda.Body.Location;
            Line($"vpl::unreachable({SourceLocation(body with { Span = new TextSpan(body.Span.End - 1, 1) })});");
        }
        var bodyText = output.ToString();
        lambdaDepth--;
        (output, indent, currentFunction) = (enclosingOutput, enclosingIndent, enclosingFunction);
        return $"{{\n{bodyText}{new string(' ', indent * 4)}}}";
    }

    /// <summary>
    /// An escaping lambda is a closure object made by <c>vpl::closure</c>, which copies the captured
    /// values, evaluated here, and passes them to the body ahead of the arguments. The body is a C++
    /// lambda capturing nothing, so it can't use anything it wasn't given. Its parameters for the
    /// captured values get new names, so they don't shadow the variables they copy.
    /// </summary>
    string Closure(BoundLambda lambda)
    {
        var (locals, self) = BoundTree.Captures(lambda);
        var values = new List<string>();
        var parameters = new List<string>();
        var renamed = new List<(LocalSymbol Local, string Name)>();
        foreach (var local in locals.Select(a => a.Local))
        {
            var outer = LocalName(local);
            values.Add(outer);
            var inner = $"{outer}_{NewTemp()}";
            parameters.Add($"const {CppType(local.Type)}& {inner}");
            renamed.Add((local, outer));
            localNames[local] = inner;
        }
        var enclosingThis = capturedThis;
        if (self is not null)
        {
            values.Add(This());
            capturedThis = $"vpl_this_{NewTemp()}";
            parameters.Add($"const {CppType(currentType!.Type)}& {capturedThis}");
        }
        parameters.AddRange(lambda.Function.Parameters.Select(p => $"{ParameterType(p.Type, p.RefKind)} {LocalName(p)}"));
        var body = LambdaBody(lambda);
        capturedThis = enclosingThis;
        foreach (var (local, outer) in renamed)
            localNames[local] = outer;

        bool cyclic = self is not null || locals.Any(a => MayHoldReferences(a.Local.Type));
        var (line, column) = lambda.Location.LineColumn;
        var name = CppStringLiteral($"lambda at {lambda.Location.Source.FileName}:{line}:{column}");
        var function = $"[]({string.Join(", ", parameters)}) -> {CppType(lambda.Function.ReturnType)} {body}";
        return $"vpl::closure<{FunctionSignature(lambda.Type)}>({(cyclic ? "true" : "false")}, {name}, {string.Join(", ", [function, .. values])})";
    }

    /// <summary>
    /// A non-escaping function chosen by <c>?:</c> or a switch expression is a <c>vpl::Choice</c> of
    /// every function it could be (<see cref="BoundTree.FunctionOptions"/>), with the conditions and
    /// cases evaluated, in order, to the index of the chosen one. Its lambdas and non-escaping locals
    /// are made up front, which no one can observe, since neither making the one nor copying the
    /// other has effects. An escaping option, such as a field or a call, is evaluated only when it is
    /// chosen, into the one slot that all of them share (<c>vpl::choose_held</c>). A switch's cases
    /// run in a C++ lambda, so the binder keeps the lambdas chosen from out of what a case declares.
    /// </summary>
    string Choice(BoundExpression choice)
    {
        static bool IsHeld(BoundExpression option) => option is BoundConversion { Operand.Type.IsEscaping: true };
        var leaves = BoundTree.FunctionOptions(choice).ToList();
        var held = leaves.Any(IsHeld) ? NewTemp() : null;
        var heldIndex = leaves.Count(o => !IsHeld(o)).ToString(CultureInfo.InvariantCulture);
        var options = new List<string>();
        string Index(BoundExpression expression)
        {
            switch (expression)
            {
                case BoundConditional nested:
                    return $"({Expression(nested.Condition)} ? {Index(nested.WhenTrue)} : {Index(nested.WhenFalse)})";
                case BoundSwitchExpression nested:
                    return SwitchExpression(nested, "int", Index);
                case BoundLambda or BoundLocalAccess:
                    options.Add(Expression(expression));
                    return (options.Count - 1).ToString(CultureInfo.InvariantCulture);
                case BoundConversion conversion when IsHeld(conversion):
                    return $"({held} = {Expression(conversion.Operand)}, {heldIndex})";
                default:
                    throw new InvalidOperationException($"a chosen function cannot be a {expression.GetType().Name}");
            }
        }
        var index = Index(choice);
        if (held is null)
            return $"vpl::choose({index}, {string.Join(", ", options)})";
        var signature = FunctionSignature(choice.Type);
        return $"vpl::choose_held<{signature}>([&](vpl::Function<{signature}>& {held}) {{ return {index}; }}{string.Concat(options.Select(o => ", " + o))})";
    }

    string Invocation(BoundInvocation invocation)
    {
        // A closure stored where the call could replace it is held until the call returns, as an object receiver is.
        var callee = invocation.Callee;
        var operands = new List<Operand> { new(callee, ByReference: true, PinRoot(callee), PinValue: callee.Type.IsEscaping && !IsStable(callee)) };
        operands.AddRange(invocation.Arguments.Select((argument, i) => ArgumentOperand(invocation.Signature, i, argument)));
        var signature = invocation.Signature;
        return Ordered(operands, parts => $"{parts[0]}({string.Join(", ", parts.Skip(1).Select((p, i) =>
            IsNullableNonEscaping(signature.ParameterType(i)) ? OptionalFunctionArgument(signature.ParameterType(i), p) : p))})");
    }
}
