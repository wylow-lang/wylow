// The types x contexts matrix: each kind of type crossed with where it can appear. A type is a
// struct, class, enum with data, nullable value or reference, fixed array, escaping or non-escaping
// function, generic instantiation, nint, or a nesting of two; a context is a local, field, parameter,
// return, type argument, element, capture, `==`, default value, or an overload. Each cell makes a
// value and shows something about it, so the snapshot records what the type does in that place; a
// cell the spec refuses records the error instead.
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
            "types x contexts: each kind of type in each place it can appear.",
            cells);
    }

    /// <summary>One kind of type, with how to make a value of it and show something about it.</summary>
    sealed record TypeRow(
        string Name,
        string Type,
        string ParameterType,
        string ArrayType,
        Func<string, int, string> Declare,
        Func<string, string> Show);

    static IEnumerable<TypeRow> Rows()
    {
        yield return new("struct", "Vec", "Vec", "Vec[2]",
            (name, value) => $"Vec {name} = Vec(x: {value});",
            expr => $"{expr}.x");
        yield return new("class", "Cell", "Cell", "Cell[2]",
            (name, value) => $"Cell {name} = new Cell({value});",
            expr => $"{expr}.value");
        yield return new("enum", "Shape", "Shape", "Shape[2]",
            (name, value) => $"Shape {name} = Shape.Circle({value});",
            expr => $"radiusOf({expr})");
        yield return new("nullable-value", "int?", "int?", "int?[2]",
            (name, value) => $"int? {name} = {value};",
            expr => $"{expr} ?? -1");
        yield return new("nullable-reference", "Cell?", "Cell?", "Cell?[2]",
            (name, value) => $"Cell? {name} = new Cell({value});",
            expr => $"{expr}?.value ?? -1");
        // The first length is the outer one, so two int[3]s are 'int[2][3]'.
        yield return new("fixed-array", "int[3]", "int[3]", "int[2][3]",
            (name, value) => $"int[3] {name} = [{value}, 2, 3];",
            expr => $"{expr}[0]");
        yield return new("function-escaping", "() -> int", "@Escaping () -> int", "(() -> int)[2]",
            (name, value) => $"@Escaping () -> int {name} = () -> {value};",
            expr => $"{expr}()");
        yield return new("function-nonescaping", "() -> int", "() -> int", "(() -> int)[2]",
            (name, value) => $"() -> int {name} = () -> {value};",
            expr => $"{expr}()");
        yield return new("generic", "Box<int>", "Box<int>", "Box<int>[2]",
            (name, value) => $"Box<int> {name} = Box<int>({value});",
            expr => $"{expr}.value");
        yield return new("nint", "nint", "nint", "nint[2]",
            (name, value) => $"nint {name} = {value};",
            expr => $"(int){expr}");
        yield return new("nested", "Box<Vec?>", "Box<Vec?>", "Box<Vec?>[2]",
            (name, value) => $"Box<Vec?> {name} = Box<Vec?>(Vec(x: {value}));",
            expr => $"{expr}.value?.x ?? -1");
    }

    static string Cell(TypeRow row, string context) => context switch
    {
        "local" => $$"""
            void main() {
                {{row.Declare("value", 3)}}
                print({{row.Show("value")}});
            }
            """,
        "field" => $$"""
            class Holder {
                internal {
                    mut {{row.Type}} value;

                    Holder({{row.ParameterType}} value) {
                        this.value = value;
                    }
                }
            }

            void main() {
                {{row.Declare("value", 3)}}
                var holder = new Holder(value);
                print({{row.Show("holder.value")}});
                {{row.Declare("other", 7)}}
                holder.value = other;
                print({{row.Show("holder.value")}});
            }
            """,
        "parameter" => $$"""
            void use({{row.ParameterType}} value) {
                print({{row.Show("value")}});
            }

            void main() {
                {{row.Declare("value", 3)}}
                use(value);
            }
            """,
        "return" => $$"""
            {{row.Type}} get() {
                {{row.Declare("value", 3)}}
                return value;
            }

            void main() {
                print({{row.Show("get()")}});
            }
            """,
        "type-argument" => $$"""
            void main() {
                {{row.Declare("value", 3)}}
                Box<{{row.Type}}> box = Box<{{row.Type}}>(value);
                print({{row.Show("box.value")}});
            }
            """,
        "element" => $$"""
            void main() {
                {{row.Declare("value", 3)}}
                {{row.Declare("other", 7)}}
                mut {{row.ArrayType}} values = [value, value];
                print({{row.Show("values[0]")}});
                values[1] = other;
                print({{row.Show("values[1]")}});
            }
            """,
        "capture" => $$"""
            void main() {
                {{row.Declare("value", 3)}}
                @Escaping () -> int captured = () -> {{row.Show("value")}};
                print(captured());
            }
            """,
        "equality" => $$"""
            void main() {
                {{row.Declare("a", 3)}}
                {{row.Declare("b", 3)}}
                print(a == b);
            }
            """,
        "default" => $$"""
            struct Defaults {
                internal {
                    {{row.Type}} value;
                }
            }

            void main() {
                Defaults defaults = Defaults();
                print({{row.Show("defaults.value")}});
            }
            """,
        "overloads" => $$"""
            String pick({{row.ParameterType}} value) {
                return "typed";
            }

            String pick(String other) {
                return "other";
            }

            void main() {
                {{row.Declare("value", 3)}}
                print(pick(value));
            }
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(context), context, "no such context"),
    };

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

        int radiusOf(Shape shape) {
            return switch (shape) {
                case Circle(var radius) -> radius;
                case Empty -> -1;
            };
        }
        """;
}
