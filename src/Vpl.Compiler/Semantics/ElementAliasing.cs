using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Semantics;

/// <summary>
/// Finds for-each loops whose element variable can refer to the element in place instead of
/// holding its own reference to the element's object, which saves a reference count increment and
/// decrement per element. What a loop iterates is held and borrowed until the loop ends, so the
/// element's object can only be destroyed by storing over the element. Each function is summarized
/// by the types of storage it may write, including through the functions it calls, and a loop
/// qualifies if nothing its body may run, nor any destructor, can store over an element of its type.
/// </summary>
/// <remarks>
/// Writes through <c>Memory</c>'s unsafe functions are not counted: the containers that use them
/// already panic rather than move or drop elements while borrowed.
/// </remarks>
public sealed class ElementAliasing
{
    const int MaxRounds = 32;
    const int MaxWrites = 64;

    /// <summary>
    /// A type of storage that may be written. An <see cref="Element"/> write is to an element, or to
    /// a place a reference or pointer leads to, which may be an element; any other write is to a
    /// variable or field, which is only an element's storage through a fixed array it contains.
    /// </summary>
    readonly record struct Write(VplType Type, bool Element);

    sealed class Summary
    {
        public HashSet<Write> Writes { get; } = [];
        public bool Unknown { get; set; }

        public int Size => Unknown ? int.MaxValue : Writes.Count;

        public void Add(Write write)
        {
            if (!Unknown && Writes.Add(write) && Writes.Count > MaxWrites)
                Unknown = true;
        }
    }

    /// <summary>Code summarized together: a function's body, plus the field initializers a constructor runs.</summary>
    sealed record Unit(FunctionSymbol Symbol, IReadOnlyList<BoundStatement> Statements, IReadOnlyList<BoundExpression> Expressions);

    readonly BoundProgram program;
    readonly Dictionary<FunctionSymbol, Unit> units = [];
    readonly Dictionary<FunctionSymbol, Summary> summaries = [];
    readonly ILookup<(string, int), FunctionSymbol> overridesByName;
    readonly Summary destructors = new();

    public ElementAliasing(BoundProgram program)
    {
        this.program = program;
        foreach (var function in program.Functions)
            units[function.Symbol] = new Unit(function.Symbol, [function.Body], InitializersRunBy(function.Symbol));
        foreach (var type in program.Types.Where(t => !units.ContainsKey(t.DefaultConstructor)))
            units[type.DefaultConstructor] = new Unit(type.DefaultConstructor, [], InitializersRunBy(type.DefaultConstructor));
        overridesByName = program.Functions.Select(f => f.Symbol).Where(f => f.IsInstanceMethod)
            .ToLookup(f => (f.Name, f.Parameters.Count));
    }

    /// <summary>Marks every loop element variable that can refer to its element in place.</summary>
    public void Run()
    {
        Summarize();
        foreach (var unit in units.Values.Where(u => u.Symbol.IsDestructor))
            Merge(destructors, summaries[unit.Symbol], TypeMap.Empty);
        foreach (var function in program.Functions)
            FindLoops(function.Body);
    }

    static IReadOnlyList<BoundExpression> InitializersRunBy(FunctionSymbol function) =>
        function.IsConstructor && function.ContainingType is { } type
            ? [.. type.Fields.Select(f => f.Initializer).OfType<BoundExpression>()]
            : [];

    /// <summary>Summarizes every unit, repeating until no summary grows; recursion makes that take several rounds.</summary>
    void Summarize()
    {
        foreach (var symbol in units.Keys)
            summaries[symbol] = new Summary();
        for (int round = 0; ; round++)
        {
            bool changed = false;
            foreach (var unit in units.Values)
            {
                var summary = Collect(unit.Symbol, unit.Statements, unit.Expressions);
                var old = summaries[unit.Symbol];
                if (summary.Size != old.Size)
                {
                    summaries[unit.Symbol] = summary;
                    changed = true;
                }
            }
            if (!changed)
                return;
            if (round == MaxRounds)
            {
                foreach (var summary in summaries.Values)
                    summary.Unknown = true;
                return;
            }
        }
    }

    Summary Collect(FunctionSymbol? function, IEnumerable<BoundStatement> statements, IEnumerable<BoundExpression> expressions)
    {
        var collector = new Collector(this, function, new Summary());
        foreach (var statement in statements)
            collector.Statement(statement);
        foreach (var expression in expressions)
            collector.Expression(expression);
        if (function is { IsConstructor: true, ContainingType: { } type, Declaration: null } && type.BaseType is { } baseType)
            collector.Call(BaseConstructor(type, baseType), direct: true);
        return collector.Summary;
    }

    /// <summary>The base constructor an implicit constructor calls, which takes no arguments.</summary>
    static MethodReference BaseConstructor(TypeSymbol type, VplType baseType)
    {
        var symbol = baseType.Symbol!;
        var constructor = symbol.Constructors.FirstOrDefault(c => c.Parameters.Count == 0) ?? symbol.DefaultConstructor;
        return new MethodReference(constructor, baseType, []);
    }

    static void Merge(Summary into, Summary from, TypeMap map)
    {
        if (from.Unknown)
            into.Unknown = true;
        foreach (var write in from.Writes)
            into.Add(write with { Type = map.Apply(write.Type) });
    }

    void FindLoops(BoundStatement statement)
    {
        switch (statement)
        {
            case BoundBlock block:
                foreach (var s in block.Statements)
                    FindLoops(s);
                break;
            case BoundIf branch:
                FindLoops(branch.Then);
                if (branch.Else is not null)
                    FindLoops(branch.Else);
                break;
            case BoundWhile loop:
                FindLoops(loop.Body);
                break;
            case BoundForRange range:
                FindLoops(range.Body);
                break;
            case BoundSwitch choice:
                foreach (var switchCase in choice.Cases)
                    FindLoops(switchCase.Body);
                break;
            case BoundFor loop:
                if (loop.Body is BoundBlock body)
                    CheckLoopElement(body);
                FindLoops(loop.Body);
                break;
        }
    }

    void CheckLoopElement(BoundBlock body)
    {
        if (body.Statements.OfType<BoundVariableDeclaration>().FirstOrDefault(d => d.Local.IsLoopElement) is not { } declaration)
            return;
        var element = declaration.Local;
        if (!element.Type.IsReference || declaration.Initializer is not (BoundIndex or BoundCall) || declaration.Initializer.Type != element.Type)
            return;

        var collector = new Collector(this, null, new Summary());
        foreach (var statement in body.Statements)
            collector.Statement(statement);
        if (collector.AssignedLocals.Contains(element))
            return;
        var summary = collector.Summary;
        Merge(summary, destructors, TypeMap.Empty);
        if (!summary.Unknown && !summary.Writes.Any(w => MayStoreOver(w, element.Type.NonNull)))
            element.AliasesElement = true;
    }

    /// <summary>Whether a write may store over an element whose type is <paramref name="element"/>.</summary>
    static bool MayStoreOver(Write write, VplType element) =>
        (write.Element && (write.Type.NonNull == element || write.Type.NonNull.IsTypeParameter)) || ContainsElements(write.Type, element, []);

    /// <summary>Whether a value of the type may contain elements of type <paramref name="element"/>: a fixed array of them, directly or in a field.</summary>
    static bool ContainsElements(VplType type, VplType element, HashSet<VplType> visiting)
    {
        if (type.IsNullableValue)
            return ContainsElements(type.NonNull, element, visiting);
        if (type.IsTypeParameter)
            return true;
        if (type.IsFixedArray)
            return type.ElementType!.NonNull == element || type.ElementType.NonNull.IsTypeParameter || ContainsElements(type.ElementType, element, visiting);
        if (!type.IsStructOrEnum || !visiting.Add(type))
            return false;
        var map = TypeMap.For(type);
        return type.Symbol!.ValueFields.Any(f => ContainsElements(map.Apply(f.Type), element, visiting));
    }

    sealed class Collector(ElementAliasing analysis, FunctionSymbol? function, Summary summary)
    {
        public Summary Summary { get; } = summary;
        public HashSet<LocalSymbol> AssignedLocals { get; } = [];

        public void Statement(BoundStatement statement)
        {
            switch (statement)
            {
                case BoundBlock block:
                    foreach (var s in block.Statements)
                        Statement(s);
                    break;
                case BoundVariableDeclaration declaration:
                    Expression(declaration.Initializer);
                    break;
                case BoundAssignment assignment:
                    Assigned(assignment.Target);
                    Expression(assignment.Target);
                    Expression(assignment.Value);
                    break;
                case BoundExpressionStatement expression:
                    Expression(expression.Expression);
                    break;
                case BoundIf branch:
                    Expression(branch.Condition);
                    Statement(branch.Then);
                    if (branch.Else is not null)
                        Statement(branch.Else);
                    break;
                case BoundWhile loop:
                    Expression(loop.Condition);
                    Statement(loop.Body);
                    break;
                case BoundFor loop:
                    if (loop.Initializer is not null)
                        Statement(loop.Initializer);
                    Expression(loop.Condition);
                    if (loop.Update is not null)
                        Statement(loop.Update);
                    Statement(loop.Body);
                    break;
                case BoundForRange range:
                    Expression(range.Start);
                    Expression(range.End);
                    Statement(range.Body);
                    break;
                case BoundReturn ret:
                    Expression(ret.Value);
                    break;
                case BoundSwitch choice:
                    Expression(choice.Value);
                    foreach (var switchCase in choice.Cases)
                        Statement(switchCase.Body);
                    break;
                case BoundSuperCall super:
                    Call(super.Constructor, direct: true);
                    foreach (var argument in super.Arguments)
                        Expression(argument);
                    break;
            }
        }

        public void Expression(BoundExpression? expression)
        {
            switch (expression)
            {
                case null:
                    return;
                case BoundCall call:
                    Call(call.Method, call.IsSuperCall);
                    break;
                case BoundNew creation:
                    Call(creation.Constructor, direct: true);
                    break;
                case BoundConstruction construction:
                    Call(construction.Constructor, direct: true);
                    break;
            }
            foreach (var child in BoundTree.Children(expression))
                Expression(child);
        }

        public void Call(MethodReference method, bool direct)
        {
            var symbol = method.Symbol;
            IEnumerable<FunctionSymbol> targets = direct || !symbol.IsDispatched
                ? [symbol]
                : [symbol, .. analysis.overridesByName[(symbol.Name, symbol.Parameters.Count)]];
            foreach (var target in targets)
            {
                if (analysis.summaries.TryGetValue(target, out var callee))
                    Merge(Summary, callee, method.Map);
            }
        }

        void Assigned(BoundExpression target)
        {
            // A constructor's own object is new, so its storage cannot be an element anything iterates.
            if (function is { IsConstructor: true } && IsRootedAtThis(target))
                return;
            switch (target)
            {
                case BoundLocalAccess { Local.RefKind: RefKind.None } local:
                    AssignedLocals.Add(local.Local);
                    Summary.Add(new Write(target.Type, Element: false));
                    break;
                case BoundLocalAccess or BoundIndex or BoundCall or BoundThis:
                    Summary.Add(new Write(target.Type, Element: true));
                    break;
                default:
                    Summary.Add(new Write(target.Type, Element: false));
                    break;
            }
        }

        static bool IsRootedAtThis(BoundExpression place) => place switch
        {
            BoundThis => true,
            BoundFieldAccess field => field.Receiver is BoundThis || (!field.Receiver.Type.IsReference && IsRootedAtThis(field.Receiver)),
            BoundIndex { Target.Type.IsFixedArray: true } index => IsRootedAtThis(index.Target),
            _ => false,
        };
    }
}
