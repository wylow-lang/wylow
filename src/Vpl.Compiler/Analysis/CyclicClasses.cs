using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;

namespace Vpl.Compiler.Analysis;

/// <summary>
/// Finds the classes whose objects may be part of a cycle of strong references: those that can
/// reach an object of their own class through fields, including through structs, enums, fixed
/// arrays, and the elements of standard library containers, counting every class a field's type
/// could refer to. Only their objects are buffered and visited by the cycle collector. Generic
/// classes are decided per instantiation, since that is what the C++ instantiates. An escaping
/// lambda's closures are decided the same way, as objects holding what the lambda captures, and a
/// function value may hold the closure of any lambda of its type.
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
    readonly List<VplType> classes = [];
    readonly Dictionary<VplType, HashSet<VplType>> held = [];
    readonly HashSet<VplType> cyclic = [];
    readonly IReadOnlyList<BoundLambda> closures;
    readonly Dictionary<FunctionSymbol, HashSet<VplType>> captured = [];
    readonly HashSet<FunctionSymbol> cyclicClosures = [];

    CyclicClasses(WholeProgram whole)
    {
        stdlib = whole.Program.Stdlib;
        types = whole.Program.Types;
        closures = [.. whole.Lambdas.Where(l => l.Type.IsEscaping)];
    }

    /// <summary>Every class instantiation the program is known to create objects of.</summary>
    public IReadOnlyList<VplType> Classes => classes;

    public bool IsCyclic(VplType classType) => cyclic.Contains(Erase(classType));

    /// <summary>Whether the closures of an escaping lambda may be part of a cycle; one the analysis didn't see is assumed to be.</summary>
    public bool IsCyclic(BoundLambda lambda) => !captured.ContainsKey(lambda.Function) || cyclicClosures.Contains(lambda.Function);

    /// <summary>
    /// The type without the nullability of references anywhere in its type arguments: `List<Node>`
    /// and `List<Node?>` are one C++ type, so they must be decided alike. `List<int?>` is another.
    /// </summary>
    static VplType Erase(VplType type) =>
        type.IsNullable ? (type.IsNullableValue ? Erase(type.NonNull).Nullable : Erase(type.NonNull))
        : type.IsPointer ? Erase(type.ElementType!).PointerTo
        : type.IsFixedArray ? Erase(type.ElementType!).FixedArrayOf(type.FixedLength)
        : type.Symbol is { } s && type.TypeArguments.Count > 0 ? s.Construct([.. type.TypeArguments.Select(Erase)])
        : type;

    public static CyclicClasses Find(WholeProgram whole)
    {
        var result = new CyclicClasses(whole);
        result.Discover();
        result.MarkCycles();
        return result;
    }

    void Discover()
    {
        var pending = new Queue<VplType>();
        void Add(VplType type)
        {
            type = Erase(type);
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
        foreach (var closure in closures)
        {
            var references = captured[closure.Function] = [];
            var (locals, self) = BoundTree.Captures(closure);
            foreach (var local in locals)
                Hold(local.Local.Type, references);
            if (self is not null)
                Hold(self.Type, references);
            foreach (var reference in references)
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
            if (stdlib.ContainerElement(type) is { } element)
                Hold(element, references);
            foreach (var reference in references)
                Add(reference);
        }
    }

    /// <summary>Adds the class and interface types a value of the type may hold strong references to.</summary>
    static void Hold(VplType type, HashSet<VplType> references)
    {
        foreach (var inline in type.InlineTypes())
        {
            if (inline.IsReference)
                references.Add(Erase(inline));
            else if (inline.IsTypeParameter || inline.IsFunction)
                references.Add(inline);
        }
    }

    /// <summary>A node is cyclic if it is on a cycle: in a strongly connected component of several nodes, or with an edge to itself.</summary>
    void MarkCycles()
    {
        var nodes = classes.Where(c => !c.Symbol!.IsAcyclic).ToList();
        int closuresStart = nodes.Count;
        // Stands for the instantiations only C++ sees, which may hold anything.
        int unknown = closuresStart + closures.Count;
        var edges = new List<int>[unknown + 1];
        for (int i = 0; i < unknown; i++)
        {
            edges[i] = [];
            bool reachesUnknown = false;
            foreach (var reference in i < closuresStart ? held[nodes[i]] : captured[closures[i - closuresStart].Function])
            {
                if (reference.IsFunction)
                {
                    for (int j = 0; j < closures.Count; j++)
                    {
                        if (FunctionValues.MayBe(reference, closures[j].Type))
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
                    cyclicClosures.Add(closures[node - closuresStart].Function);
            }
        }
    }

    static bool IsSubtype(VplType type, VplType target) =>
        type == target || Conversions.Supertypes(type).Any(t => Erase(t) == target);

    /// <summary>Whether a reference of the type may refer to an instantiation of a generic class that only C++ sees.</summary>
    bool MayReferToUnknown(VplType reference) =>
        reference.IsTypeParameter
        || types.Any(s => s is { Kind: TypeKind.Class, IsGeneric: true, IsAcyclic: false }
            && Conversions.Supertypes(s.Type).Any(t => t.Symbol == reference.Symbol));

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
