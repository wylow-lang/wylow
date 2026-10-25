using Vpl.Compiler.Ir;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Analysis;

/// <summary>A function some code may run, and the type arguments it runs with.</summary>
public readonly record struct Callee(IrFunction Function, TypeMap Map);

/// <summary>
/// The lowered program as the whole-program analyses see it: what each <see cref="IrRun"/> may
/// run. A constructor's field initializers and base call are in its own code, so each function
/// is one unit; a struct's memberwise constructor is one per form a call uses.
/// </summary>
public sealed class Callees
{
    readonly Overrides overrides;
    readonly Dictionary<FunctionSymbol, List<IrFunction>> functions = [];
    readonly Dictionary<TypeSymbol, IrFunction> defaults = [];
    readonly List<(IrFunction Function, VplType Type)> lambdas = [];
    readonly Dictionary<VplType, IReadOnlyList<IrFunction>> lambdasOfType = [];

    public Callees(IrProgram program, Overrides overrides)
    {
        Program = program;
        this.overrides = overrides;
        foreach (var function in program.Functions)
        {
            // A struct's make_default is under a symbol of its own, which no call names.
            if (function is { Builds: not null, Symbol: { IsMemberwise: false, ContainingType: { } type } })
                defaults[type] = function;
            else if (!functions.TryGetValue(function.Symbol, out var list))
                functions[function.Symbol] = [function];
            else
                list.Add(function);
            foreach (var statement in IrWalk.Descendants(function.Body, intoLambdas: false))
            {
                switch (statement)
                {
                    case IrLet { Value: IrLambda lambda }:
                        lambdas.Add((lambda.Function, lambda.Type));
                        break;
                    case IrLet { Value: IrClosure closure }:
                        lambdas.Add((closure.Function, closure.Type));
                        break;
                }
            }
        }
        var borrowable = program.Bound.Stdlib.Borrowable;
        Released =
        [
            .. program.Functions.Where(f => f.Symbol.IsDestructor),
            .. borrowable is null ? [] : borrowable.Methods.Values.SelectMany(m => m).SelectMany(Targets).SelectMany(f => functions.GetValueOrDefault(f) ?? []),
        ];
    }

    public IrProgram Program { get; }

    /// <summary>Every function a release may run: the destructors, and every implementation of <c>Borrowable</c>.</summary>
    public IReadOnlyList<IrFunction> Released { get; }

    /// <summary>
    /// The functions code may run, or null if it may run a function that isn't lowered, whose code
    /// is unknown. An extern function counts as running nothing: the containers that use
    /// <c>Memory</c>'s unsafe functions panic rather than move or drop elements while borrowed.
    /// </summary>
    public IReadOnlyList<Callee>? Of(IrRun run)
    {
        switch (run)
        {
            case IrRunMethod(var method, var direct):
            {
                var found = new List<Callee>();
                foreach (var target in direct || !method.Symbol.IsDispatched ? [method.Symbol] : Targets(method.Symbol))
                {
                    if (functions.TryGetValue(target, out var lowered))
                        found.AddRange(lowered.Select(f => new Callee(f, method.Map)));
                    else if (!target.IsExtern && !target.IsBuiltin && !target.IsImplicit)
                        return null;
                }
                // An implicit struct constructor makes the default value.
                if (method.Symbol is { IsImplicit: true, ContainingType.Kind: TypeKind.Struct })
                    found.AddRange(Defaults(method.Owner!));
                return found;
            }
            case IrRunFunctionValue(var type):
                return [.. LambdasOfType(type).Select(f => new Callee(f, TypeMap.Empty))];
            case IrRunDefault(var type):
                return [.. Defaults(type)];
            default:
                return [.. Released.Select(f => new Callee(f, TypeMap.Empty))];
        }
    }

    /// <summary>The methods a dispatched call may run, except abstract ones, which run nothing.</summary>
    IEnumerable<FunctionSymbol> Targets(FunctionSymbol method) =>
        overrides.Targets(method).Where(f => !(f.IsAbstract || f.ContainingType?.Kind == TypeKind.Interface) || functions.ContainsKey(f));

    /// <summary>The <c>make_default</c>s making the type's default value may run; a type parameter's may be any struct's.</summary>
    IEnumerable<Callee> Defaults(VplType type) =>
        type.IsTypeParameter ? defaults.Values.Select(f => new Callee(f, TypeMap.Empty))
        : type.IsFixedArray ? Defaults(type.ElementType!)
        : type.Symbol is { } symbol && defaults.TryGetValue(symbol, out var found) ? [new Callee(found, TypeMap.For(type))]
        : [];

    /// <summary>The lambdas a function value of the type may be (<see cref="FunctionValues.MayBe"/>).</summary>
    public IReadOnlyList<IrFunction> LambdasOfType(VplType type)
    {
        if (!lambdasOfType.TryGetValue(type, out var found))
            lambdasOfType[type] = found = [.. lambdas.Where(l => FunctionValues.MayBe(type, l.Type)).Select(l => l.Function)];
        return found;
    }
}
