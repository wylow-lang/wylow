using Wyl.Compiler;
using Wyl.Compiler.Analysis;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

// The method a dispatched call runs on an object of a runtime type: the nearest override in its
// class chain, or a struct's own method, which a type parameter's bound dispatches to.
static class DispatchSelfTests
{
    const string Source = """
        abstract class Shape {
            internal {
                abstract int area();
            }
        }

        class Square extends Shape {
            internal {
                override int area() { return 4; }
            }
        }

        interface Named {
            int name();
        }

        class Root implements Named {
            internal {
                int name() { return 1; }
            }
        }

        class Leaf extends Root { }

        struct Point implements Named {
            internal {
                int x;
                int name() { return x; }
            }
        }

        class Box<T> implements Named {
            internal {
                T value;
                Box(T value) { this.value = value; }
                int name() { return 1; }
            }
        }

        void main() { }
        """;

    public static List<TestResult> Run()
    {
        var compilation = Compilation.Compile(new SourceText("dispatch_self_test.wyl", Source));
        if (compilation.Program is not { } program)
            return [new("dispatch/fixture", string.Join('\n', compilation.Diagnostics))];
        var overrides = new Overrides(program);
        var types = program.Types.ToDictionary(t => t.Name);
        WylType Type(string name) => types[name].Type;
        WylType Construct(string name, params WylType[] arguments) => types[name].Construct(arguments);
        MethodReference Method(WylType owner, string name) => new(owner.Symbol!.Methods[name][0], owner, []);

        string? Run(string expected, MethodReference call, WylType runtime, WylType expectedOwner)
        {
            if (overrides.Implementation(call, runtime) is not { } found)
                return $"'{call.Symbol.Name}' on '{runtime}' ran nothing, expected '{expected}'";
            if (found.Symbol.ContainingType?.Name != expected || found.Owner != expectedOwner)
                return $"'{call.Symbol.Name}' on '{runtime}' ran '{found.Symbol.ContainingType?.Name}.{found.Symbol.Name}' of '{found.Owner}', expected '{expected}' of '{expectedOwner}'";
            return null;
        }

        List<string> problems = [];
        void Check(string name, Func<string?> check)
        {
            if (check() is { } problem)
                problems.Add($"{name}: {problem}");
        }

        var shape = Type("Shape");
        var square = Type("Square");
        var named = Type("Named");
        var root = Type("Root");
        var leaf = Type("Leaf");
        var point = Type("Point");
        var box = Construct("Box", WylType.Int);

        Check("base", () => Run("Shape", Method(shape, "area"), shape, shape));
        Check("override", () => Run("Square", Method(shape, "area"), square, square));
        Check("interface", () => Run("Root", Method(named, "name"), root, root));
        Check("inherited", () => Run("Root", Method(named, "name"), leaf, root));
        Check("struct", () => Run("Point", Method(named, "name"), point, point));
        Check("generic", () => Run("Box", Method(named, "name"), box, box));
        return [new("dispatch/implementation", problems.Count == 0 ? null : string.Join('\n', problems))];
    }
}
