using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.Analysis;

/// <summary>A function some code may run, and the type arguments it runs with.</summary>
public readonly record struct Callee(IrFunction Function, TypeMap Map);

/// <summary>The lowered functions some code may run, and whether it may also run an extern or builtin one.</summary>
public sealed record CalleeSet(IReadOnlyList<Callee> Functions, bool RunsExtern);

/// <summary>
/// The lowered program as the whole-program analyses see it: what each <see cref="IrRun"/> may
/// run. A constructor's field initializers and base call are in its own code, so each function
/// is one unit; a struct's memberwise constructor is one per form a call uses.
/// </summary>
public sealed class Callees
{
    readonly Overrides overrides;
    readonly CyclicClasses cycles;
    readonly IrFunctions functions;
    readonly List<(IrFunction Function, WylType Type)> lambdas = [];
    readonly Dictionary<WylType, IReadOnlyList<IrFunction>> lambdasOfType = [];
    readonly int generation;

    internal Callees(IrProgram program)
    {
        Program = program;
        generation = program.Generation;
        overrides = new Overrides(program.Bound);
        cycles = CyclicClasses.Find(program);
        functions = program.Functions;
        foreach (var function in program.Functions)
        {
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
        borrows = [.. borrowable is null ? [] : borrowable.Methods.Values.SelectMany(m => m).SelectMany(Targets).SelectMany(functions.Of)];
        released = [.. program.Functions.Where(f => f.Symbol.IsDestructor), .. borrows];
    }

    /// <summary>Every implementation of <c>Borrowable</c>'s methods.</summary>
    readonly IReadOnlyList<IrFunction> borrows;

    public IrProgram Program { get; }

    /// <summary>The methods a dispatched call may run.</summary>
    public Overrides Overrides => Fresh(overrides);

    /// <summary>Which classes and closures may be part of a cycle, and what releasing a value may destroy.</summary>
    public CyclicClasses Cycles => Fresh(cycles);

    /// <summary>Every function a release of an unknown value may run: the destructors, and every implementation of <c>Borrowable</c>.</summary>
    public IReadOnlyList<IrFunction> Released => Fresh(released);
    readonly IReadOnlyList<IrFunction> released;

    /// <summary>
    /// Throws if the program has changed since these facts were found from it. Every fact found
    /// from the callees asks through here, so no pass can be given facts of an earlier program.
    /// </summary>
    public T Fresh<T>(T answer) => Program.Generation == generation ? answer
        : throw new InvalidOperationException($"facts found from generation {generation} of the IR were asked at generation {Program.Generation}");

    /// <summary>The functions code may run, or null if it may run one that isn't lowered and isn't extern, whose code is unknown.</summary>
    public CalleeSet? Of(IrRun run)
    {
        Fresh(run);
        switch (run)
        {
            case IrRunMethod(var method, var direct):
            {
                var found = new List<Callee>();
                bool runsExtern = false;
                foreach (var target in direct || !method.Symbol.IsDispatched ? [method.Symbol] : Targets(method.Symbol))
                {
                    var lowered = functions.Of(target);
                    if (lowered.Count > 0)
                        found.AddRange(lowered.Select(f => new Callee(f, method.Map)));
                    else if (target.IsExtern || target.IsBuiltin)
                        runsExtern = true;
                    else if (!target.IsImplicit)
                        return null;
                }
                return new(found, runsExtern);
            }
            case IrRunFunctionValue(var type):
                return new([.. LambdasOfType(type).Select(f => new Callee(f, TypeMap.Empty))], false);
            case IrRunDefault(var type):
                return new([.. Defaults(type)], false);
            case IrRunRelease(var type):
                return new([.. Releasing(type)], false);
            default:
                throw new InvalidOperationException($"unexpected run {run}");
        }
    }

    /// <summary>
    /// The destructors, base classes' included, of the objects releasing a value of the type may
    /// destroy (<see cref="CyclicClasses.Destroyable"/>), and their classes' <c>Borrowable</c> methods.
    /// </summary>
    IEnumerable<Callee> Releasing(WylType? type)
    {
        if ((type is null ? null : cycles.Destroyable(type)) is not { } destroyed)
            return Released.Select(f => new Callee(f, TypeMap.Empty));
        var found = new List<Callee>();
        // A unique struct with a destructor runs it when a value of its type, or one holding it
        // inline, is released.
        if (type is not null)
        {
            foreach (var inline in type.InlineTypes())
            {
                if (inline.Symbol is { Kind: TypeKind.Struct, Destructor: { } destructor })
                    found.AddRange(functions.Of(destructor).Select(f => new Callee(f, TypeMap.For(inline))));
            }
        }
        foreach (var destroyedClass in destroyed)
        {
            for (var c = destroyedClass; c is not null; c = c.BaseType)
            {
                var map = TypeMap.For(c);
                if (c.Symbol!.Destructor is { } destructor)
                    found.AddRange(functions.Of(destructor).Select(f => new Callee(f, map)));
                found.AddRange(borrows.Where(f => f.Symbol.ContainingType == c.Symbol).Select(f => new Callee(f, map)));
            }
        }
        return found;
    }

    /// <summary>
    /// What an extern function runs that analyses must know of, as other runs: <c>std::memory</c>'s
    /// destroy values and make default ones, and its cycle collector destroys any garbage.
    /// </summary>
    public IReadOnlyList<IrRun> ExternRuns(MethodReference method)
    {
        Fresh(method);
        if (!StdlibSymbols.IsMemoryFunction(method.Symbol))
            return [];
        return method.Symbol.Name switch
        {
            "drop" => [new IrRunRelease(((PointerType)method.ParameterType(0)).Element)],
            "initializeDefault" => [new IrRunDefault(((PointerType)method.ParameterType(0)).Element)],
            "collectCycles" => [new IrRunRelease(null)],
            _ => [],
        };
    }

    /// <summary>The methods a dispatched call may run, except abstract ones, which run nothing.</summary>
    IEnumerable<FunctionSymbol> Targets(FunctionSymbol method) =>
        overrides.Targets(method).Where(f => !(f.IsAbstract || f.ContainingType?.Kind == TypeKind.Interface) || functions.Contains(f));

    /// <summary>The <c>make_default</c>s making the type's default value may run; a type parameter's may be any struct's.</summary>
    IEnumerable<Callee> Defaults(WylType type) =>
        type.IsTypeParameter ? functions.Defaults.Select(f => new Callee(f, TypeMap.Empty))
        : type is FixedArrayType array ? Defaults(array.Element)
        : type.Symbol is { } symbol && functions.Default(symbol) is { } found ? [new Callee(found, TypeMap.For(type))]
        : [];

    /// <summary>The lambdas a function value of the type may be (<see cref="FunctionValues.MayBe"/>).</summary>
    public IReadOnlyList<IrFunction> LambdasOfType(WylType type)
    {
        Fresh(type);
        if (!lambdasOfType.TryGetValue(type, out var found))
            lambdasOfType[type] = found = [.. lambdas.Where(l => FunctionValues.MayBe(type, l.Type)).Select(l => l.Function)];
        return found;
    }
}
