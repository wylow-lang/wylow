using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Analysis;

/// <summary>
/// Code summarized together: a function's body, plus the field initializers a constructor runs and
/// the base constructor an implicit constructor calls.
/// </summary>
public sealed record Unit(
    FunctionSymbol Symbol,
    IReadOnlyList<BoundStatement> Statements,
    IReadOnlyList<BoundExpression> Initializers,
    MethodReference? ImplicitBaseConstructor);

/// <summary>
/// The program as the whole-program analyses see it: a unit of code for each function, implicit
/// constructor, and lambda, and the functions each call may run.
/// </summary>
public sealed class WholeProgram
{
    readonly Dictionary<FunctionSymbol, Unit> units = [];
    readonly Dictionary<VplType, IReadOnlyList<FunctionSymbol>> lambdasOfType = [];

    public WholeProgram(BoundProgram program)
    {
        Program = program;
        foreach (var function in program.Functions)
            units[function.Symbol] = ConstructorUnit(function.Symbol, [function.Body]);
        foreach (var type in program.Types.Where(t => !units.ContainsKey(t.DefaultConstructor)))
            units[type.DefaultConstructor] = ConstructorUnit(type.DefaultConstructor, []);
        Lambdas =
        [
            .. program.Functions.SelectMany(f => BoundTree.AllExpressions(f.Body))
                .Concat(program.Types.SelectMany(t => t.Fields).Select(f => f.Initializer).OfType<BoundExpression>().SelectMany(BoundTree.AllExpressions))
                .OfType<BoundLambda>()
                .Distinct(),
        ];
        foreach (var lambda in Lambdas)
            units[lambda.Function] = new Unit(lambda.Function, [lambda.Body], [], null);
        Overrides = new Overrides(program);
    }

    public BoundProgram Program { get; }

    public Overrides Overrides { get; }

    /// <summary>
    /// Every lambda in the program, including those in lambdas and field initializers. Each is a
    /// unit of its own, which a call of a function value of its type may run.
    /// </summary>
    public IReadOnlyList<BoundLambda> Lambdas { get; }

    public IEnumerable<Unit> Units => units.Values;

    public Unit? UnitOf(FunctionSymbol function) => units.GetValueOrDefault(function);

    /// <summary>The bodies of every function and lambda.</summary>
    public IEnumerable<BoundBlock> Bodies => [.. Program.Functions.Select(f => f.Body), .. Lambdas.Select(l => l.Body)];

    Unit ConstructorUnit(FunctionSymbol function, IReadOnlyList<BoundStatement> statements)
    {
        if (!function.IsConstructor || function.ContainingType is not { } type)
            return new Unit(function, statements, [], null);
        var initializers = type.Fields.Select(f => f.Initializer).OfType<BoundExpression>().ToList();
        var baseConstructor = function.Declaration is null && type.BaseType is { } baseType ? BaseConstructor(baseType) : null;
        return new Unit(function, statements, initializers, baseConstructor);
    }

    /// <summary>The base constructor an implicit constructor calls, which takes no arguments.</summary>
    static MethodReference BaseConstructor(VplType baseType)
    {
        var symbol = baseType.Symbol!;
        var constructor = symbol.Constructors.FirstOrDefault(c => c.Parameters.Count == 0) ?? symbol.DefaultConstructor;
        return new MethodReference(constructor, baseType, []);
    }

    /// <summary>The lambdas a function value of the type may be (<see cref="FunctionValues.MayBe"/>).</summary>
    public IReadOnlyList<FunctionSymbol> LambdasOfType(VplType type)
    {
        if (!lambdasOfType.TryGetValue(type, out var found))
            lambdasOfType[type] = found = [.. Lambdas.Where(l => FunctionValues.MayBe(type, l.Type)).Select(l => l.Function)];
        return found;
    }

    /// <summary>
    /// The functions a call may run: the method, or if the call dispatches, also everything
    /// overriding or implementing it. Abstract methods are left out, since they run nothing. The
    /// rest have units, except extern and builtin functions.
    /// </summary>
    public IEnumerable<FunctionSymbol> Targets(FunctionSymbol method, bool direct) =>
        (direct || !method.IsDispatched ? [method] : Overrides.Targets(method))
            .Where(f => units.ContainsKey(f) || !(f.IsAbstract || f.ContainingType?.Kind == TypeKind.Interface));
}
