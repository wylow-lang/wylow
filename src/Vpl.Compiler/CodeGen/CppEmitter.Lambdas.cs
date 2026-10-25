using Vpl.Compiler.Analysis;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.CodeGen;

/// <remarks>
/// A non-escaping lambda is a C++ lambda capturing by reference, which is safe because it can't
/// outlive the call it is passed to; an escaping one is a <c>vpl::Function</c>, code to call and a
/// closure object holding copies of what it captures, if anything. A non-escaping function-typed
/// parameter is a template parameter, so the C++ optimizer can inline each lambda into the
/// function it is passed to. Where a template can't be used, it is a <c>vpl::FunctionRef</c>, which
/// calls the lambda indirectly: in an overridden virtual method or a class's implementation of an
/// interface method, which C++ can't make a template; in a recursive function that passes itself
/// a new lambda, which would instantiate itself without end; in a constructor sharing its
/// parameter count with another, whose name can't tell them apart; and in a function type's own
/// parameters.
/// </remarks>
public sealed partial class CppEmitter
{
    /// <summary>Functions with function-typed parameters that may call themselves.</summary>
    HashSet<FunctionSymbol> recursive = [];

    static bool IsNonEscaping(VplType type) => type.IsFunction && !type.IsEscaping;

    /// <summary>Whether the function's non-escaping function parameters are template parameters, rather than <c>vpl::FunctionRef</c>s.</summary>
    bool TakesCallables(FunctionSymbol function) =>
        function.Parameters.Any(p => IsNonEscaping(p.Type))
        && (!function.IsDispatched || unoverridden.Contains(function)) && !function.ImplementsInterface
        && !(function.IsConstructor && HasSameCountConstructor(function)) && !function.IsLambda && !recursive.Contains(function);

    /// <summary>
    /// Constructors of a type share their C++ name, and a template parameter would accept any
    /// argument, so C++ could pick a different one of the same parameter count than VPL did.
    /// </summary>
    static bool HasSameCountConstructor(FunctionSymbol constructor) =>
        constructor.ContainingType!.Constructors.Any(c => c != constructor && c.Parameters.Count == constructor.Parameters.Count);

    /// <summary>Virtual methods with function parameters that nothing overrides, which are emitted as ordinary methods so that they can be templates.</summary>
    HashSet<FunctionSymbol> unoverridden = [];

    static HashSet<FunctionSymbol> FindUnoverridden(WholeProgram whole) =>
    [
        .. whole.Program.Types.SelectMany(t => t.Methods.Values.SelectMany(m => m))
            .Where(m => m is { IsVirtual: true, IsAbstract: false, ImplementsInterface: false }
                && m.Parameters.Any(p => IsNonEscaping(p.Type)) && whole.Overrides.Targets(m).Count == 1),
    ];

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
    /// Overloads that differ only in their function types would be the same C++ template, and ones
    /// that differ only in <c>nint</c> and <c>long</c> (or <c>nuint</c> and <c>ulong</c>) the same
    /// C++ function on 64-bit targets. So a function with such parameters gets a suffix that encodes
    /// them, if it may have such an overload (<see cref="FindOverloaded"/>).
    /// </summary>
    string OverloadSuffix(FunctionSymbol function)
    {
        if (!overloaded.Contains(function))
            return "";
        var parts = function.Parameters.Select((p, i) => p.Type.IsFunction || MentionsPointerSized(p.Type) ? $"{i}{Mangle(p.Type)}" : "").ToList();
        return parts.Any(p => p.Length > 0) ? "_" + string.Concat(parts) : "";
    }

    static bool MentionsPointerSized(VplType type) =>
        type == VplType.NInt || type == VplType.NUInt
        || (type.IsNullable && MentionsPointerSized(type.NonNull))
        || (type.ReferencedType is { } referenced && MentionsPointerSized(referenced))
        || (type.ElementType is { } element && MentionsPointerSized(element))
        || type.TypeArguments.Any(MentionsPointerSized);

    /// <summary>Functions and methods that share their name and parameter count with another.</summary>
    HashSet<FunctionSymbol> overloaded = [];

    /// <summary>
    /// Grouped across the whole program, so that a method and the methods that override or implement
    /// it, which share their name and parameter count, are named alike.
    /// </summary>
    static HashSet<FunctionSymbol> FindOverloaded(BoundProgram program) =>
    [
        .. program.Functions.Select(f => f.Symbol).Where(f => f.ContainingType is null)
            .Concat(program.Types.SelectMany(t => t.Methods.Values.SelectMany(m => m)))
            .Distinct()
            .GroupBy(f => (f.Name, f.Parameters.Count))
            .Where(g => g.Count() > 1)
            .SelectMany(g => g),
    ];

    /// <summary>A name for a type made only of identifier characters, which no other type has.</summary>
    static string Mangle(VplType type)
    {
        static string Name(string name) => $"{name.Length}{name}";
        static string Mode(RefKind kind) => kind switch { RefKind.Ref => "r", RefKind.In => "i", _ => "v" };
        if (type.IsNullable) return "N" + Mangle(type.NonNull);
        if (type.IsNullableReference) return "Q" + Mode(type.ReferenceKind) + Mangle(type.ReferencedType!);
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

    static bool IsNullableNonEscaping(VplType type) => type.IsNullable && IsNonEscaping(type.NonNull);

    /// <summary>
    /// An argument for a nullable non-escaping parameter, a <c>vpl::Optional</c> of a
    /// <c>vpl::FunctionRef</c>. It is made at the call, from the value before its conversion, rather
    /// than where the argument is evaluated, which may be a statement of its own that a lambda made
    /// in it wouldn't outlive. Any other argument is passed as it is.
    /// </summary>
    string OptionalFunctionArgument(VplType parameter, string argument) =>
        IsNullableNonEscaping(parameter) ? $"vpl::optional_ref<{FunctionSignature(parameter.NonNull)}>({argument})" : argument;

    /// <summary>
    /// The functions with function-typed parameters that a template would instantiate without end:
    /// those on a cycle of calls where one call passes a function parameter something whose C++
    /// type is made by the caller, such as a lambda or a choice. A function that only passes its own
    /// parameters on (or escaping values, whose type is fixed) instantiates the same template again.
    /// </summary>
    static HashSet<FunctionSymbol> FindRecursive(WholeProgram whole)
    {
        var calls = new Dictionary<FunctionSymbol, HashSet<FunctionSymbol>>();
        var growing = new List<(FunctionSymbol Caller, FunctionSymbol Callee)>();
        foreach (var function in whole.Program.Functions)
        {
            var code = BoundTree.AllExpressions(function.Body)
                .Concat(whole.UnitOf(function.Symbol)!.Initializers.SelectMany(BoundTree.AllExpressions));
            // A function value's call has no callee of its own here; the lambdas it may run are part of their creators.
            var edges = code.Where(e => e is not BoundInvocation).Select(BoundTree.CallOf).OfType<CallSite>()
                .Select(c => (Callee: c.Method.Symbol, c.Arguments)).ToList();
            if (function.Body.Statements.FirstOrDefault() is BoundSuperCall super)
                edges.Add((super.Constructor.Symbol, super.Arguments));
            calls[function.Symbol] = [.. edges.Select(e => e.Callee)];
            growing.AddRange(edges.Where(e => e.Arguments.Where((a, i) => i < e.Callee.Parameters.Count && IsNonEscaping(e.Callee.Parameters[i].Type)).Any(MakesCallableType))
                .Select(e => (function.Symbol, e.Callee)));
        }

        bool Reaches(FunctionSymbol from, FunctionSymbol target)
        {
            var seen = new HashSet<FunctionSymbol>();
            var pending = new Stack<FunctionSymbol>([from]);
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

        return [.. calls.Keys.Where(f => f.Parameters.Any(p => IsNonEscaping(p.Type)) && growing.Any(e => Reaches(f, e.Caller) && Reaches(e.Callee, f)))];
    }

    static bool MakesCallableType(BoundExpression argument) =>
        argument is not (BoundLocalAccess { Local.IsParameter: true } or { Type.IsEscaping: true } or BoundConversion { Operand.Type.IsEscaping: true });
}
