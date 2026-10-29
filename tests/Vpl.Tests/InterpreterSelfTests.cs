using Vpl.Compiler;
using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Interpret;
using Vpl.Compiler.Text;

// Small programs with what they must print, panic with, or exit with, one per instruction kind and
// per runtime message. They are what says whether the interpreter is wrong when no native mode runs.

static class InterpreterSelfTests
{
    public static List<TestResult> Run()
    {
        var results = new List<TestResult>();

        Case(results, "print", """
            void main() {
                print("hi");
                print(1 + 2);
                print($"[{42,5}] {3.5:F2}");
            }
            """, output: ["hi", "3", "[   42] 3.50"]);

        Case(results, "arithmetic", """
            void main() {
                int a = 7;
                print(a / 2);
                print(a % 2);
                int shift = 33;
                print(1 << shift);
                print(-8 >> 1);
                print(~0);
                print(0xFF & 0x0F);
                print(true & false);
            }
            """, output: ["3", "1", "2", "-4", "-1", "15", "false"]);

        Case(results, "float_text", """
            void main() {
                print(3.0);
                print(1e20);
                print(0.0001);
                print(0.00012);
                print(1.0d / 3.0d);
                print(1.0f / 3.0f);
                print(0.0 / 0.0);
                print(-1.0 / 0.0);
            }
            """, output: ["3.0", "1e+20", "1e-04", "0.00012", "0.3333333333333333", "0.33333334", "nan", "-inf"]);

        Case(results, "conversions", """
            void main() {
                print((int)3.9);
                print((int)1e20);
                print((byte)300);
                print((float)1e300d);
                print((long)'a');
                print((char)65);
                print((long)(char)0x10FFFF);
            }
            """, output: ["3", "2147483647", "44", "inf", "97", "A", "1114111"]);

        Case(results, "control_flow", """
            int sum(int n) {
                mut int total = 0;
                for (mut int i = 0; i < n; i++) {
                    if (i % 2 == 0) { continue; }
                    total += i;
                }
                return total;
            }

            void main() {
                print(sum(10));
                mut int i = 0;
                while (true) {
                    i++;
                    if (i == 3) { break; }
                }
                print(i);
            }
            """, output: ["25", "3"]);

        Case(results, "exit_code", """
            int main() {
                return 7;
            }
            """, output: []);

        Case(results, "panic_explicit", """
            void main() {
                print("before");
                panic("stopped");
            }
            """, panic: "stopped", panicAtLine: 3, output: ["before"]);

        Case(results, "overflow_debug", """
            void main() {
                int x = 2147483647;
                print(x + 1);
            }
            """, panic: "integer overflow", panicAtLine: 3);

        Case(results, "overflow_release", """
            void main() {
                int x = 2147483647;
                print(x + 1);
            }
            """, release: true, output: ["-2147483648"]);

        Case(results, "division_by_zero", """
            void main() {
                int zero = 0;
                print(10 / zero);
            }
            """, panic: "division by zero");

        Case(results, "min_by_minus_one", """
            void main() {
                int min = -2147483648;
                int minusOne = -1;
                print(min / minusOne);
            }
            """, panic: "integer overflow");

        Case(results, "char_cast", """
            void main() {
                int code = 0xD800;
                print((char)code);
            }
            """, panic: "integer is not a Unicode scalar value");

        Case(results, "enum_cast", """
            enum Color { Red, Green }

            void main() {
                print((int)(Color)1);
                int code = 7;
                print((int)(Color)code);
            }
            """, panic: "integer is not a value of enum 'Color'", output: ["1"]);

        Case(results, "structs", """
            struct Vec3 {
                internal {
                    float x;
                    float y;
                    float z;
                }
            }

            struct Tagged {
                internal {
                    int id = 4;
                }
            }

            void main() {
                var v = Vec3(1, 2.5, -3);
                print($"{v.y}");
                var t = Tagged();
                print(t.id);
                var copy = v;
                print(copy.x + copy.z);
                print(Vec3().x);
            }
            """, output: ["2.5", "4", "-2.0", "0.0"]);

        Case(results, "enums", """
            enum Shape {
                Circle(float radius),
                Square(float side),
            }

            float area(Shape shape) {
                return switch (shape) {
                    case Circle(var r) -> 3.0f * r * r;
                    case Square(var s) -> s * s;
                };
            }

            void main() {
                print(area(Shape.Circle(2)));
                print(area(Shape.Square(3)));
                print(Shape.Circle(1) == Shape.Circle(1));
                print(Shape.Circle(1) == Shape.Square(1));
            }
            """, output: ["12.0", "9.0", "true", "false"]);

        Case(results, "nullable_values", """
            void main() {
                int? none = null;
                int? some = 5;
                print(none == null);
                print(some! + 1);
                print(none ?? 9);
                print($"{none} {some}");
            }
            """, output: ["true", "6", "9", "null 5"]);

        Case(results, "nullable_panic", """
            void main() {
                int? none = null;
                print((none!).toString());
            }
            """, panic: "null value unwrapped with '!'");

        Case(results, "fixed_arrays", """
            void main() {
                mut int[3] values = [1, 2, 3];
                print(values[0] + values[2]);
                print(values.length);
                values[1] = 5;
                print(values[1]);
            }
            """, output: ["4", "3", "5"]);

        Case(results, "index_panic", """
            void main() {
                int[3] values = [1, 2, 3];
                int i = 3;
                print(values[i]);
            }
            """, panic: "index 3 is out of bounds for length 3", panicAtLine: 4);

        Case(results, "bounds_check", """
            struct Pair {
                internal {
                    int[2] values;

                    @CallerLocation
                    mut int get(int index) {
                        Bounds.check(index, 2);
                        return values[index];
                    }
                }
            }

            void main() {
                mut Pair p = Pair();
                print(p.get(1));
                print(p.get(2));
            }
            """, panic: "index 2 is out of bounds for length 2", panicAtLine: 16, output: ["0"]);

        Case(results, "interpolation_specifiers", """
            void main() {
                int n = 42;
                print($"{n:D5} {255:X} {5:B8} {-1:X}");
                print($"{3.14159f:F2} {1500.0:E2} {0.00012:E1}");
                int? none = null;
                print($"[{none,6:D2}] [{n,-4}]");
            }
            """, output: ["00042 FF 00000101 FFFFFFFF", "3.14 1.50E+003 1.2E-004", "[  null] [42  ]"]);

        Case(results, "strings", """
            void main() {
                String text = "héllo";
                print(text == "héllo");
                print(text.byteLength());
                print(text.charCount());
                print(text.slice(1, 2));
                print(text.indexOf("llo"));
                print("b".compareTo("a"));
            }
            """, output: ["true", "6", "5", "é", "3", "1"]);

        Case(results, "string_slice_panic", """
            void main() {
                print("abc".slice(4, 1));
            }
            """, panic: "byte range of");

        Case(results, "generics", """
            T identity<T>(T value) {
                return value;
            }

            void main() {
                print(identity(5));
                print(identity("text"));
            }
            """, output: ["5", "text"]);

        Case(results, "nesting", """
            int twice(int value) {
                return value * 2;
            }

            void main() {
                print(twice(twice(3)));
            }
            """, output: ["12"]);

        Case(results, "references", """
            struct Cell {
                internal {
                    int value;

                    mut ref int at() { return ref value; }
                }
            }

            void bump(ref int x) { x += 1; }

            void main() {
                mut Cell c = Cell();
                bump(ref c.value);
                print(c.value);
                c.at() = 7;
                print(c.value);
                ref int r = c.at();
                r = 9;
                print(c.value);
            }
            """, output: ["1", "7", "9"]);

        Case(results, "nullable_reference", """
            int read(in? int value) {
                return value == null ? -1 : value!;
            }

            void main() {
                mut int x = 1;
                ref? int r = x;
                print(r == null);
                r! += 1;
                print(x);
                print(read(null));
                print(read(x));
            }
            """, output: ["false", "2", "-1", "2"]);

        Case(results, "bounds_range", """
            void main() {
                Bounds.checkRange(1, 3, 4);
                print("ok");
                Bounds.checkRange(2, 3, 4);
            }
            """, panic: "range of 3 from 2 is out of bounds for length 4", output: ["ok"]);

        Case(results, "null_reference_panic", """
            void main() {
                ref? int nothing = null;
                print(nothing == null);
                nothing! = 1;
            }
            """, panic: "null reference unwrapped with '!'", output: ["true"]);

        Case(results, "string_slice_boundary", """
            void main() {
                print("é".slice(1, 1));
            }
            """, panic: "byte offset 1 is inside a character");

        return results;
    }

    static void Case(
        List<TestResult> results, string name, string source, string? panic = null, int? panicAtLine = null,
        bool release = false, params string[] output) =>
        results.Add(new TestResult("interpreter/" + name, Check(name, source, output, panic, panicAtLine, release)));

    static string? Check(string name, string source, IReadOnlyList<string> expected, string? panic, int? panicAtLine, bool release)
    {
        var text = new SourceText(name + ".vpl", source);
        var interpreted = Compilation.Interpret(text, new CompileOptions(Optimize: false), new InterpreterOptions(Release: release));
        var errors = interpreted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
            return "did not compile: " + string.Join(" | ", errors);
        if (interpreted.Interpretation is not { } result)
            return "the pipeline produced no IR";
        if (result.Status == InterpretationStatus.Unsupported)
            return "unsupported: " + result.Error.Trim();
        var problems = new List<string>();
        var actual = result.OutputText.Replace("\r\n", "\n").Split('\n').ToList();
        if (actual.Count > 0 && actual[^1] == "")
            actual.RemoveAt(actual.Count - 1);
        if (!actual.SequenceEqual(expected))
            problems.Add($"expected [{string.Join(" | ", expected)}], got [{string.Join(" | ", actual)}]");
        if (panic is not null)
        {
            var at = panicAtLine is { } line ? $"\n    at {name}.vpl:{line}:" : "";
            if (result.Status != InterpretationStatus.Panicked || !result.Error.Contains("panic: " + panic, StringComparison.Ordinal) || !result.Error.Contains(at, StringComparison.Ordinal))
                problems.Add($"expected panic '{panic}'{(at.Length > 0 ? $" at line {panicAtLine}" : "")}, got {result.Status}: {result.Error.Trim()}");
        }
        else if (result.Status != InterpretationStatus.Completed)
        {
            problems.Add($"expected completion, got {result.Status}: {result.Error.Trim()}");
        }
        return problems.Count == 0 ? null : string.Join('\n', problems);
    }
}
