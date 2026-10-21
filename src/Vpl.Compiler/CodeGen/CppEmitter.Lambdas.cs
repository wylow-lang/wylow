using System.Text;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.CodeGen;

/// <remarks>
/// A lambda is a C++ lambda capturing by reference, which is safe because it can't outlive the
/// call it is passed to. A function-typed parameter is a template parameter, so the C++ optimizer
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

    /// <summary>Whether the function's function-typed parameters are template parameters, rather than <c>vpl::FunctionRef</c>s.</summary>
    bool TakesCallables(FunctionSymbol function) =>
        function.Parameters.Any(p => p.Type.IsFunction)
        && !function.IsDispatched && !function.ImplementsInterface
        && !function.IsConstructor && !function.IsLambda && !recursive.Contains(function);

    static string CallableTypeName(LocalSymbol parameter) => "vpl_F_" + parameter.Name;

    string FunctionTemplatePrefix(FunctionSymbol function)
    {
        IEnumerable<string> names = function.TypeParameters.Select(TypeParameterName);
        if (TakesCallables(function))
            names = names.Concat(function.Parameters.Where(p => p.Type.IsFunction).Select(CallableTypeName));
        var list = names.ToList();
        return list.Count == 0 ? "" : $"template <{string.Join(", ", list.Select(n => "class " + n))}> ";
    }

    string ParameterDeclaration(FunctionSymbol function, LocalSymbol parameter, VplType type) =>
        type.IsFunction && TakesCallables(function)
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
            return $"F{type.FunctionParameters.Count}{string.Concat(type.FunctionParameters.Select(p => Mode(p.RefKind) + Mangle(p.Type)))}R{Mangle(type.FunctionReturnType!)}";
        if (type.TypeParameter is { } parameter) return "T" + Name(parameter.Name);
        if (type.Symbol is { } s)
            return Name(s.Name) + (type.TypeArguments.Count > 0 ? $"I{string.Concat(type.TypeArguments.Select(Mangle))}E" : "");
        return Name(type.Name);
    }

    /// <summary>An argument for a <c>vpl::FunctionRef</c> parameter, converted explicitly so that C++ picks the overload VPL did.</summary>
    string FunctionArgument(MethodReference method, int index, string argument)
    {
        var type = method.ParameterType(index);
        return type.IsFunction && !TakesCallables(method.Symbol) ? $"vpl::FunctionRef<{FunctionSignature(type)}>({argument})" : argument;
    }

    IEnumerable<string> FunctionArguments(MethodReference method, IEnumerable<string> arguments) =>
        arguments.Select((argument, i) => FunctionArgument(method, i, argument));

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

        return [.. calls.Keys.Where(f => f.Parameters.Any(p => p.Type.IsFunction) && Reaches(f, f))];
    }

    string Lambda(BoundLambda lambda)
    {
        var function = lambda.Function;
        var parameters = string.Join(", ", function.Parameters.Select(p => $"{ParameterType(p.Type, p.RefKind)} {LocalName(p)}"));

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

        return $"[&]({parameters}) -> {CppType(function.ReturnType)} {{\n{bodyText}{new string(' ', indent * 4)}}}";
    }

    string Invocation(BoundInvocation invocation)
    {
        var operands = new List<Operand> { new(invocation.Callee, ByReference: true, PinRoot(invocation.Callee)) };
        operands.AddRange(invocation.Arguments.Select((argument, i) => ArgumentOperand(argument, invocation.Signature.ParameterRefKind(i))));
        return Ordered(operands, parts => $"{parts[0]}({string.Join(", ", parts.Skip(1))})");
    }
}
