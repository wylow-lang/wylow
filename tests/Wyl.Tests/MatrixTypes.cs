// The types x contexts matrix: each kind of type crossed with where it can appear. A type is a
// struct, class, enum with data, nullable value or reference, fixed array, escaping or non-escaping
// function, generic instantiation, nint, a nesting of two, or a value kind programs compute with
// (String, double, long). A context is a local, field, parameter, return, type argument, element,
// capture, `==`, default value, an overload, a field a struct pattern binds, a switch with
// `case null`, a field a guarded case binds, or an argument of `super(...)` and `this(...)`. Each cell makes a value and shows something about
// it, so the snapshot records what the type does in that place; a cell the spec refuses records the
// error instead. Equality compares an equal pair and an unequal one, and a nullable row shows both
// its null and its non-null side.
static class MatrixTypes
{
    static readonly string[] Contexts =
    [
        "local", "field", "parameter", "return", "type-argument", "element", "capture", "equality", "default", "overloads",
        "struct-pattern", "case-null", "guarded", "constructor-call",
    ];

    public static Matrix Build()
    {
        var cells = new List<MatrixCell>();
        foreach (var row in Rows())
        {
            foreach (var context in Contexts)
            {
                var extra = row.Declarations.Length > 0 ? row.Declarations + "\n" : "";
                var source = $"// matrix: types x contexts, cell: {row.Name}/{context}\n\n{Fixture}\n{extra}{Cell(row, context)}";
                cells.Add(new MatrixCell($"{row.Name}/{context}", source));
            }
        }
        return new Matrix(
            "types",
            "types x contexts: each kind of type, or of value, in each place it can appear.",
            cells);
    }

    /// <summary>One kind of type, with how to make a value of it and show something about it.</summary>
    sealed record TypeRow(
        string Name,
        string Type,
        string ParameterType,
        string ArrayType,
        string First,
        string Second,
        Func<string, string> Value,
        Func<string, string> Show,
        bool Nullable = false,
        bool Comparable = true,
        bool Unique = false,
        string Declarations = "")
    {
        /// <summary>The modifier a struct declaration of the row's type needs: unique types hold a unique field.</summary>
        public string StructModifier => Unique ? "unique " : "";

        /// <summary>
        /// A local holding the row's value. A local is written like a parameter, so a function type
        /// takes the row's parameter form; <paramref name="raw"/> is a value expression.
        /// </summary>
        public string Declare(string name, string raw) => $"{ParameterType} {name} = {Value(raw)};";

        /// <summary>A local that may be reassigned, which an escaping lambda cannot capture.</summary>
        public string DeclareMutable(string name, string raw) => $"mut {ParameterType} {name} = {Value(raw)};";

        /// <summary>A local of the row's type holding null.</summary>
        public string DeclareNull(string name) => $"{ParameterType} {name} = null;";
    }

    static IEnumerable<TypeRow> Rows()
    {
        yield return new("struct", "Vec", "Vec", "Vec[2]", "3", "7",
            raw => $"Vec(x: {raw})",
            expr => $"print({expr}.x);");
        yield return new("unique", "Unique", "Unique", "Unique[2]", "3", "7",
            raw => $"Unique(x: {raw})",
            expr => $"print({expr}.x);",
            Unique: true,
            Declarations: "unique struct Unique {\n    int x = 1;\n}");
        // A unique struct with a destructor can be moved like any unique value, and its destructor
        // runs wherever a value of it is released.
        yield return new("unique-destructor", "Tracked", "Tracked", "Tracked[2]", "3", "7",
            raw => $"Tracked(x: {raw})",
            expr => $"print({expr}.x);",
            Unique: true,
            Declarations: "unique struct Tracked {\n    int x = 1;\n\n    ~Tracked() { print($\"tracked {x}\"); }\n}");
        yield return new("class", "Cell", "Cell", "Cell[2]", "3", "7",
            raw => $"new Cell({raw})",
            expr => $"print({expr}.value);");
        yield return new("shared-class", "Shared", "Shared", "Shared[2]", "3", "7",
            raw => $"new Shared({raw})",
            expr => $"print({expr}.value);",
            Declarations: "shared class Shared {\n    int value;\n\n    Shared(int value) {\n        this.value = value;\n    }\n}");
        yield return new("enum", "Shape", "Shape", "Shape[2]", "3", "7",
            raw => $"Shape.Circle({raw})",
            expr => $"print(radiusOf({expr}));");
        yield return new("nullable-value", "int?", "int?", "int?[2]", "3", "7",
            raw => raw,
            expr => $"print({expr} ?? -1);",
            Nullable: true);
        yield return new("nullable-reference", "Cell?", "Cell?", "Cell?[2]", "3", "7",
            raw => $"new Cell({raw})",
            expr => $"print({expr}?.value ?? -1);",
            Nullable: true);
        yield return new("fixed-array", "int[3]", "int[3]", "int[2][3]", "3", "7",
            raw => $"[{raw}, 2, 3]",
            expr => $"print({expr}[0]);");
        yield return new("function-escaping", "fn() -> int", "escaping fn() -> int", "(fn() -> int)[2]", "3", "7",
            raw => $"() -> {raw}",
            expr => $"print({expr}());",
            Comparable: false);
        yield return new("function-nonescaping", "fn() -> int", "fn() -> int", "(fn() -> int)[2]", "3", "7",
            raw => $"() -> {raw}",
            expr => $"print({expr}());",
            Comparable: false);
        yield return new("function-shared-escaping", "shared fn() -> int", "escaping shared fn() -> int", "(shared fn() -> int)[2]", "3", "7",
            raw => $"() -> {raw}",
            expr => $"print({expr}());",
            Comparable: false);
        yield return new("function-shared-nonescaping", "shared fn() -> int", "shared fn() -> int", "(shared fn() -> int)[2]", "3", "7",
            raw => $"() -> {raw}",
            expr => $"print({expr}());",
            Comparable: false);
        yield return new("generic", "Box<int>", "Box<int>", "Box<int>[2]", "3", "7",
            raw => $"Box<int>({raw})",
            expr => $"print({expr}.value);");
        yield return new("nint", "nint", "nint", "nint[2]", "3", "7",
            raw => raw,
            expr => $"print((int){expr});");
        yield return new("nested", "Box<Vec?>", "Box<Vec?>", "Box<Vec?>[2]", "3", "7",
            raw => $"Box<Vec?>(Vec(x: {raw}))",
            expr => $"print({expr}.value?.x ?? -1);");

        // Value kinds: what programs compute with, which a row per type kind leaves out.
        yield return StringRow("string-empty", "\"\"", "\"x\"");
        yield return StringRow("string-one", "\"é\"", "\"e\"");
        yield return StringRow("string-multibyte", "\"héllo\"", "\"hello\"");
        yield return StringRow("string-concat", "$\"ab{1}cd\"", "$\"ab{2}cd\"");
        yield return new("double", "double", "double", "double[2]", "-0.0d", "1.5d",
            raw => raw,
            expr => $"print({expr});\nprint({expr} % 1.5d);");
        yield return new("double-inexact", "double", "double", "double[2]", "0.1d + 0.2d", "0.3d",
            raw => raw,
            expr => $"print({expr});");
        yield return new("long", "long", "long", "long[2]", "2147483648L", "-2147483649L",
            raw => raw,
            expr => $"print({expr});");

        static TypeRow StringRow(string name, string first, string second) => new(
            name, "String", "String", "String[2]", first, second,
            raw => raw,
            expr => $"print({expr});\nprint({expr}.byteLength());\nprint({expr}.charCount());\nprint({expr}.isEmpty());");
    }

    static string Cell(TypeRow row, string context)
    {
        var main = new List<string>();
        switch (context)
        {
            case "local":
                main.Add(row.Nullable ? row.DeclareMutable("value", row.First) : row.Declare("value", row.First));
                main.Add(row.Show("value"));
                if (row.Nullable)
                {
                    main.Add("value = null;");
                    main.Add(row.Show("value"));
                }
                return Wrap(main);

            case "field":
                main.Add(row.Declare("value", row.First));
                main.Add("var holder = new Holder(value);");
                main.Add(row.Show("holder.value"));
                if (row.Nullable)
                {
                    main.Add("holder.value = null;");
                    main.Add(row.Show("holder.value"));
                }
                main.Add(row.Declare("other", row.Second));
                main.Add("holder.value = other;");
                main.Add(row.Show("holder.value"));
                return $$"""
                    class Holder {
                        mut {{row.Type}} value;

                        Holder({{row.ParameterType}} value) {
                            this.value = value;
                        }
                    }

                    {{Wrap(main)}}
                    """;

            case "parameter":
                main.Add(row.Declare("value", row.First));
                main.Add("use(value);");
                if (row.Nullable)
                    main.Add("use(null);");
                return $$"""
                    void use({{row.ParameterType}} value) {
                    {{row.Show("value")}}
                    }

                    {{Wrap(main)}}
                    """;

            case "return":
                if (row.Nullable)
                {
                    main.Add(row.Show("get(true)"));
                    main.Add(row.Show("get(false)"));
                    return $$"""
                        {{row.Type}} get(bool present) {
                            if (present) {
                                {{row.Declare("value", row.First)}}
                                return value;
                            }
                            return null;
                        }

                        {{Wrap(main)}}
                        """;
                }
                main.Add(row.Show("get()"));
                return $$"""
                    {{row.Type}} get() {
                        {{row.Declare("value", row.First)}}
                        return value;
                    }

                    {{Wrap(main)}}
                    """;

            case "type-argument":
                main.Add(row.Declare("value", row.First));
                main.Add($"Box<{row.Type}> box = Box<{row.Type}>(value);");
                // A unique value moves into the box and back out through a pattern, since a field
                // is read out only where its type is copyable.
                if (row.Unique)
                {
                    main.Add("switch (box) {");
                    main.Add($"case Box<{row.Type}>(var field) -> {{");
                    main.Add(row.Show("field"));
                    main.Add("}");
                    main.Add("}");
                }
                else
                {
                    main.Add(row.Show("box.value"));
                }
                if (row.Nullable)
                {
                    main.Add($"Box<{row.Type}> empty = Box<{row.Type}>(null);");
                    main.Add(row.Show("empty.value"));
                }
                return Wrap(main);

            case "element":
                main.Add(row.Declare("value", row.First));
                main.Add(row.Declare("other", row.Second));
                // A unique value moves into its element, so each element is a value of its own.
                main.Add($"mut {row.ArrayType} values = [{(row.Unique ? "value, other" : "value, value")}];");
                main.Add(row.Show("values[0]"));
                if (row.Nullable)
                {
                    main.Add("values[1] = null;");
                    main.Add(row.Show("values[1]"));
                }
                if (row.Unique)
                {
                    main.Add(row.Declare("replacement", row.Second));
                    main.Add("values[1] = replacement;");
                }
                else
                {
                    main.Add("values[1] = other;");
                }
                main.Add(row.Show("values[1]"));
                return Wrap(main);

            case "capture":
                main.Add(row.Declare("value", row.First));
                main.Add("escaping fn() -> void captured = () -> {");
                main.Add(row.Show("value"));
                main.Add("};");
                main.Add("captured();");
                if (row.Nullable)
                {
                    main.Add(row.DeclareNull("none"));
                    main.Add("escaping fn() -> void nullCaptured = () -> {");
                    main.Add(row.Show("none"));
                    main.Add("};");
                    main.Add("nullCaptured();");
                }
                return Wrap(main);

            case "equality":
                main.Add(row.Declare("a", row.First));
                main.Add(row.Declare("b", row.First));
                main.Add("print(a == b);");
                if (row.Nullable)
                {
                    main.Add(row.DeclareNull("none"));
                    main.Add("print(a == none);");
                    main.Add("print(none == none);");
                }
                if (row.Comparable)
                {
                    main.Add(row.Declare("c", row.Second));
                    main.Add("print(a == c);");
                    main.Add("print(a != c);");
                }
                return Wrap(main);

            case "constructor-call":
                // Passed to super(...) as a parameter, which needs no statements; to this(...) as
                // a call's value, computed in the mem-initializer; and beside a second computed
                // argument, which takes the arguments constructor. A struct chains too.
                main.Add(row.Declare("value", row.First));
                main.Add("var passed = new Made(value);");
                main.Add(row.Show("passed.value"));
                main.Add("var computed = new Made(Pick());");
                main.Add(row.Show("computed.value"));
                main.Add("var both = new Made(Pick(), 2);");
                main.Add(row.Show("both.value"));
                main.Add("print(both.extra);");
                main.Add("var kept = Kept(Pick());");
                main.Add(row.Show("kept.value"));
                return $$"""
                    class Base {
                        {{row.Type}} value;
                        int extra;

                        Base({{row.ParameterType}} value, int extra) {
                            this.value = value;
                            this.extra = extra;
                        }
                    }

                    class Made extends Base {
                        Made({{row.ParameterType}} value) {
                            super(value, 0);
                        }

                        Made(Pick pick) {
                            this(made());
                        }

                        Made(Pick pick, int m) {
                            this(made(), m + 1);
                        }

                        Made({{row.ParameterType}} value, int extra) {
                            super(value, extra);
                        }
                    }

                    {{row.StructModifier}}struct Kept {
                        {{row.Type}} value;

                        Kept({{row.ParameterType}} value) {
                            this.value = value;
                        }

                        Kept(Pick pick) {
                            this(made());
                        }
                    }

                    // Selects the constructors that chain, with a type no row is.
                    struct Pick { }

                    {{row.Type}} made() {
                        {{row.Declare("value", row.Second)}}
                        return value;
                    }

                    {{Wrap(main)}}
                    """;

            case "default":
                main.Add($"{(row.Nullable ? "mut " : "")}Defaults defaults = Defaults();");
                main.Add(row.Show("defaults.value"));
                if (row.Nullable)
                {
                    main.Add($"defaults.value = {row.Value(row.First)};");
                    main.Add(row.Show("defaults.value"));
                }
                return $$"""
                    {{row.StructModifier}}struct Defaults {
                        {{row.Type}} value;
                    }

                    {{Wrap(main)}}
                    """;

            case "overloads":
                main.Add(row.Declare("value", row.First));
                main.Add("print(pick(value));");
                if (row.Nullable)
                    main.Add("print(pick(null));");
                return $$"""
                    String pick({{row.ParameterType}} value) {
                        return "typed";
                    }

                    String pick(Other other) {
                        return "other";
                    }

                    {{Wrap(main)}}
                    """;

            case "struct-pattern":
                main.Add(row.Declare("value", row.First));
                main.Add("Holds held = Holds(value, 1);");
                main.Add("switch (held) {");
                main.Add("case Holds(var bound, 1) -> {");
                main.Add(row.Show("bound"));
                main.Add("}");
                main.Add("default -> print(\"other\");");
                main.Add("}");
                // A unique value is moved by the case, so the nullable one is another value.
                main.Add(row.Unique ? $"Holds? maybe = Holds({row.Value(row.Second)}, 2);" : "Holds? maybe = held;");
                main.Add("if (maybe is Holds(var copied, _)) {");
                main.Add(row.Show("copied"));
                main.Add("}");
                if (row.Nullable)
                {
                    main.Add("Holds empty = Holds(null, 2);");
                    main.Add("if (empty is Holds(var none, 2)) {");
                    main.Add(row.Show("none"));
                    main.Add("}");
                }
                return $$"""
                    {{row.StructModifier}}struct Holds {
                        {{row.Type}} value;
                        int tag;
                    }

                    {{Wrap(main)}}
                    """;

            case "guarded":
                // A case whose guard fails binds its own copy; the next one's guard passes.
                main.Add(row.Declare("value", row.First));
                main.Add("Holds held = Holds(value, 1);");
                main.Add("switch (held) {");
                main.Add("case Holds(var skipped, var tag) when tag == 2 -> print(\"wrong\");");
                main.Add("case Holds(var bound, var tag) when tag == 1 -> {");
                main.Add(row.Show("bound"));
                main.Add("}");
                main.Add("default -> print(\"other\");");
                main.Add("}");
                return $$"""
                    {{row.StructModifier}}struct Holds {
                        {{row.Type}} value;
                        int tag;
                    }

                    {{Wrap(main)}}
                    """;

            case "case-null":
            {
                // A nullable row is matched as it is; another is made nullable.
                var nullable = row.Nullable ? row.Type : row.Type.StartsWith("fn") ? $"({row.Type})?" : $"{row.Type}?";
                foreach (var some in new[] { "true", "false" })
                {
                    main.Add($"switch (maybe({some})) {{");
                    main.Add("case null -> print(\"null\");");
                    main.Add("case var v -> {");
                    if (row.Nullable)
                    {
                        main.Add($"{row.ParameterType} shown = v;");
                        main.Add(row.Show("shown"));
                    }
                    else
                    {
                        main.Add(row.Show("v"));
                    }
                    main.Add("}");
                    main.Add("}");
                }
                return $$"""
                    {{nullable}} maybe(bool some) {
                        if (some) {
                            return {{row.Value(row.First)}};
                        }
                        return null;
                    }

                    {{Wrap(main)}}
                    """;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(context), context, "no such context");
        }
    }

    static string Wrap(List<string> statements) => $"void main() {{\n{string.Join('\n', statements)}\n}}";

    const string Fixture = """
        struct Vec {
            int x = 1;
            int y = 2;
        }

        final class Cell {
            mut int value = 1;

            Cell(int value) {
                this.value = value;
            }
        }

        enum Shape {
            Circle(int radius),
            Empty,
        }

        struct Box<T> {
            T value;
        }

        struct Other {
            int value;
        }

        int radiusOf(Shape shape) {
            return switch (shape) {
                case Circle(var radius) -> radius;
                case Empty -> -1;
            };
        }
        """;
}
