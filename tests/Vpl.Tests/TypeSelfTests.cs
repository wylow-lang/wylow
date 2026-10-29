using System.Text;
using Vpl.Compiler;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

// Properties every type must have, checked on random types built from the built-in types, the
// standard library's, and the fixture's. Each failure is a bug, or an exception the spec must name.
static class TypeSelfTests
{
    const int Seed = 1;
    const int Count = 1500;

    // `Probe`'s type parameters are the ones the random types use, so that the same types can be
    // written as parameters of its methods.
    const string Fixture = """
        struct Vec { internal { int x; float y; } }
        struct Init { internal { int n = 1; } }
        struct Box<T> { internal { T value; } }
        struct Pair<A, B> { internal { A first; B second; } }
        struct Holds { internal { Node node; } }
        struct Sorted<T extends Comparable<T>> { internal { T item; } }
        class Node { internal { mut Node? next; } }
        class Cell<T> {
            internal {
                T value;
                Cell(T value) { this.value = value; }
            }
        }
        class Animal { }
        class Dog extends Animal { }
        interface Shape { }
        class Circle implements Shape { }
        enum Color { Red, Green }
        enum Maybe<T> { Some(T value), None }
        enum Choice { Nothing, Thing(Node node) }
        class Probe<T, U extends Comparable<U>> {

        """;

    sealed record Property(string Name, Func<VplType, string?> Check);

    public static List<TestResult> Run()
    {
        var first = Compile(Fixture + "}\nvoid main() { }\n", "types/fixture", out var failure);
        if (first is null)
            return [failure!];
        var types = new TypeGenerator(first, Seed).Take(Count);
        return [.. Properties(first).Select(p => Check(p, types)), .. PoolChecks(types), ParsesBack(types), ConversionFacts(first)];
    }

    static BoundProgram? Compile(string text, string name, out TestResult? failure)
    {
        var compilation = Compilation.Compile(new SourceText("type_self_test.vpl", text));
        failure = compilation.Program is null ? new(name, string.Join('\n', compilation.Diagnostics.Take(20))) : null;
        return compilation.Program;
    }

    static TestResult Check(Property property, IReadOnlyList<VplType> types) =>
        Report(property.Name, types.Select(t => property.Check(t) is { } problem ? $"{t}: {problem}" : null));

    // The shortest counterexamples first, since they show the cause best.
    static TestResult Report(string name, IEnumerable<string?> problems)
    {
        var found = problems.OfType<string>().Distinct().OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal).ToList();
        if (found.Count == 0)
            return new($"types/{name}", null);
        return new($"types/{name}", $"{found.Count} counterexamples, the shortest:\n" + string.Join('\n', found.Take(8)));
    }

    static IEnumerable<Property> Properties(BoundProgram program)
    {
        var parameters = TypeGenerator.Parameters(program);
        var box = program.Types.Single(s => s.Name == "Box");
        yield return new("map_parts", t => t.MapParts(p => p) == t ? null : "rebuilt from its parts, it is another type");
        yield return new("empty_map", t => TypeMap.Empty.Apply(t) == t ? null : "the empty substitution changed it");
        yield return new("substitute_parameter", t => parameters.All(p => TypeMap.Empty.With([p], [t]).Apply(p.Type) == t)
            ? null : "substituted for a type parameter, it is another type");
        yield return new("substitute_self", t => TypeMap.Empty.With(parameters, [.. parameters.Select(p => p.Type)]).Apply(t) == t
            ? null : "substituting each type parameter by itself changed it");
        yield return new("contained_parameters", t =>
            t.ContainsTypeParameters != t.ContainedTypeParameters.Any() ? "ContainsTypeParameters disagrees with ContainedTypeParameters"
            : t.ContainedTypeParameters.Except(parameters).Any() ? "contains a type parameter it wasn't built from"
            : TypeMap.Empty.With(parameters, [.. parameters.Select(_ => VplType.Int)]).Apply(t) is var closed && closed.ContainsTypeParameters
                ? $"substituting every type parameter leaves some, in '{closed}'"
            : null);
        yield return new("cpp_identity", t => t.CppIdentity.CppIdentity != t.CppIdentity ? $"its C++ identity '{t.CppIdentity}' has another one"
            : Parts(t.CppIdentity).FirstOrDefault(p => p == VplType.NInt || p == VplType.NUInt || p is NullableType { NonNull.IsReference: true }) is { } kept
                ? $"its C++ identity '{t.CppIdentity}' keeps '{kept}'"
            : null);
        yield return new("cpp_identity_facts", t => FactDifferences(t, t.CppIdentity) is { } differ ? $"its C++ identity '{t.CppIdentity}' differs in {differ}" : null);
        yield return new("conversion_reflexive", t => Conversions.IsImplicit(t, t) ? null : "doesn't convert to itself");
        yield return new("nullable_conversion", t => !t.CanBeNullable ? null
            : !Conversions.IsImplicit(t, t.Nullable) ? $"doesn't convert to '{t.Nullable}'"
            : !Conversions.IsImplicit(VplType.Null, t.Nullable) ? $"null doesn't convert to '{t.Nullable}'"
            : null);
        yield return new("nullable_object", t => t.IsNullableObject == (t.MayBeNull && !t.IsNullableValue)
            ? null : "IsNullableObject disagrees with MayBeNull and IsNullableValue");
        yield return new("storage_facts", t =>
            t.MayOwnObjects && !t.IsCounted ? "may own objects, but isn't counted"
            : t.SharesStorage && !t.IsCounted ? "shares storage, but isn't counted"
            : t.SharesStorage && !t.MayShareStorage ? "shares storage, but may not"
            : t.IsCounted && !t.NonNull.IsTypeParameter && !t.BorrowSavesCopy ? "is counted, but borrowing it saves no copy"
            : t.DefaultRunsCode && !t.HasDefaultCode ? "making its default runs code, but it has no default code"
            : null);
        yield return new("inline_parts", t => t.InlineTypes().Skip(1).FirstOrDefault(p => !t.MayContain(p, element: false) && !t.MayContain(p, element: true)) is { } part
            ? $"stores a '{part}' inline, but may not contain one" : null);
        yield return new("defaultable_fields", t => !t.IsStruct || t.Symbol!.IsExtern ? null
            : t.IsDefaultable != (t.FindNonDefaultableField() is null) ? "IsDefaultable disagrees with FindNonDefaultableField"
            : t.IsDefaultable && t.Symbol.Fields.FirstOrDefault(f => f.Declaration.Initializer is null && !TypeMap.For(t).Apply(f.Type).IsDefaultable) is { } field
                ? $"is defaultable, but field '{field.Name}' has no default"
            : null);
        yield return new("equatable_fields", t => !t.IsStructOrEnum || t.Symbol!.IsExtern ? null
            : t.SupportsEquality != (t.FindNonEquatableField() is null) ? "SupportsEquality disagrees with FindNonEquatableField"
            : null);
        // The runtime writes a formatted type as text itself, so it is one of the primitives or String.
        yield return new("formatted_types", t =>
            t.IsFormatted && t.NonNull.IsAggregate ? $"'{t}' is an aggregate, which the runtime doesn't write as text"
            : t.IsFormatted && !t.SupportsEquality ? $"'{t}' is written by the runtime, but doesn't support equality"
            : null);
        // A fixed array, a nullable value, and a struct with one field hold their value as it is.
        yield return new("wrapper_facts", t =>
        {
            if (!Storable(t))
                return null;
            List<VplType> wrappers = [t.FixedArrayOf(2), box.Construct([t])];
            if (t.CanBeNullable && !t.IsReference)
                wrappers.Add(t.Nullable);
            foreach (var wrapper in wrappers)
            {
                if (WrapperDifferences(t, wrapper) is { } differ)
                    return $"'{wrapper}' differs from it in {differ}";
                // A nullable value is held where its value is, so it shares what its value shares.
                if (wrapper.IsNullable && wrapper.SharesStorage != t.SharesStorage)
                    return $"'{t}' and '{wrapper}' disagree on SharesStorage";
            }
            return null;
        });
        yield return new("supertypes", t =>
        {
            foreach (var super in Conversions.Supertypes(t))
            {
                if (!Conversions.IsSubtype(t, super))
                    return $"isn't a subtype of its supertype '{super}'";
                if (t.IsReference && super.IsReference && !Conversions.IsImplicit(t, super))
                    return $"doesn't convert to its supertype '{super}'";
            }
            return null;
        });
    }

    static bool Storable(VplType t) => !t.IsRefStruct && !t.IsNullableReference && !t.IsNonEscapingFunction && !t.IsNullableNonEscapingFunction;

    /// <summary>The type and the types it is built from, recursively.</summary>
    static IEnumerable<VplType> Parts(VplType t) => [t, .. t.Parts.SelectMany(Parts)];

    // What is decided per C++ type: how a value is stored, copied, released, and made by default.
    static readonly (string Name, Func<VplType, bool> Fact)[] CppFacts =
    [
        ("IsCounted", t => t.IsCounted),
        ("MayOwnObjects", t => t.MayOwnObjects),
        ("SharesStorage", t => t.SharesStorage),
        ("MayShareStorage", t => t.MayShareStorage),
        ("KeepsIdentity", t => t.KeepsIdentity),
        ("BorrowSavesCopy", t => t.BorrowSavesCopy),
        ("HasDefaultCode", t => t.HasDefaultCode),
        ("DefaultRunsCode", t => t.DefaultRunsCode),
    ];

    static string? FactDifferences(VplType a, VplType b)
    {
        var names = CppFacts.Where(f => f.Fact(a) != f.Fact(b)).Select(f => f.Name).ToList();
        if (!a.InlineTypes().Select(t => t.CppIdentity).ToHashSet().SetEquals(b.InlineTypes().Select(t => t.CppIdentity)))
            names.Add("InlineTypes");
        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    static readonly (string Name, Func<VplType, bool> Fact)[] HeldFacts =
    [
        ("IsCounted", t => t.IsCounted),
        ("MayOwnObjects", t => t.MayOwnObjects),
        ("SupportsEquality", t => t.SupportsEquality),
    ];

    static readonly (string Name, Func<VplType, bool> Fact)[] DefaultFacts =
    [
        ("IsDefaultable", t => t.IsDefaultable),
        ("DefaultRunsCode", t => t.DefaultRunsCode),
    ];

    static string? WrapperDifferences(VplType held, VplType wrapper)
    {
        // A nullable value is null by default; the others are made of their value's default.
        var facts = wrapper.IsNullable ? HeldFacts : [.. HeldFacts, .. DefaultFacts];
        var names = facts.Where(f => f.Fact(held) != f.Fact(wrapper)).Select(f => f.Name).ToList();
        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    /// <summary>Properties of the implicit conversions and of names, over the random types and the types near them.</summary>
    static IEnumerable<TestResult> PoolChecks(IReadOnlyList<VplType> types)
    {
        var pool = new HashSet<VplType>([VplType.Null, VplType.Char, VplType.Float, VplType.Double, .. VplType.Integers]);
        foreach (var t in types.Take(400))
        {
            IEnumerable<VplType> near = [t, t.NonNull, t.Narrowed, t.Referred, .. Conversions.Supertypes(t)];
            if (t.CanBeNullable)
                near = near.Append(t.Nullable);
            if (t.NonNull is FunctionType function)
                near = near.Concat([function.Escaping, function.NonEscaping, function.Escaping.Nullable, function.NonEscaping.Nullable]);
            if (t is NullableReferenceType reference)
                near = near.Concat([reference.Referenced.NullableReference(RefKind.In), reference.Referenced.NullableReference(RefKind.Ref)]);
            pool.UnionWith(near);
        }
        var list = pool.ToList();
        var converts = list.ToDictionary(a => a, a => list.Where(b => Conversions.IsImplicit(a, b)).ToHashSet());

        yield return Report("conversion_explicit", list.SelectMany(a => converts[a].Where(b => !Conversions.IsExplicit(a, b))
            .Select(b => $"'{a}' converts implicitly but not explicitly to '{b}'")));
        // The one exception: `char` converts to `int`, which converts to `double`, but `char` isn't a
        // number (spec 02-types.md §2.3).
        yield return Report("conversion_transitive", list.SelectMany(a => converts[a].SelectMany(b => converts[b]
            .Where(c => !converts[a].Contains(c) && !(a.NonNull == VplType.Char && c.NonNull.IsFloatingPoint))
            .Select(c => $"'{a}' converts to '{b}', and that to '{c}', but not '{a}' to '{c}'"))));
        yield return Report("conversion_antisymmetric", list.SelectMany(a => converts[a]
            .Where(b => a != b && converts[b].Contains(a)).Select(b => $"'{a}' and '{b}' convert to each other")));
        yield return Report("names_distinct", list.GroupBy(t => t.Name).Where(g => g.Count() > 1 && !Twins([.. g]))
            .Select(g => $"'{g.Key}' names {g.Count()} types"));
    }

    // The two forms of a function type print alike, nullable or not.
    static bool Twins(IReadOnlyList<VplType> types) =>
        types.Count == 2 && types[0].IsNullable == types[1].IsNullable
        && types[0].NonNull is FunctionType a && types[1].NonNull is FunctionType b && a.NonEscaping == b.NonEscaping;

    /// <summary>
    /// Each type's name, written as a parameter's type, binds to the type. Generating the types again
    /// from the same seed builds them from the second compilation's symbols.
    /// </summary>
    static TestResult ParsesBack(IReadOnlyList<VplType> types)
    {
        var text = new StringBuilder(Fixture);
        text.AppendLine("    internal {");
        int firstLine = text.ToString().Count(c => c == '\n') + 1;
        for (int i = 0; i < types.Count; i++)
        {
            var annotation = types[i].NonNull.IsEscaping ? "@Escaping " : "";
            text.AppendLine($"        void p{i}({annotation}{types[i].Name} x) {{ }}");
        }
        text.AppendLine("    }\n}\nvoid main() { }");
        var compilation = Compilation.Compile(new SourceText("type_self_test.vpl", text.ToString()));
        if (compilation.Program is not { } program)
        {
            // The parser's recovery spills a syntax error onto the next probe.
            var lines = compilation.Diagnostics.Select(d => d.Line).ToHashSet();
            return Report("parses_back", compilation.Diagnostics.Where(d => !lines.Contains(d.Line - 1)).Select(d => d.Line - firstLine is var i && i >= 0 && i < types.Count
                ? $"'{types[i]}': {d.Message}" : d.ToString()));
        }
        var expected = new TypeGenerator(program, Seed).Take(types.Count);
        var probe = program.Types.Single(s => s.Name == "Probe");
        return Report("parses_back", Enumerable.Range(0, types.Count).Select(i =>
            probe.Methods[$"p{i}"].Single().Parameters.Single().Type is var bound && bound != expected[i]
                ? $"'{expected[i]}' binds to '{bound}'{(bound.Name == expected[i].Name ? ", which prints alike" : "")}" : null));
    }

    /// <summary>
    /// Conversions at the type-set edges no random type reaches: error recovery, the non-numeric
    /// casts, an enum with data, and exactness at a floating type's limit.
    /// </summary>
    static TestResult ConversionFacts(BoundProgram program)
    {
        List<string> problems = [];
        void Expect(bool condition, string problem)
        {
            if (!condition)
                problems.Add(problem);
        }

        Expect(Conversions.IsImplicit(VplType.Error, VplType.Int), "an error type converts to anything");
        Expect(!Conversions.IsExplicit(VplType.Bool, VplType.Char), "a bool doesn't cast to a char");
        var maybe = program.Types.Single(s => s.Name == "Maybe").Construct([VplType.Int]);
        Expect(!Conversions.IsExplicit(VplType.Int, maybe), "an integer doesn't cast to an enum whose variants carry data");
        Expect(Conversions.IsExactlyRepresentable(1, VplType.Float), "1 is exact in a float");
        Expect(!Conversions.IsExactlyRepresentable(((Int128)1 << 24) + 1, VplType.Float), "2^24 + 1 is not exact in a float");
        Expect(Conversions.IsExactlyRepresentable(1, VplType.Double), "1 is exact in a double");
        Expect(!Conversions.IsExactlyRepresentable(((Int128)1 << 53) + 1, VplType.Double), "2^53 + 1 is not exact in a double");
        return new("types/conversions", problems.Count == 0 ? null : string.Join('\n', problems));
    }
}

/// <summary>
/// Random types the binder accepts as a parameter's type, deterministic for a seed and the
/// declarations of a program: each kind of type, with its parts picked recursively.
/// </summary>
sealed class TypeGenerator(BoundProgram program, int seed)
{
    readonly Random random = new(seed);

    // By name, so that two compilations of one fixture generate alike.
    readonly TypeSymbol[] symbols = [.. program.Types.Where(s => s.Name != "String").OrderBy(s => s.Name, StringComparer.Ordinal)];
    readonly IReadOnlyList<TypeParameterSymbol> parameters = Parameters(program);

    static readonly VplType[] Builtins = [VplType.Bool, VplType.Char, VplType.Float, VplType.Double, VplType.String, .. VplType.Integers];

    public static IReadOnlyList<TypeParameterSymbol> Parameters(BoundProgram program) => program.Types.Single(s => s.Name == "Probe").TypeParameters;

    enum Place { Parameter, Storable, Return }

    public IReadOnlyList<VplType> Take(int count) => [.. Enumerable.Range(0, count).Select(_ => Make(Place.Parameter, depth: 3))];

    VplType Make(Place place, int depth)
    {
        while (true)
        {
            int kind = depth == 0 ? random.Next(3) : random.Next(10);
            var made = kind switch
            {
                0 or 1 => Builtins[random.Next(Builtins.Length)],
                2 => random.Next(4) == 0 ? parameters[random.Next(parameters.Count)].Type : place == Place.Return && random.Next(3) == 0 ? VplType.Void : null,
                3 or 4 => Named(depth),
                5 => Nullable(place, depth),
                6 => Make(Place.Storable, depth - 1).PointerTo,
                7 => Make(Place.Storable, depth - 1).FixedArrayOf(random.Next(1, 4)),
                8 => Function(place, depth),
                _ => place == Place.Parameter ? NullableReference(depth) : null,
            };
            if (made is not null && (place == Place.Parameter || !made.IsRefStruct))
                return made;
        }
    }

    VplType? Named(int depth)
    {
        var symbol = symbols[random.Next(symbols.Length)];
        if (!symbol.IsGeneric)
            return symbol.Type;
        if (symbol == program.Stdlib.Weak)
            return WeakTarget(depth) is { } target ? symbol.Construct([target]) : null;
        List<VplType> arguments = [.. symbol.TypeParameters.Select(_ => Make(Place.Storable, depth - 1))];
        var map = TypeMap.Empty.With(symbol.TypeParameters, arguments);
        bool satisfied = symbol.TypeParameters.Zip(arguments).All(pair => pair.First.Bounds.All(b => program.Stdlib.SatisfiesBound(pair.Second, map.Apply(b))));
        return satisfied ? symbol.Construct(arguments) : null;
    }

    VplType? WeakTarget(int depth)
    {
        for (int i = 0; i < 20; i++)
        {
            if (Make(Place.Storable, depth - 1) is { IsReference: true, IsNullable: false } target)
                return target;
        }
        return null;
    }

    VplType? Nullable(Place place, int depth)
    {
        var inner = Make(Place.Storable, depth - 1);
        if (!inner.CanBeNullable)
            return null;
        // Only a parameter can hold a nullable non-escaping function.
        return place == Place.Parameter && inner is FunctionType function && random.Next(2) == 0 ? function.NonEscaping.Nullable : inner.Nullable;
    }

    VplType Function(Place place, int depth)
    {
        var functionParameters = Enumerable.Range(0, random.Next(4)).Select(_ => Parameter(depth - 1)).ToList();
        var function = Make(Place.Return, depth - 1).FunctionReturning(functionParameters);
        // A function type is escaping unless it is a parameter's.
        return place == Place.Parameter && random.Next(2) == 0 ? function : function.Escaping;
    }

    FunctionParameterType Parameter(int depth)
    {
        var type = Make(Place.Parameter, depth);
        if (type is NullableReferenceType reference)
            return new(reference.Kind, type);
        // Objects and functions are passed without copying, so they can't be 'ref' or 'in';
        // a nullable function is such a value too.
        bool passedAsIs = type.IsReference || type.NonNull.IsFunction;
        return new(passedAsIs ? RefKind.None : (RefKind)random.Next(3), type);
    }

    VplType? NullableReference(int depth)
    {
        var referenced = Make(Place.Storable, depth - 1);
        // A reference to a function value would be a place nothing can have, so no 'ref?' or 'in?'.
        return referenced.IsReference || referenced.NonNull.IsFunction ? null : referenced.NullableReference(random.Next(2) == 0 ? RefKind.Ref : RefKind.In);
    }
}
