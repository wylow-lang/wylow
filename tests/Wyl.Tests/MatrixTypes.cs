// The types x contexts matrix: each kind of type crossed with where it can appear. A type is a
// struct, class, enum with data, nullable value or reference, fixed array, escaping or non-escaping
// function, generic instantiation, nint, a nesting of two, or a value kind programs compute with
// (String, double, long). A context is a local, field, parameter, return, type argument, element,
// capture, `==`, default value, or an overload. Each cell makes a value and shows something about
// it, so the snapshot records what the type does in that place; a cell the spec refuses records the
// error instead. Equality compares an equal pair and an unequal one, and a nullable row shows both
// its null and its non-null side.
static class MatrixTypes
{
    static readonly string[] Contexts =
    [
        "local", "field", "parameter", "return", "type-argument", "element", "capture", "equality", "default", "overloads",
    ];

    public static Matrix Build()
    {
        var cells = new List<MatrixCell>();
        foreach (var row in Rows())
        {
            foreach (var context in Contexts)
            {
                var source = $"// matrix: types x contexts, cell: {row.Name}/{context}\n\n{Fixture}\n{Cell(row, context)}";
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
        bool Comparable = true)
    {
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
        yield return new("class", "Cell", "Cell", "Cell[2]", "3", "7",
            raw => $"new Cell({raw})",
            expr => $"print({expr}.value);");
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
        yield return new("function-escaping", "() -> int", "@Escaping () -> int", "(() -> int)[2]", "3", "7",
            raw => $"() -> {raw}",
            expr => $"print({expr}());",
            Comparable: false);
        yield return new("function-nonescaping", "() -> int", "() -> int", "(() -> int)[2]", "3", "7",
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
                        internal {
                            mut {{row.Type}} value;

                            Holder({{row.ParameterType}} value) {
                                this.value = value;
                            }
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
                main.Add(row.Show("box.value"));
                if (row.Nullable)
                {
                    main.Add($"Box<{row.Type}> empty = Box<{row.Type}>(null);");
                    main.Add(row.Show("empty.value"));
                }
                return Wrap(main);

            case "element":
                main.Add(row.Declare("value", row.First));
                main.Add(row.Declare("other", row.Second));
                main.Add($"mut {row.ArrayType} values = [value, value];");
                main.Add(row.Show("values[0]"));
                if (row.Nullable)
                {
                    main.Add("values[1] = null;");
                    main.Add(row.Show("values[1]"));
                }
                main.Add("values[1] = other;");
                main.Add(row.Show("values[1]"));
                return Wrap(main);

            case "capture":
                main.Add(row.Declare("value", row.First));
                main.Add("@Escaping () -> void captured = () -> {");
                main.Add(row.Show("value"));
                main.Add("};");
                main.Add("captured();");
                if (row.Nullable)
                {
                    main.Add(row.DeclareNull("none"));
                    main.Add("@Escaping () -> void nullCaptured = () -> {");
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

            case "default":
                main.Add($"{(row.Nullable ? "mut " : "")}Defaults defaults = Defaults();");
                main.Add(row.Show("defaults.value"));
                if (row.Nullable)
                {
                    main.Add($"defaults.value = {row.Value(row.First)};");
                    main.Add(row.Show("defaults.value"));
                }
                return $$"""
                    struct Defaults {
                        internal {
                            {{row.Type}} value;
                        }
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

            default:
                throw new ArgumentOutOfRangeException(nameof(context), context, "no such context");
        }
    }

    static string Wrap(List<string> statements) => $"void main() {{\n{string.Join('\n', statements)}\n}}";

    const string Fixture = """
        struct Vec {
            internal {
                int x = 1;
                int y = 2;
            }
        }

        final class Cell {
            internal {
                mut int value = 1;

                Cell(int value) {
                    this.value = value;
                }
            }
        }

        enum Shape {
            Circle(int radius),
            Empty,
        }

        struct Box<T> {
            internal {
                T value;
            }
        }

        struct Other {
            internal {
                int value;
            }
        }

        int radiusOf(Shape shape) {
            return switch (shape) {
                case Circle(var radius) -> radius;
                case Empty -> -1;
            };
        }
        """;
}
