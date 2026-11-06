using System.Text;
using Wyl.Compiler;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;
using Wyl.Harness;

// Properties every type must have, checked on random types built from the built-in types, the
// standard library's, and the fixture's. Each failure is a bug, or an exception the spec must name.
static class TypeSelfTests
{
    const int Seed = 1;
    const int Count = 1500;

    // `Probe`'s type parameters are the ones the random types use, so that the same types can be
    // written as parameters of its methods.
    const string Fixture = """
        struct Vec { int x; float y; }
        struct Init { int n = 1; }
        struct Box<T> { T value; }
        struct Pair<A, B> { A first; B second; }
        struct Holds { Node node; }
        struct Sorted<T extends Comparable<T>> { T item; }
        class Node { mut Node? next; }
        class Cell<T> {
            T value;
            Cell(T value) { this.value = value; }
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

    sealed record Property(string Name, Func<WylType, string?> Check);

    public static List<TestResult> Run()
    {
        var first = Compile(Fixture + "}\nvoid main() { }\n", "types/fixture", out var failure);
        if (first is null)
            return [failure!];
        var types = new TypeGenerator(first, Seed).Take(Count);
        return [.. Properties(first).Select(p => Check(p, types)), .. PoolChecks(types), ParsesBack(types), ConversionFacts(first), CopyingFacts(), SendableFacts(), FunctionFormFacts()];
    }

    /// <summary>
    /// What a unique type is copyable as (spec <c>types.copyable</c>, <c>structs.unique</c>): a
    /// unique struct or enum is not copyable, nor is anything holding one; a type parameter is not
    /// copyable until a bound says so; and a class reference, a function, a primitive, and a string are.
    /// </summary>
    static TestResult CopyingFacts()
    {
        const string text = """
            unique struct Owned { int n; }
            unique struct Holder { Owned owned; }
            unique enum MaybeOwned { None, Some(Owned owned) }
            struct Box<T> { T value; }

            class Node { }

            void main() { }
            """;
        if (Compile(text, "types/copying", out var failure) is not { } program)
            return failure!;
        var owned = program.Types.Single(s => s.Name == "Owned").Type;
        var holder = program.Types.Single(s => s.Name == "Holder").Type;
        var maybe = program.Types.Single(s => s.Name == "MaybeOwned").Type;
        var box = program.Types.Single(s => s.Name == "Box").Construct([owned]);
        var node = program.Types.Single(s => s.Name == "Node").Type;
        List<string> problems = [];
        void Expect(WylType type, bool unique, bool copyable)
        {
            if (type.IsUniqueType != unique || type.IsCopyable != copyable)
                problems.Add($"'{type}' is {(type.IsUniqueType ? "" : "not ")}unique and {(type.IsCopyable ? "" : "not ")}copyable");
        }
        Expect(owned, unique: true, copyable: false);
        Expect(owned.FixedArrayOf(2), unique: true, copyable: false);
        Expect(owned.Nullable, unique: true, copyable: false);
        Expect(holder, unique: true, copyable: false);
        Expect(maybe, unique: true, copyable: false);
        Expect(box, unique: true, copyable: false);
        Expect(node, unique: false, copyable: true);
        Expect(WylType.String, unique: false, copyable: true);
        Expect(WylType.Int, unique: false, copyable: true);
        Expect(WylType.Int.FunctionReturning([]), unique: false, copyable: true);
        return new("types/copying", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    static TestResult SendableFacts()
    {
        const string text = """
            import std::{List};

            struct Pair { int first; String second; }
            struct Holds { Node node; }
            struct Box<T> { T value; }

            class Node { }

            void main() { }
            """;
        if (Compile(text, "types/sendable", out var failure) is not { } program)
            return failure!;
        var pair = program.Types.Single(s => s.Name == "Pair").Type;
        var holds = program.Types.Single(s => s.Name == "Holds").Type;
        var node = program.Types.Single(s => s.Name == "Node").Type;
        var box = program.Types.Single(s => s.Name == "Box");
        var list = program.Types.Single(s => s.Name == "List");
        List<string> problems = [];
        void Expect(WylType type, bool sendable)
        {
            if (type.IsSendable != sendable)
                problems.Add($"'{type}' is {(type.IsSendable ? "" : "not ")}sendable");
        }
        Expect(pair, sendable: true);
        Expect(holds, sendable: false);
        Expect(holds.FixedArrayOf(2), sendable: false);
        Expect(node, sendable: false);
        Expect(node.Nullable, sendable: false);
        Expect(node.FixedArrayOf(2), sendable: false);
        Expect(box.Construct([WylType.Int]), sendable: true);
        Expect(box.Construct([node]), sendable: false);
        Expect(list.Construct([WylType.Int]), sendable: false);
        Expect(program.Stdlib.Array!.Type, sendable: false);
        Expect(WylType.Int, sendable: true);
        Expect(WylType.Int.Nullable, sendable: true);
        Expect(WylType.String, sendable: true);
        Expect(WylType.Int.FunctionReturning([]), sendable: false);
        Expect(WylType.Int.FunctionReturning([]).Escaping, sendable: false);
        Expect(WylType.Int.FunctionReturning([]).Shared, sendable: false);
        Expect(WylType.Int.FunctionReturning([]).Shared.Escaping, sendable: true);
        Expect(WylType.Int.PointerTo, sendable: false);
        return new("types/sendable", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    static TestResult FunctionFormFacts()
    {
        List<string> problems = [];
        void Expect(bool condition, string problem)
        {
            if (!condition)
                problems.Add(problem);
        }
        var plain = WylType.Int.FunctionReturning([]);
        var escaping = plain.Escaping;
        var shared = plain.Shared;
        var sharedEscaping = shared.Escaping;
        Expect(new[] { plain, escaping, shared, sharedEscaping }.Distinct().Count() == 4, "the four forms are not distinct");
        Expect(ReferenceEquals(plain.Escaping, escaping) && ReferenceEquals(escaping.NonEscaping, plain), "escaping does not return to the same forms");
        Expect(ReferenceEquals(plain.Shared, shared) && ReferenceEquals(shared.NonShared, plain), "shared does not return to the same forms");
        Expect(ReferenceEquals(shared.Escaping, sharedEscaping) && ReferenceEquals(sharedEscaping.Shared, sharedEscaping), "the shared and escaping forms disagree");
        Expect(ReferenceEquals(escaping.Shared, sharedEscaping) && ReferenceEquals(sharedEscaping.NonShared, escaping), "the two ways to the shared escaping form disagree");
        Expect(ReferenceEquals(sharedEscaping.NonEscaping, shared), "dropping escaping leaves another shared form");
        Expect(Conversions.IsImplicit(sharedEscaping, plain), "an escaping shared function does not convert to a non-escaping one");
        Expect(Conversions.IsImplicit(sharedEscaping, escaping), "a shared function does not convert to the same type without shared");
        Expect(Conversions.IsImplicit(sharedEscaping, shared), "an escaping shared function does not convert to a non-escaping shared one");
        Expect(!Conversions.IsImplicit(plain, escaping), "a conversion adds escaping");
        Expect(!Conversions.IsImplicit(plain, shared), "a conversion adds shared");
        Expect(!Conversions.IsImplicit(escaping, sharedEscaping), "a conversion adds shared to an escaping function");
        Expect(!Conversions.IsImplicit(shared, sharedEscaping), "a conversion adds escaping to a shared function");
        Expect(sharedEscaping.MapParts(p => p) == sharedEscaping, "rebuilding a shared form changes it");
        Expect(sharedEscaping.CppIdentity == escaping.CppIdentity, "the C++ identity keeps shared");
        var inner = new FunctionParameterType(RefKind.None, plain);
        var outer = (FunctionType)WylType.Int.FunctionReturning([inner]).Shared.MapParts(p => p.NonNull is FunctionType function ? function.Escaping : p);
        Expect(outer.IsShared, "MapParts drops shared");
        Expect(outer.Parameters[0].Type.IsEscaping, "MapParts does not map the parameters");
        return new("types/function_forms", problems.Count == 0 ? null : string.Join('\n', problems));
    }

    static BoundProgram? Compile(string text, string name, out TestResult? failure)
    {
        var compilation = Compilation.Compile(new SourceText("type_self_test.wyl", TestPrograms.WithStd(text)));
        failure = compilation.Program is null ? new(name, string.Join('\n', compilation.Diagnostics.Take(20))) : null;
        return compilation.Program;
    }

    static TestResult Check(Property property, IReadOnlyList<WylType> types) =>
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
            : TypeMap.Empty.With(parameters, [.. parameters.Select(_ => WylType.Int)]).Apply(t) is var closed && closed.ContainsTypeParameters
                ? $"substituting every type parameter leaves some, in '{closed}'"
            : null);
        yield return new("cpp_identity", t => t.CppIdentity.CppIdentity != t.CppIdentity ? $"its C++ identity '{t.CppIdentity}' has another one"
            : Parts(t.CppIdentity).FirstOrDefault(p => p == WylType.NInt || p == WylType.NUInt || p is NullableType { NonNull.IsReference: true }) is { } kept
                ? $"its C++ identity '{t.CppIdentity}' keeps '{kept}'"
            : null);
        yield return new("cpp_identity_facts", t => FactDifferences(t, t.CppIdentity) is { } differ ? $"its C++ identity '{t.CppIdentity}' differs in {differ}" : null);
        yield return new("conversion_reflexive", t => Conversions.IsImplicit(t, t) ? null : "doesn't convert to itself");
        yield return new("nullable_conversion", t => !t.CanBeNullable ? null
            : !Conversions.IsImplicit(t, t.Nullable) ? $"doesn't convert to '{t.Nullable}'"
            : !Conversions.IsImplicit(WylType.Null, t.Nullable) ? $"null doesn't convert to '{t.Nullable}'"
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
            List<WylType> wrappers = [t.FixedArrayOf(2), box.Construct([t])];
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

    static bool Storable(WylType t) => !t.IsRefStruct && !t.IsNullableReference && !t.IsNonEscapingFunction && !t.IsNullableNonEscapingFunction;

    /// <summary>The type and the types it is built from, recursively.</summary>
    static IEnumerable<WylType> Parts(WylType t) => [t, .. t.Parts.SelectMany(Parts)];

    // What is decided per C++ type: how a value is stored, copied, released, and made by default.
    static readonly (string Name, Func<WylType, bool> Fact)[] CppFacts =
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

    static string? FactDifferences(WylType a, WylType b)
    {
        var names = CppFacts.Where(f => f.Fact(a) != f.Fact(b)).Select(f => f.Name).ToList();
        if (!a.InlineTypes().Select(t => t.CppIdentity).ToHashSet().SetEquals(b.InlineTypes().Select(t => t.CppIdentity)))
            names.Add("InlineTypes");
        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    static readonly (string Name, Func<WylType, bool> Fact)[] HeldFacts =
    [
        ("IsCounted", t => t.IsCounted),
        ("MayOwnObjects", t => t.MayOwnObjects),
        ("SupportsEquality", t => t.SupportsEquality),
    ];

    static readonly (string Name, Func<WylType, bool> Fact)[] DefaultFacts =
    [
        ("IsDefaultable", t => t.IsDefaultable),
        ("DefaultRunsCode", t => t.DefaultRunsCode),
    ];

    static string? WrapperDifferences(WylType held, WylType wrapper)
    {
        // A nullable value is null by default; the others are made of their value's default.
        var facts = wrapper.IsNullable ? HeldFacts : [.. HeldFacts, .. DefaultFacts];
        var names = facts.Where(f => f.Fact(held) != f.Fact(wrapper)).Select(f => f.Name).ToList();
        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    /// <summary>Properties of the implicit conversions and of names, over the random types and the types near them.</summary>
    static IEnumerable<TestResult> PoolChecks(IReadOnlyList<WylType> types)
    {
        var pool = new HashSet<WylType>([WylType.Null, WylType.Char, WylType.Float, WylType.Double, .. WylType.Integers]);
        foreach (var t in types.Take(400))
        {
            IEnumerable<WylType> near = [t, t.NonNull, t.Narrowed, t.Referred, .. Conversions.Supertypes(t)];
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
        // number (spec 04-types.md#types.primitives.char).
        yield return Report("conversion_transitive", list.SelectMany(a => converts[a].SelectMany(b => converts[b]
            .Where(c => !converts[a].Contains(c) && !(a.NonNull == WylType.Char && c.NonNull.IsFloatingPoint))
            .Select(c => $"'{a}' converts to '{b}', and that to '{c}', but not '{a}' to '{c}'"))));
        yield return Report("conversion_antisymmetric", list.SelectMany(a => converts[a]
            .Where(b => a != b && converts[b].Contains(a)).Select(b => $"'{a}' and '{b}' convert to each other")));
        yield return Report("names_distinct", list.GroupBy(t => t.Name).Where(g => g.Count() > 1 && !Twins([.. g]))
            .Select(g => $"'{g.Key}' names {g.Count()} types"));
    }

    // The two forms of a function type print alike, nullable or not.
    static bool Twins(IReadOnlyList<WylType> types) =>
        types.Count == 2 && types[0].IsNullable == types[1].IsNullable
        && types[0].NonNull is FunctionType a && types[1].NonNull is FunctionType b && a.NonEscaping == b.NonEscaping;

    /// <summary>
    /// Each type's name, written as a parameter's type, binds to the type. Generating the types again
    /// from the same seed builds them from the second compilation's symbols.
    /// </summary>
    static TestResult ParsesBack(IReadOnlyList<WylType> types)
    {
        var text = new StringBuilder(Fixture);
        int firstLine = text.ToString().Count(c => c == '\n') + 1;
        for (int i = 0; i < types.Count; i++)
        {
            var annotation = types[i].NonNull.IsEscaping ? "escaping " : "";
            text.AppendLine($"    void p{i}({annotation}{types[i].Name} x) {{ }}");
        }
        text.AppendLine("}\nvoid main() { }");
        var compilation = Compilation.Compile(new SourceText("type_self_test.wyl", TestPrograms.WithStd(text.ToString())));
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

        Expect(Conversions.IsImplicit(WylType.Error, WylType.Int), "an error type converts to anything");
        Expect(!Conversions.IsExplicit(WylType.Bool, WylType.Char), "a bool doesn't cast to a char");
        var maybe = program.Types.Single(s => s.Name == "Maybe").Construct([WylType.Int]);
        Expect(!Conversions.IsExplicit(WylType.Int, maybe), "an integer doesn't cast to an enum whose variants carry data");
        Expect(Conversions.IsExactlyRepresentable(1, WylType.Float), "1 is exact in a float");
        Expect(!Conversions.IsExactlyRepresentable(((Int128)1 << 24) + 1, WylType.Float), "2^24 + 1 is not exact in a float");
        Expect(Conversions.IsExactlyRepresentable(1, WylType.Double), "1 is exact in a double");
        Expect(!Conversions.IsExactlyRepresentable(((Int128)1 << 53) + 1, WylType.Double), "2^53 + 1 is not exact in a double");
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
    readonly TypeSymbol[] symbols = [.. program.Types.Where(s => !StdlibSymbols.IsHostedTypeName(s.Name) && s.Reach.Includes(program.Sources[0])).OrderBy(s => s.Name, StringComparer.Ordinal)];
    readonly IReadOnlyList<TypeParameterSymbol> parameters = Parameters(program);

    static readonly WylType[] Builtins = [WylType.Bool, WylType.Char, WylType.Float, WylType.Double, WylType.String, .. WylType.Integers];

    public static IReadOnlyList<TypeParameterSymbol> Parameters(BoundProgram program) => program.Types.Single(s => s.Name == "Probe").TypeParameters;

    enum Place { Parameter, Storable, Return }

    public IReadOnlyList<WylType> Take(int count) => [.. Enumerable.Range(0, count).Select(_ => Make(Place.Parameter, depth: 3))];

    WylType Make(Place place, int depth)
    {
        while (true)
        {
            int kind = depth == 0 ? random.Next(3) : random.Next(10);
            var made = kind switch
            {
                0 or 1 => Builtins[random.Next(Builtins.Length)],
                2 => random.Next(4) == 0 ? parameters[random.Next(parameters.Count)].Type : place == Place.Return && random.Next(3) == 0 ? WylType.Void : null,
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

    WylType? Named(int depth)
    {
        var symbol = symbols[random.Next(symbols.Length)];
        if (!symbol.IsGeneric)
            return symbol.Type;
        if (symbol == program.Stdlib.Weak)
            return WeakTarget(depth) is { } target ? symbol.Construct([target]) : null;
        List<WylType> arguments = [.. symbol.TypeParameters.Select(_ => Make(Place.Storable, depth - 1))];
        var map = TypeMap.Empty.With(symbol.TypeParameters, arguments);
        bool satisfied = symbol.TypeParameters.Zip(arguments).All(pair => pair.First.Bounds.All(b => program.Stdlib.SatisfiesBound(pair.Second, map.Apply(b))));
        return satisfied ? symbol.Construct(arguments) : null;
    }

    WylType? WeakTarget(int depth)
    {
        for (int i = 0; i < 20; i++)
        {
            if (Make(Place.Storable, depth - 1) is { IsReference: true, IsNullable: false } target)
                return target;
        }
        return null;
    }

    WylType? Nullable(Place place, int depth)
    {
        var inner = Make(Place.Storable, depth - 1);
        if (!inner.CanBeNullable)
            return null;
        // Only a parameter can hold a nullable non-escaping function.
        return place == Place.Parameter && inner is FunctionType function && random.Next(2) == 0 ? function.NonEscaping.Nullable : inner.Nullable;
    }

    WylType Function(Place place, int depth)
    {
        var functionParameters = Enumerable.Range(0, random.Next(4)).Select(_ => Parameter(depth - 1)).ToList();
        var function = Make(Place.Return, depth - 1).FunctionReturning(functionParameters);
        if (random.Next(2) == 0)
            function = function.Shared;
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

    WylType? NullableReference(int depth)
    {
        var referenced = Make(Place.Storable, depth - 1);
        // A reference to a function value would be a place nothing can have, so no 'ref?' or 'in?'.
        return referenced.IsReference || referenced.NonNull.IsFunction ? null : referenced.NullableReference(random.Next(2) == 0 ? RefKind.Ref : RefKind.In);
    }
}
