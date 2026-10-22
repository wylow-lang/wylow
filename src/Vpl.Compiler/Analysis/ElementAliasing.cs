using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Analysis;

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

    readonly WholeProgram whole;
    readonly Summaries<Summary> summaries;
    readonly Summary destructors = new();
    readonly Dictionary<LocalSymbol, Location> aliasing = [];

    ElementAliasing(WholeProgram whole)
    {
        this.whole = whole;
        summaries = new Summaries<Summary>(whole.Units, () => new Summary(), s => s.Size);
    }

    public static ElementAliasing Find(WholeProgram whole)
    {
        var result = new ElementAliasing(whole);
        result.summaries.Solve(result.Collect);
        foreach (var unit in whole.Units.Where(u => u.Symbol.IsDestructor))
            Merge(result.destructors, result.summaries[unit.Symbol]!, TypeMap.Empty);
        foreach (var body in whole.Bodies)
            result.FindLoops(body);
        return result;
    }

    /// <summary>
    /// Whether a loop's element variable refers to the element in place rather than copying it,
    /// because nothing in the loop can store over the element.
    /// </summary>
    public bool AliasesElement(LocalSymbol element) => aliasing.ContainsKey(element);

    /// <summary>Where each loop element that refers to its element in place is declared.</summary>
    public IEnumerable<Location> Locations => aliasing.Values;

    /// <summary>
    /// A lambda's writes count where it may run, at each call of a function value of its type, not
    /// in the code that makes it: no other code runs a lambda.
    /// </summary>
    Summary Collect(Unit unit)
    {
        var collector = new Collector(this, unit.Symbol, new Summary());
        foreach (var statement in unit.Statements)
            collector.Statement(statement);
        foreach (var initializer in unit.Initializers)
            collector.Expression(initializer);
        if (unit.ImplicitBaseConstructor is { } baseConstructor)
            collector.Call(baseConstructor, direct: true);
        return collector.Summary;
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
        if (statement is BoundFor { Body: BoundBlock body })
            CheckLoopElement(body);
        foreach (var inner in BoundTree.InnerStatements(statement))
            FindLoops(inner);
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
            aliasing[element] = declaration.Location;
    }

    /// <summary>Whether a write may store over an element whose type is <paramref name="element"/>.</summary>
    static bool MayStoreOver(Write write, VplType element) =>
        (write.Element && IsElementOf(write.Type, element)) || ContainsElements(write.Type, element);

    static bool IsElementOf(VplType type, VplType element) => type.NonNull == element || type.NonNull.IsTypeParameter;

    /// <summary>Whether a value of the type may contain elements of type <paramref name="element"/>: a fixed array of them, directly or in a field.</summary>
    static bool ContainsElements(VplType type, VplType element) =>
        type.InlineTypes().Any(t => t.IsTypeParameter || (t.IsFixedArray && IsElementOf(t.ElementType!, element)));

    sealed class Collector(ElementAliasing analysis, FunctionSymbol? function, Summary summary)
    {
        public Summary Summary { get; } = summary;
        public HashSet<LocalSymbol> AssignedLocals { get; } = [];

        public void Statement(BoundStatement statement)
        {
            if (statement is BoundAssignment assignment)
                Assigned(assignment.Target);
            else if (statement is BoundSuperCall super)
                Call(super.Constructor, direct: true);
            foreach (var expression in BoundTree.OwnExpressions(statement))
                Expression(expression);
            foreach (var inner in BoundTree.InnerStatements(statement))
                Statement(inner);
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
                case BoundInvocation invocation:
                    foreach (var lambda in analysis.whole.LambdasOfType(invocation.Callee.Type.NonNull))
                        Merge(Summary, analysis.summaries[lambda]!, TypeMap.Empty);
                    break;
            }
            foreach (var child in BoundTree.Children(expression))
                Expression(child);
        }

        public void Call(MethodReference method, bool direct)
        {
            foreach (var target in analysis.whole.Targets(method.Symbol, direct))
            {
                if (analysis.summaries[target] is { } callee)
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
