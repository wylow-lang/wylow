// The values x operations matrix: a value at an edge crossed with what is done with it. The edges
// are 0, 1, -1, INT_MAX, INT_MIN, a long past INT_MAX, -0.0 and an inexact double, an empty array,
// a one-element list, the iterators of an empty list, a one-element list, a list holding a null,
// and an empty array, the element iterators of an empty chain and a one-element chain, the
// iterator adapters and terminal operations over an empty list, a one-element list, and the
// one-element element iterators, the last index, the empty and multi-byte strings, the code point past ASCII,
// true, null as a value and as a reference, and an unequal pair. An operation is arithmetic,
// comparison, indexing, iteration, conversion, formatting, or switch. A row only pairs with the
// operations its value supports, so each cell is a meaningful program; a cell where the spec calls
// for an error or a panic records that instead.
static class MatrixValues
{
    static readonly string[] Operations =
    [
        "arithmetic", "comparison", "indexing", "iteration", "conversion", "formatting", "switch",
    ];

    public static Matrix Build()
    {
        var cells = new List<MatrixCell>();
        foreach (var row in Rows())
        {
            foreach (var operation in Operations)
            {
                if (!row.Bodies.TryGetValue(operation, out var body))
                    continue;
                var source = $"// matrix: values x operations, cell: {row.Name}/{operation}\n\n{Fixture}\n\nvoid main() {{\n{row.Setup}\n{body}\n}}{row.Declarations}";
                cells.Add(new MatrixCell($"{row.Name}/{operation}", source));
            }
        }
        return new Matrix(
            "values",
            "values x operations: a value at an edge, crossed with what is done with it.",
            cells);
    }

    /// <param name="Declarations">Types only the row uses, after <c>main</c>, so that the lines of every other cell stay where they are.</param>
    sealed record ValueRow(string Name, string Setup, Dictionary<string, string> Bodies, string Declarations = "");

    static IEnumerable<ValueRow> Rows()
    {
        yield return Numeric("zero", "0", """
            print(value + 1);
            print(value - 1);
            print(value * 5);
            print(value << 4);
            print(value >> 1);
            print(-value);
            print(7 / value);
            """);
        yield return Numeric("one", "1", """
            print(value + 1);
            print(value - 1);
            print(value * 5);
            print(value / 2);
            print(value % 2);
            print(value << 3);
            print(value >> 1);
            print(-value);
            """);
        yield return Numeric("minus-one", "-1", """
            print(value + 1);
            print(value - 1);
            print(value * 5);
            print(value / 2);
            print(value % 2);
            print(value << 1);
            print(value >> 1);
            print(-value);
            """);
        yield return Numeric("max", "2147483647", """
            print(value - 1);
            print(value / 2);
            print(value % 7);
            print(value >> 1);
            print(value + 1);
            """);
        yield return Numeric("min", "-2147483648", """
            print(value + 1);
            print(value / 2);
            print(value % 3);
            print(value >> 1);
            print(-value);
            """);

        yield return new("long-past-int", "long value = 2147483648L;", new()
        {
            ["arithmetic"] = """
                print(value + 1);
                print(value - 1);
                print(value * 2);
                print(value / 3);
                print(value % 3);
                print(value << 1);
                print(value >> 1);
                print(-value);
                print(value * value * 4);
                """,
            ["comparison"] = """
                print(value > 2147483647L);
                print(value == 2147483648L);
                print(value * -1 < 0);
                print(value >= 0);
                """,
            ["conversion"] = """
                print((int)value);
                print((uint)value);
                print((double)value);
                print((float)value);
                print((ulong)value);
                print(value.toString());
                """,
            ["formatting"] = """
                print($"{value:D12}");
                print($"{value:X8}");
                print($"{value:B40}");
                print($"[{value,12}]");
                """,
            ["switch"] = """
                print(switch (value) {
                    case 2147483647L -> "int max";
                    case 2147483648L -> "past int";
                    default -> "other";
                });
                """,
        });

        yield return new("double-zero", "double value = -0.0d;", new()
        {
            ["arithmetic"] = """
                print(value + 1.0d);
                print(value * 5.0d);
                print(value / 2.0d);
                print(value % 1.5d);
                print(-value);
                print(value + -0.0d);
                print(1.0d / value);
                """,
            ["comparison"] = """
                print(value == 0.0d);
                print(value < 0.0d);
                print(value <= 0.0d);
                print(value != 0.0d);
                print(value == -0.0d);
                print(value >= 0.0d);
                """,
            ["conversion"] = """
                print((int)value);
                print((long)value);
                print((float)value);
                print((double)value);
                print(value.toString());
                """,
            ["formatting"] = """
                print($"{value}");
                print($"{value:F2}");
                print($"{value:F0}");
                print($"{value:E2}");
                print($"[{value,6}]");
                """,
        });

        yield return new("double-inexact", "double value = 0.1d + 0.2d;", new()
        {
            ["arithmetic"] = """
                print(value);
                print(value - 0.3d);
                print(value * 3.0d);
                print(value / 3.0d);
                """,
            ["comparison"] = """
                print(value == 0.3d);
                print(value > 0.3d);
                print(value < 0.3d);
                print(value != 0.3d);
                print(value >= 0.3d);
                """,
            ["conversion"] = """
                print((float)value);
                print((int)value);
                print((long)value);
                print((double)value);
                """,
            ["formatting"] = """
                print($"{value:F1}");
                print($"{value:F20}");
                print($"{value:E3}");
                """,
        });

        yield return new("empty-array", "int[] values = new int[0];", new()
        {
            ["indexing"] = """
                print(values.length);
                print(values[0]);
                """,
            ["iteration"] = """
                mut int count = 0;
                for (int value : values) {
                    count++;
                }
                print(count);
                """,
            ["conversion"] = """
                Span<int> view = values;
                print(view.length());
                ReadOnlySpan<int> read = values;
                print(read.length());
                """,
            ["comparison"] = """
                int[] other = new int[0];
                print(values == other);
                print(values == values);
                """,
        });

        yield return new("one-list", """
            var values = new List<int>();
            values.add(4);
            """, new()
        {
            ["indexing"] = """
                print(values[0]);
                print(values[values.count() - 1]);
                print(values[1]);
                """,
            ["iteration"] = """
                mut int count = 0;
                mut int total = 0;
                for (int value : values) {
                    count++;
                    total += value;
                }
                print(count);
                print(total);
                """,
            ["conversion"] = """
                Span<int> view = values;
                print(view.length());
                print(view[0]);
                """,
            ["comparison"] = """
                var other = new List<int>();
                other.add(4);
                print(values == other);
                print(values == values);
                """,
        });

        yield return new("empty-list-iterator", "var values = new List<int>();", new()
        {
            ["iteration"] = """
                mut int count = 0;
                for (int value : values.iterator()) {
                    count++;
                }
                print(count);
                values.add(1);
                print(values.count());
                """,
        });

        yield return new("one-list-iterator", """
            var values = new List<int>();
            values.add(4);
            """, new()
        {
            ["iteration"] = """
                for (int at, int value : values.iterator()) {
                    print(at);
                    print(value);
                }
                mut var iterator = values.iterator();
                print(iterator.moveNext());
                print(iterator.current());
                print(iterator.moveNext());
                print(iterator.moveNext());
                """,
        });

        yield return new("null-element-iterator", """
            var values = new List<int?>();
            values.add(null);
            values.add(3);
            """, new()
        {
            ["iteration"] = """
                for (int at, int? value : values.iterator()) {
                    print(at);
                    print(value ?? -1);
                }
                """,
        });

        yield return new("empty-array-iterator", "int[] values = new int[0];", new()
        {
            ["iteration"] = """
                mut var iterator = values.iterator();
                print(iterator.moveNext());
                print(iterator.current());
                """,
        });

        yield return new("empty-ref-iterator", "Link? first = null;", new()
        {
            ["iteration"] = """
                mut int count = 0;
                for (ref int value : Links(first)) {
                    count++;
                }
                print(count);
                mut var links = Links(first);
                print(links.moveNext());
                print(links.current());
                """,
        }, Chain);

        yield return new("one-ref-iterator", "var first = new Link(4, null);", new()
        {
            ["iteration"] = """
                for (int at, ref int value : Links(first)) {
                    print(at);
                    value += 1;
                }
                for (in int value : Links(first)) {
                    print(value);
                }
                for (mut int value : Links(first)) {
                    value = 0;
                }
                print(first.value);
                mut var links = Links(first);
                print(links.moveNext());
                ref int current = links.current();
                current = 9;
                print(links.moveNext());
                print(first.value);
                """,
        }, Chain);

        yield return new("one-in-iterator", "var first = new Link(4, null);", new()
        {
            ["iteration"] = """
                for (int at, in int value : ReadLinks(first)) {
                    print(at);
                    print(value);
                }
                for (int value : ReadLinks(first)) {
                    print(value + 1);
                }
                """,
        }, Chain);

        yield return new("empty-list-adapters", "var values = new List<int>();", new()
        {
            ["iteration"] = """
                print(values.iterator().filter(v -> true).count());
                print(values.iterator().map(v -> v + 1).first() ?? -1);
                print(values.iterator().take(1).count());
                print(values.iterator().skip(0).count());
                print(values.iterator().zip(values.iterator(), (a, b) -> a + b).count());
                print(values.iterator().sum());
                print(values.iterator().toList().count());
                print(values.iterator().any(v -> true));
                print(values.iterator().all(v -> false));
                print(values.asReadOnlySpan().count(v -> true));
                print(values.any(v -> true));
                print(values.all(v -> false));
                print(values.sum());
                values.add(1);
                print(values.count());
                """,
        });

        yield return new("one-list-adapters", """
            var values = new List<int>();
            values.add(4);
            """, new()
        {
            ["iteration"] = """
                print(values.iterator().filter(v -> v == 4).first() ?? -1);
                print(values.iterator().filter(v -> v != 4).first() ?? -1);
                print(values.iterator().map(v -> v * 2).sum());
                print(values.iterator().take(0).count());
                print(values.iterator().take(1).sum());
                print(values.iterator().skip(1).count());
                print(values.iterator().skip(0).sum());
                print(values.iterator().zip(new List<int>().iterator(), (a, b) -> a + b).count());
                print(values.iterator().zip(values.iterator(), (a, b) -> a * b).sum());
                print(values.iterator().toList()[0]);
                print(values.asReadOnlySpan().first(v -> v > 3) ?? -1);
                print(values.toList()[0]);
                mut var adapted = values.iterator().skip(0);
                print(adapted.moveNext());
                print(adapted.current());
                print(adapted.moveNext());
                """,
        });

        yield return new("one-ref-iterator-adapters", "var first = new Link(4, null);", new()
        {
            ["iteration"] = """
                for (ref int value : Links(first).filter(v -> v == 4)) {
                    value += 1;
                }
                print(first.value);
                for (ref int value : Links(first).skip(1)) {
                    value = 0;
                }
                for (ref int value : Links(first).take(0)) {
                    value = 0;
                }
                print(first.value);
                print(Links(first).count());
                print(Links(first).first() ?? -1);
                print(Links(first).toList()[0]);
                print(Links(null).any(v -> true));
                """,
        }, Chain);

        yield return new("one-in-iterator-adapters", "var first = new Link(4, null);", new()
        {
            ["iteration"] = """
                for (in int value : ReadLinks(first).filter(v -> v > 0).take(1)) {
                    print(value);
                }
                print(ReadLinks(first).map(v -> v * 3).sum());
                print(ReadLinks(first).skip(1).count());
                print(ReadLinks(first).all(v -> v == 4));
                print(ReadLinks(null).first() ?? -1);
                """,
        }, Chain);

        yield return new("last-array", "mut int[3] values = [4, 5, 6];", new()
        {
            ["indexing"] = """
                print(values.length);
                print(values[0]);
                print(values[values.length - 1]);
                """,
            ["iteration"] = """
                for (int at, int value : values) {
                    print(at);
                    print(value);
                }
                """,
            ["conversion"] = """
                Span<int> view = values;
                print(view.length());
                print(view[2]);
                print(view[view.length() - 1]);
                """,
            ["comparison"] = """
                int[3] same = [4, 5, 6];
                int[3] differs = [4, 5, 7];
                print(values == same);
                print(values == differs);
                print(values != differs);
                """,
        });

        yield return new("empty-string", "String text = \"\";", new()
        {
            ["comparison"] = """
                print(text == "");
                print(text == "x");
                print(text != "x");
                print(text.isEmpty());
                print(text.byteLength());
                print(text.charCount());
                print(text.indexOf("") == 0);
                """,
            ["iteration"] = """
                mut int count = 0;
                for (char c : text) {
                    count++;
                }
                print(count);
                """,
            ["conversion"] = """
                print(text.bytes().length());
                print(text.slice(0, 0) == text);
                """,
            ["formatting"] = """
                print($"[{text}]");
                print($"[{text,4}]");
                print($"{text,0}|");
                """,
        });

        yield return new("multibyte-string", "String text = \"héllo\";", new()
        {
            ["comparison"] = """
                print(text == "héllo");
                print(text == "hello");
                print(text.startsWith("hé"));
                print(text.endsWith("llo"));
                print(text.contains("él"));
                print(text.compareTo("hello") > 0);
                print(text.indexOf("l"));
                """,
            ["iteration"] = """
                for (int at, char c : text) {
                    print(at);
                    print(c);
                }
                """,
            ["conversion"] = """
                print(text.byteLength());
                print(text.charCount());
                print(text.bytes().length());
                print(text.bytes()[1]);
                print(text.slice(1, 2));
                """,
            ["formatting"] = """
                print($"[{text,7}]");
                """,
        });

        yield return new("char-multibyte", "char value = 'é';", new()
        {
            ["arithmetic"] = """
                print(value + 1);
                print(value - 'a');
                print((char)(value + 1));
                """,
            ["comparison"] = """
                print(value == 'é');
                print(value < 'z');
                print(value > 'a');
                print(value != 'e');
                """,
            ["conversion"] = """
                print((int)value);
                print((long)value);
                print((char)233);
                """,
            ["formatting"] = """
                print($"{value}");
                print($"[{value,3}]");
                """,
            ["switch"] = """
                print(switch (value) {
                    case 'é' -> "e acute";
                    case 'a' -> "a";
                    default -> "other";
                });
                """,
        });

        yield return new("bool", "bool value = true;", new()
        {
            ["arithmetic"] = """
                print(value & false);
                print(value | false);
                print(value ^ true);
                print(!value);
                """,
            ["comparison"] = """
                print(value == true);
                print(value == false);
                print(value != false);
                print(!value);
                """,
            ["formatting"] = """
                print($"{value}");
                print($"[{value,6}]");
                print($"[{value,-6}]");
                """,
            ["switch"] = """
                print(switch (value) {
                    case true -> "yes";
                    case false -> "no";
                });
                """,
        });

        yield return new("null-value", "int? value = null;", new()
        {
            ["comparison"] = """
                print(value == null);
                print(value != null);
                print(value == 0);
                print(value != 0);
                print(value ?? -1);
                """,
            ["conversion"] = """
                print($"{value}");
                print(value is int);
                if (value is int found) {
                    print(found);
                } else {
                    print("none");
                }
                """,
            ["formatting"] = """
                print($"{value}");
                print($"[{value,6}]");
                print($"{value:D3}");
                """,
        });

        yield return new("null-reference", "Cell? value = null;", new()
        {
            ["comparison"] = """
                print(value == null);
                print(value != null);
                print(value == value);
                print(value?.value ?? -1);
                """,
            ["conversion"] = """
                print($"{value}");
                print(value is Cell);
                if (value is Cell cell) {
                    print(cell.value);
                } else {
                    print("none");
                }
                """,
            ["formatting"] = """
                print($"{value}");
                print($"[{value,6}]");
                """,
            ["switch"] = """
                print(switch (value) {
                    case Cell cell -> "cell";
                    case null -> "null";
                });
                """,
        });

        yield return new("unequal", """
            int left = 3;
            int right = 4;
            """, new()
        {
            ["comparison"] = """
                print(left == right);
                print(left != right);
                print(left < right);
                print(left <= right);
                print(left > right);
                print(left >= right);
                print('a' < 'b');
                print(true == false);
                """,
        });
    }

    static ValueRow Numeric(string name, string literal, string arithmetic) => new(name, $"int value = {literal};", new()
    {
        ["arithmetic"] = arithmetic,
        ["comparison"] = """
            print(value == 0);
            print(value != 0);
            print(value < 0);
            print(value <= 0);
            print(value > 0);
            print(value >= 0);
            print(value == 1);
            print(value < 1);
            """,
        ["indexing"] = """
            int[3] a = [7, 8, 9];
            print(a[value]);
            """,
        ["conversion"] = """
            print((long)value);
            print((double)value);
            print((float)value);
            print((uint)value);
            print((byte)value);
            print($"{value}");
            print(value.toString());
            """,
        ["formatting"] = """
            print($"{value:D5}");
            print($"{value:X4}");
            print($"{value:B8}");
            print($"[{value,8}]");
            print($"[{value,-8}]");
            """,
        ["switch"] = """
            print(switch (value) {
                case 0 -> "zero";
                case 1 -> "one";
                case -1 -> "minus one";
                default -> "other";
            });
            """,
    });

    const string Fixture = """
        final class Cell implements Printable {
            mut int value = 1;

            Cell(int value) {
                this.value = value;
            }

            pub String toString() {
                return $"Cell({value})";
            }
        }
        """;

    /// <summary>A chain of objects, and its element iterators, which return references into the objects.</summary>
    const string Chain = """


        final class Link {
            pub mut int value;
            pub Link? next;

            Link(int value, Link? next) {
                this.value = value;
                this.next = next;
            }
        }

        struct Links implements RefIterator<int> {
            Link? upcoming;
            Link? at;

            Links(Link? first) {
                upcoming = first;
            }

            pub mut bool moveNext() {
                at = upcoming;
                if (at is Link link) {
                    upcoming = link.next;
                }
                return at != null;
            }

            pub mut ref int current() {
                return ref at!.value;
            }
        }

        struct ReadLinks implements InIterator<int> {
            Link? upcoming;
            Link? at;

            ReadLinks(Link? first) {
                upcoming = first;
            }

            pub mut bool moveNext() {
                at = upcoming;
                if (at is Link link) {
                    upcoming = link.next;
                }
                return at != null;
            }

            pub in int current() {
                return ref at!.value;
            }
        }
        """;
}
