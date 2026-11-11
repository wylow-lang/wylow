using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.Analysis;

/// <summary>
/// Finds the classes whose objects may be part of a cycle of strong references: those that can
/// reach an object of their own class through fields, including through structs, enums, fixed
/// arrays, and the elements of standard library containers, counting every class a field's type
/// could refer to. Only their objects are buffered and visited by the cycle collector. Generic
/// classes are decided per instantiation, since that is what the C++ instantiates. An escaping
/// lambda's closures are decided the same way, as objects holding what the lowered lambda captures,
/// and a function value may hold the closure of any lambda of its type.
/// </summary>
/// <remarks>
/// The program's instantiations are those the binder constructed, closed over the types of their
/// fields. Generic code can still instantiate others that only C++ sees, through a generic
/// function's type arguments; the runtime treats those as cyclic, and a field whose type could
/// refer to one is assumed to reach every class and closure.
/// </remarks>
public sealed class CyclicClasses
{
    readonly StdlibSymbols stdlib;
    readonly IReadOnlyList<TypeSymbol> types;
    readonly List<WylType> classes = [];
    readonly Dictionary<WylType, HashSet<WylType>> held = [];
    readonly HashSet<WylType> cyclic = [];
    readonly IReadOnlyList<(FunctionSymbol Lambda, WylType Type)> closures;
    readonly Dictionary<FunctionSymbol, HashSet<WylType>> captured = [];
    readonly HashSet<FunctionSymbol> cyclicClosures = [];
    readonly Dictionary<WylType, IReadOnlySet<WylType>?> destroyable = [];

    CyclicClasses(IrProgram program)
    {
        stdlib = program.Bound.Stdlib;
        types = program.Bound.Types;
        // A lambda in a field initializer is lowered into each constructor, alike.
        var made = program.Functions.SelectMany(f => IrWalk.Descendants(f.Body, intoLambdas: false))
            .OfType<IrLet>().Select(l => l.Value).OfType<IrClosure>()
            .DistinctBy(c => c.Function.Symbol).ToList();
        closures = [.. made.Select(c => (c.Function.Symbol, c.Type))];
        foreach (var closure in made)
        {
            var references = captured[closure.Function.Symbol] = [];
            foreach (var capture in closure.Function.Captures)
                Hold(capture.Type, references);
        }
    }

    /// <summary>Every class instantiation the program is known to create objects of.</summary>
    public IReadOnlyList<WylType> Classes => classes;

    public bool IsCyclic(WylType classType) => cyclic.Contains(classType.CppIdentity);

    /// <summary>Whether the closures of an escaping lambda may be part of a cycle; one the analysis didn't see is assumed to be, and a shared one never is.</summary>
    public bool IsCyclicClosure(FunctionSymbol lambda) =>
        !lambda.IsShared && (!captured.ContainsKey(lambda) || cyclicClosures.Contains(lambda));

    internal static CyclicClasses Find(IrProgram program)
    {
        var result = new CyclicClasses(program);
        result.Discover();
        result.MarkCycles();
        return result;
    }

    void Discover()
    {
        var pending = new Queue<WylType>();
        void Add(WylType type)
        {
            type = type.CppIdentity;
            if (type.IsClass && !type.ContainsTypeParameters && !held.ContainsKey(type))
            {
                held[type] = [];
                classes.Add(type);
                pending.Enqueue(type);
            }
        }

        foreach (var s in types.Where(s => s.Kind == TypeKind.Class))
        {
            foreach (var instance in s.Instances.ToList())
                Add(instance);
        }
        foreach (var (lambda, _) in closures)
        {
            foreach (var reference in captured[lambda])
                Add(reference);
        }
        while (pending.TryDequeue(out var type))
        {
            var references = held[type];
            for (var t = type; t is not null; t = t.BaseType)
            {
                var map = TypeMap.For(t);
                foreach (var field in t.Symbol!.Fields)
                    Hold(map.Apply(field.Type), references);
            }
            foreach (var element in stdlib.OwnedElements(type))
                Hold(element, references);
            foreach (var reference in references)
                Add(reference);
        }
    }

    /// <summary>Adds the class and interface types a value of the type may hold strong references to.</summary>
    static void Hold(WylType type, HashSet<WylType> references)
    {
        foreach (var inline in type.InlineTypes())
        {
            if (inline.IsReference)
                references.Add(inline.CppIdentity);
            else if (inline.IsTypeParameter || inline.IsFunction)
                references.Add(inline);
        }
    }

    /// <summary>
    /// The class instantiations whose objects releasing a value of the type may destroy: those its
    /// strong references may refer to, and those their fields, elements, and closures' captures may,
    /// or null if that may be any, through a type parameter or a class only C++ instantiates.
    /// </summary>
    public IReadOnlySet<WylType>? Destroyable(WylType type)
    {
        if (destroyable.TryGetValue(type, out var known))
            return known;
        var found = new HashSet<WylType>();
        var seenClosures = new HashSet<FunctionSymbol>();
        var references = new HashSet<WylType>();
        Hold(type, references);
        var pending = new Stack<WylType>(references);
        bool any = false;
        while (!any && pending.TryPop(out var reference))
        {
            if (reference.IsFunction)
            {
                any = reference.ContainsTypeParameters;
                foreach (var closure in closures.Where(c => FunctionValues.MayBe(reference, c.Type) && seenClosures.Add(c.Lambda)))
                {
                    foreach (var captured in this.captured[closure.Lambda])
                        pending.Push(captured);
                }
                continue;
            }
            any = reference.IsTypeParameter || MayReferToUnknown(reference) || (reference.Symbol!.Kind == TypeKind.Class && !held.ContainsKey(reference));
            foreach (var c in classes.Where(c => IsSubtype(c, reference) && found.Add(c)))
            {
                foreach (var next in held[c])
                    pending.Push(next);
            }
        }
        return destroyable[type] = any ? null : found;
    }

    /// <summary>A node is cyclic if it is on a cycle: in a strongly connected component of several nodes, or with an edge to itself.</summary>
    void MarkCycles()
    {
        var nodes = classes.Where(c => !c.Symbol!.IsAcyclic && !c.Symbol!.IsShared).ToList();
        var unshared = this.closures.Where(c => !c.Lambda.IsShared).ToList();
        int closuresStart = nodes.Count;
        // Stands for the instantiations only C++ sees, which may hold anything.
        int unknown = closuresStart + unshared.Count;
        var edges = new List<int>[unknown + 1];
        for (int i = 0; i < unknown; i++)
        {
            edges[i] = [];
            bool reachesUnknown = false;
            foreach (var reference in i < closuresStart ? held[nodes[i]] : captured[unshared[i - closuresStart].Lambda])
            {
                if (reference.IsFunction)
                {
                    for (int j = 0; j < unshared.Count; j++)
                    {
                        if (FunctionValues.MayBe(reference, unshared[j].Type))
                            edges[i].Add(closuresStart + j);
                    }
                    reachesUnknown |= reference.ContainsTypeParameters;
                    continue;
                }
                for (int j = 0; j < nodes.Count; j++)
                {
                    if (IsSubtype(nodes[j], reference))
                        edges[i].Add(j);
                }
                reachesUnknown |= MayReferToUnknown(reference);
            }
            if (reachesUnknown)
                edges[i].Add(unknown);
        }
        edges[unknown] = [.. Enumerable.Range(0, unknown)];

        foreach (var component in StronglyConnectedComponents(edges))
        {
            foreach (int node in component)
            {
                if (node == unknown || (component.Count == 1 && !edges[node].Contains(node)))
                    continue;
                if (node < closuresStart)
                    cyclic.Add(nodes[node]);
                else
                    cyclicClosures.Add(unshared[node - closuresStart].Lambda);
            }
        }
    }

    static bool IsSubtype(WylType type, WylType target) =>
        type == target || Conversions.Supertypes(type, function: null).Any(t => t.CppIdentity == target);

    /// <summary>Whether a reference of the type may refer to an instantiation of a generic class that only C++ sees.</summary>
    bool MayReferToUnknown(WylType reference) =>
        reference.IsTypeParameter
        || types.Any(s => s is { Kind: TypeKind.Class, IsGeneric: true, IsAcyclic: false, IsShared: false }
            && Conversions.Supertypes(s.Type, function: null).Any(t => t.Symbol == reference.Symbol));

    /// <summary>Tarjan's algorithm.</summary>
    static List<List<int>> StronglyConnectedComponents(List<int>[] edges)
    {
        var components = new List<List<int>>();
        var index = new int[edges.Length];
        var low = new int[edges.Length];
        var onStack = new bool[edges.Length];
        var stack = new Stack<int>();
        Array.Fill(index, -1);
        int next = 0;

        void Visit(int node)
        {
            index[node] = low[node] = next++;
            stack.Push(node);
            onStack[node] = true;
            foreach (int target in edges[node])
            {
                if (index[target] < 0)
                {
                    Visit(target);
                    low[node] = Math.Min(low[node], low[target]);
                }
                else if (onStack[target])
                    low[node] = Math.Min(low[node], index[target]);
            }
            if (low[node] != index[node])
                return;
            var component = new List<int>();
            int member;
            do
            {
                member = stack.Pop();
                onStack[member] = false;
                component.Add(member);
            } while (member != node);
            components.Add(component);
        }

        for (int node = 0; node < edges.Length; node++)
        {
            if (index[node] < 0)
                Visit(node);
        }
        return components;
    }
}
