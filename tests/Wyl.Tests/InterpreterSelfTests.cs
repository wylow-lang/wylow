using Wyl.Compiler;
using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Interpret;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Text;
using Wyl.Harness;

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
                float x;
                float y;
                float z;
            }

            struct Tagged {
                int id = 4;
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
                int[2] values;

                @CallerLocation
                mut int get(int index) {
                    Bounds.check(index, 2);
                    return values[index];
                }
            }

            void main() {
                mut Pair p = Pair();
                print(p.get(1));
                print(p.get(2));
            }
            """, panic: "index 2 is out of bounds for length 2", panicAtLine: 14, output: ["0"]);

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
                int value;

                mut ref int at() { return ref value; }
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

        Case(results, "objects", """
            class Counter {
                mut int count = 10;

                Counter(int step) {
                    this.step = step;
                }

                void tick() {
                    count += step;
                }

                int get() {
                    return count;
                }

                int step;
            }

            void main() {
                var a = new Counter(2);
                a.tick();
                print(a.get());
                Counter b = a;
                b.tick();
                print(a.count);
                print(a == b);
                print(a == new Counter(2));
            }
            """, output: ["12", "14", "true", "false"]);

        Case(results, "inheritance_dispatch", """
            interface Named {
                String name();
            }

            abstract class Shape implements Named {
                int sides = 4;

                int perimeter() {
                    return sides * side();
                }

                abstract int side();
            }

            class Square extends Shape {
                int size = 3;

                override int side() {
                    return size;
                }

                String name() {
                    return "square";
                }
            }

            class Unit extends Shape {
                override int side() {
                    return 1;
                }

                String name() {
                    return "unit";
                }
            }

            void show(Named n, Shape? s) {
                print(n.name());
                if (s is Shape shape) {
                    print(shape.perimeter());
                }
            }

            void main() {
                var s = new Square();
                show(s, s);
                print(s.perimeter());
                Shape u = new Unit();
                print(u.perimeter());
                print(u is Square);
                Named named = s;
                print(named.name());
            }
            """, output: ["square", "12", "12", "4", "false", "square"]);

        Case(results, "destruction_order", """
            class Part {
                String name;

                Part(String name) {
                    this.name = name;
                }

                ~Part() {
                    print($"gone {name}");
                }
            }

            class Whole {
                Part first = new Part("first");
                Part second = new Part("second");

                Whole() {
                    print("whole body");
                }

                ~Whole() {
                    print("whole body done");
                }
            }

            void main() {
                mut var w = new Whole();
                w = new Whole();
                print("after");
            }
            """, output: ["whole body", "whole body", "whole body done", "gone second", "gone first", "after", "whole body done", "gone second", "gone first"]);

        Case(results, "assignment_sees_new_value", """
            class Item {
                String name;
                mut Holder? holder;

                Item(String name, Holder? holder) {
                    this.name = name;
                    this.holder = holder;
                }

                ~Item() {
                    if (holder is Holder h && h.item is Item now) {
                        print(now.name);
                    }
                }
            }

            class Holder {
                mut Item? item;
            }

            void main() {
                var h = new Holder();
                h.item = new Item("second", h);
                h.item = new Item("third", h);
            }
            """, output: ["third"]);

        Case(results, "construction_and_destruction_phases", """
            int mark(String text, int value) {
                print(text);
                return value;
            }

            class Base {
                int field = mark("base field", 1);

                Base() {
                    print("base body");
                    whoami();
                }

                virtual void whoami() {
                    print("as base");
                }

                ~Base() {
                    print("~base body");
                    whoami();
                }
            }

            class Derived extends Base {
                int other = mark("derived field", 2);

                Derived() {
                    print("derived body");
                    whoami();
                }

                override void whoami() {
                    print("as derived");
                }

                ~Derived() {
                    print("~derived body");
                    whoami();
                }
            }

            void main() {
                var x = new Derived();
                print("end");
            }
            """, output: ["base field", "base body", "as base", "derived field", "derived body", "as derived", "end",
                          "~derived body", "as derived", "~base body", "as base"]);

        Case(results, "abstract_in_constructor", """
            abstract class Shape {
                Shape() {
                    print(area());
                }

                abstract int area();
            }

            class Square extends Shape {
                override int area() {
                    return 4;
                }
            }

            void main() {
                var s = new Square();
            }
            """, panic: "abstract method 'Shape.area' called during construction or destruction", panicAtLine: 6);

        Case(results, "destructor_kept_reference", """
            class Keeper {
                mut Ghost? kept;
            }

            class Ghost {
                Keeper keeper;

                Ghost(Keeper keeper) {
                    this.keeper = keeper;
                }

                ~Ghost() {
                    keeper.kept = this;
                }
            }

            void main() {
                var keeper = new Keeper();
                mut var g = new Ghost(keeper);
                g = new Ghost(new Keeper());
            }
            """, panic: "an object's destructor kept a reference to the object");

        Case(results, "borrow_hooks", """
            class Box implements Borrowable {
                mut int[2] items;

                pub void beginBorrow() {
                    print("begin");
                }

                pub void endBorrow() {
                    print("end");
                }

                ref int operator [](int index) {
                    return ref items[index];
                }
            }

            void bump(ref int n) {
                print("bump");
                n++;
            }

            void main() {
                Box box = new Box();
                int plain = box[1];
                print(plain);
                bump(ref box[0]);
                print(box.items[0]);
                {
                    ref int held = box[1];
                    held = 4;
                }
                print(box.items[1]);
            }
            """, output: ["0", "begin", "bump", "end", "1", "begin", "end", "4"]);

        Case(results, "references_into_objects", """
            struct Pair {
                int[2] items;

                mut ref int at(int i) {
                    return ref items[i];
                }
            }

            class Bag {
                mut int value;

                ref int slot() {
                    return ref value;
                }
            }

            void write(ref int place, int value) {
                place = value;
            }

            void main() {
                mut Pair p = Pair();
                p.at(1) = 3;
                write(ref p.at(0), 5);
                print(p.items[0] + p.items[1]);
                var bag = new Bag();
                ref int slot = bag.slot();
                slot = 8;
                print(bag.value);
                bag.slot() = 9;
                print(bag.value);
            }
            """, output: ["8", "8", "9"]);

        Case(results, "struct_field_of_object", """
            struct Vec3 {
                float x;
                float y;
                float z;
            }

            class Particle {
                Vec3 position;
                mut Vec3 velocity;

                Particle(Vec3 position) {
                    this.position = position;
                }
            }

            void main() {
                var p = new Particle(Vec3(1, 2, 3));
                p.velocity = Vec3(0.5f, 0, 0);
                print(p.position.y);
                print(p.velocity.x);
                mut var copy = p.position;
                copy.y = 7;
                print(p.position.y);
                print(copy.y);
            }
            """, output: ["2.0", "0.5", "2.0", "7.0"]);

        Case(results, "lambdas", """
            int apply((int) -> int f, int x) {
                return f(x);
            }

            int run(() -> int f) {
                return f();
            }

            void main() {
                mut int total = 0;
                int start = 2;
                (int) -> int add = x -> x + start;
                print(apply(add, 3));
                run(() -> {
                    total += 5;
                    print(total);
                    return total;
                });
                print(total);
                print(apply(x -> x * 10, 4));
            }
            """, output: ["5", "5", "5", "40"]);

        Case(results, "closures", """
            class Counter {
                mut int count;

                Counter(int count) {
                    this.count = count;
                }

                () -> int reader() {
                    return () -> count;
                }

                () -> void adder(int by) {
                    return () -> {
                        count += by;
                    };
                }

                ~Counter() {
                    print($"freed {count}");
                }
            }

            (int) -> int keep(int base) {
                return x -> base * 2 + x;
            }

            void main() {
                var f = keep(21);
                print(f(0));
                @Escaping mut () -> void add;
                {
                    var c = new Counter(10);
                    var read = c.reader();
                    add = c.adder(5);
                    add();
                    print(read());
                }
                add();
                print("end");
            }
            """, output: ["42", "15", "end", "freed 20"]);

        Case(results, "generic_type_maps", """
            struct Pair<A, B> {
                A first;
                B second;

                Pair(A first, B second) {
                    this.first = first;
                    this.second = second;
                }

                Pair<B, A> swapped() {
                    return Pair<B, A>(second, first);
                }

                String text<C extends Printable>(C extra) {
                    return extra.toString();
                }
            }

            Pair<B, A> flip<A, B>(Pair<A, B> p) {
                return p.swapped();
            }

            T identity<T>(T value) {
                return value;
            }

            void main() {
                var p = Pair<int, String>(1, "two");
                var q = flip(p);
                print($"{q.first} {q.second}");
                print(p.swapped().text(3.5));
                print(Pair<int, int>().first + Pair<int, int>().second);
                print(identity(identity(7)));
            }
            """, output: ["two 1", "3.5", "0", "7"]);

        Case(results, "function_fields", """
            class Button {
                mut (int) -> int scale = x -> x;
                mut ((int) -> int)? transform = null;
                String name;

                Button(String name) {
                    this.name = name;
                }

                void set(@Escaping (int) -> int f) {
                    scale = f;
                }

                int apply(int value) {
                    var f = transform;
                    if (f != null) {
                        return scale(f(value));
                    }
                    return scale(value);
                }
            }

            void main() {
                var b = new Button("b");
                print(b.apply(3));
                b.set(x -> x * 2);
                print(b.apply(3));
                b.transform = x -> x + 1;
                print(b.apply(3));
                b.transform = null;
                print(b.apply(3));
                print(b.name);
            }
            """, output: ["3", "6", "8", "6", "b"]);

        Case(results, "choices", """
            int pick(bool first, (int) -> int a, (int) -> int b) {
                (int) -> int chosen = first ? a : b;
                return chosen(10);
            }

            void main() {
                print(pick(true, x -> x + 1, x -> x * 2));
                print(pick(false, x -> x + 1, x -> x * 2));
                mut bool flag = false;
                int base = 100;
                (int) -> int f = flag ? x -> x + base : x -> x - base;
                print(f(1));
                flag = true;
                print(f(1));
                @Escaping (int) -> int g = flag ? x -> x + 1 : x -> x + 2;
                print(g(10));
            }
            """, output: ["11", "20", "-99", "-99", "11"]);

        Case(results, "function_references", """
            class Mob {
                int depth;

                Mob(int depth) {
                    this.depth = depth;
                }

                static int byDepth(Mob a, Mob b) {
                    return a.depth - b.depth;
                }

                static int scaled(int x) {
                    return x * 5;
                }

                int scaledTwice() {
                    return apply(scaled, apply(scaled, depth));
                }
            }

            int apply((int) -> int f, int x) {
                return f(x);
            }

            int inc(int x) {
                return x + 1;
            }

            double halve(double x) {
                return x / 2;
            }

            void main() {
                (int) -> int f = inc;
                print(f(1));
                print(apply(inc, 41));
                print(new Mob(2).scaledTwice());
                (double) -> double h = halve;
                print(h(5));
                (Mob, Mob) -> int byDepth = Mob.byDepth;
                print(byDepth(new Mob(1), new Mob(4)));
            }
            """, output: ["2", "42", "50", "2.5", "-3"]);

        Case(results, "interface_dispatch", """
            interface Runner {
                mut int run(() -> int task);
            }

            class Twice implements Runner {
                int run(() -> int task) {
                    return task() + task();
                }
            }

            int callTemplate(() -> int f) {
                return f();
            }

            void main() {
                Runner r = new Twice();
                mut int calls = 0;
                print(r.run(() -> {
                    calls += 1;
                    return calls;
                }));
                print(calls);
                print(r.run(() -> 7));
                print(callTemplate(() -> 3));
            }
            """, output: ["3", "2", "14", "3"]);

        Case(results, "ref_function_values", """
            void bump(ref int x) {
                x += 10;
            }

            int write(ref int place, int value) {
                place = value;
                return value;
            }

            void main() {
                (ref int) -> void twice = v -> {
                    v += 1;
                    v *= 2;
                };
                mut int a = 3;
                twice(ref a);
                print(a);
                (ref int) -> void f = bump;
                f(ref a);
                print(a);
                (ref int, int) -> int w = write;
                print(w(ref a, 4));
                print(a);
            }
            """, output: ["8", "18", "4", "4"]);

        Case(results, "type_parameter_bounds", """
            String show<T extends Printable>(T value) {
                return value.toString();
            }

            int order<T extends Comparable<T>>(T a, T b) {
                return a.compareTo(b);
            }

            T max<T extends Comparable<T>>(T a, T b) {
                return a.compareTo(b) < 0 ? b : a;
            }

            void main() {
                print(show(42));
                print(show('x'));
                print(show("text"));
                print(show(true));
                print(show(2.5));
                print(order(3, 5));
                print(order("b", "a"));
                print(max(9, 4));
                print(max('a', 'b'));
            }
            """, output: ["42", "x", "text", "true", "2.5", "-1", "1", "9", "b"]);

        Case(results, "generic_closures", """
            struct Cell<T extends Printable> {
                T value;

                Cell(T value) {
                    this.value = value;
                }

                String show() {
                    return value.toString();
                }
            }

            () -> String maker<T extends Printable>(T value) {
                return () -> Cell<T>(value).show();
            }

            class Box<T extends Printable> {
                T value;

                Box(T value) {
                    this.value = value;
                }

                () -> T reader() {
                    return () -> value;
                }
            }

            void main() {
                print(maker(7)());
                print(maker("text")());
                var b = new Box<int>(5);
                print(b.reader()());
            }
            """, output: ["7", "text", "5"]);

        Case(results, "raw_memory", """
            unsafe int third(int* values) {
                return values[2];
            }

            void main() {
                unsafe {
                    int* nothing = null;
                    print(nothing == null);
                    int* values = Memory.allocate<int>(3);
                    for (int i : 0..3) {
                        Memory.initialize(values + i, i * 10);
                    }
                    print(values[0]);
                    print(third(values));
                    int* middle = values + 1;
                    print(middle[1]);
                    print(values == middle);
                    Memory.moveTo(values, values + 1, 2);
                    print(values[1]);
                    print(values[2]);
                    Memory.drop(values + 2);
                    Memory.free(values);
                    Memory.free<int>(null);
                }
            }
            """, output: ["true", "0", "20", "20", "false", "0", "10"]);

        Case(results, "spans", """
            int total(ReadOnlySpan<int> values) {
                mut int sum = 0;
                for (int v : values) {
                    sum += v;
                }
                return sum;
            }

            struct Numbers {
                int[3] items;

                ReadOnlySpan<int> view() {
                    return items;
                }
            }

            void main() {
                mut int[3] numbers = [1, 2, 3];
                print(total(numbers));
                Span<int> span = numbers;
                span[0] = 10;
                print(numbers[0]);
                print(span.length());
                Span<int> tail = span.slice(1, 2);
                print(total(tail));
                for (ref int v : tail) {
                    v *= 2;
                }
                print(numbers[2]);
                ReadOnlySpan<int> view = tail;
                print(view[0]);
                mut Numbers holder = Numbers();
                holder.items[1] = 7;
                print(total(holder.view()));
            }
            """, output: ["6", "10", "3", "5", "6", "4", "7"]);

        Case(results, "string_bytes", """
            int total(ReadOnlySpan<byte> bytes) {
                mut int sum = 0;
                for (byte b : bytes) {
                    sum += b;
                }
                return sum;
            }

            void main() {
                String text = "hé";
                print(text.bytes().length());
                print(text.bytes()[0]);
                print(total(text.bytes()));
                print(text.bytes().slice(1, 2)[1]);
                print("".bytes().length());
            }
            """, output: ["3", "104", "468", "169", "0"]);

        Case(results, "containers", """
            class Item {
                int id;

                Item(int id) {
                    this.id = id;
                }

                ~Item() {
                    print(-id);
                }
            }

            void main() {
                Item?[] slots = new Item?[3];
                print(slots[0] == null);
                slots[1] = new Item(5);
                print(slots[1]!.id);
                var list = new List<int>();
                list.add(3);
                list.add(4);
                print(list.count());
                print(list[1]);
            }
            """, output: ["true", "5", "2", "4", "-5"]);

        Case(results, "bounds_index", """
            void main() {
                int[] values = new int[2];
                int index = 2;
                print(values[index]);
            }
            """, panic: "index 2 is out of bounds for length 2", panicAtLine: 4);

        Case(results, "bounds_range", """
            void main() {
                mut int[3] numbers = [1, 2, 3];
                Span<int> span = numbers;
                Span<int> tail = span.slice(2, 2);
                print(tail.length());
            }
            """, panic: "range of 2 from 2 is out of bounds for length 3", panicAtLine: 4);

        Case(results, "allocate_negative", """
            void main() {
                unsafe {
                    int* values = Memory.allocate<int>(-1);
                    print(values == null);
                }
            }
            """, panic: "cannot allocate a negative number of elements", panicAtLine: 3);

        Case(results, "uninitialized_read", """
            void main() {
                unsafe {
                    int* values = Memory.allocate<int>(1);
                    print(values[0]);
                }
            }
            """, undefined: "read storage that was never initialized");

        Case(results, "null_pointer", """
            void main() {
                unsafe {
                    int* nothing = null;
                    print(nothing[0]);
                }
            }
            """, undefined: "dereferenced the null pointer 'nothing'");

        Case(results, "weak_reference", """
            class Target {
                int id;

                Target(int id) {
                    this.id = id;
                }

                ~Target() {
                    print($"gone {id}");
                }
            }

            void main() {
                mut var a = new Target(1);
                Weak<Target> watch = a;
                if (watch.get() is Target t) {
                    print(t.id);
                }
                a = new Target(2);
                print(watch.get() == null);
            }
            """, output: ["1", "gone 1", "true", "gone 2"]);

        Case(results, "weak_while_dying", """
            class Watcher {
                mut Weak<Watcher> self;

                ~Watcher() {
                    self = this;
                    print(self.get() == null);
                }
            }

            void main() {
                _ = new Watcher();
            }
            """, output: ["true"]);

        Case(results, "weak_in_struct", """
            class Target {
                int id;

                Target(int id) {
                    this.id = id;
                }
            }

            struct Holder {
                Weak<Target> target;
                int priority = 1;
            }

            void main() {
                var a = new Target(5);
                mut Holder holder = Holder();
                holder.target = a;
                Holder copy = holder;
                holder.target = null;
                if (copy.target.get() is Target t) {
                    print(t.id);
                }
            }
            """, output: ["5"]);

        Case(results, "cycle_collected", """
            class Node {
                int id;
                mut Node? next;

                Node(int id) {
                    this.id = id;
                }

                ~Node() {
                    print(id);
                }
            }

            void main() {
                mut var a = new Node(1);
                a.next = new Node(2);
                a.next!.next = a;
                a = new Node(3);
                Memory.collectCycles();
                print(Memory.cycleStats().freed);
            }
            """, output: ["1", "2", "2", "3"]);

        Case(results, "cycle_budget", """
            class Node {
                mut Node? next;
            }

            void main() {
                mut var a = new Node();
                a.next = new Node();
                a.next!.next = a;
                a = new Node();
                Memory.collectCycles(1);
                var budgeted = Memory.cycleStats();
                print(budgeted.visited);
                Memory.collectCycles();
                var collected = Memory.cycleStats();
                print(collected.visited);
                print(collected.freed);
            }
            """, output: ["0", "2", "2"]);

        Case(results, "weak_after_collection", """
            class Node {
                mut Node? next;
            }

            void main() {
                mut Weak<Node> watch = null;
                if (true) {
                    var a = new Node();
                    a.next = a;
                    watch = a;
                }
                print(watch.get() != null);
                Memory.collectCycles();
                print(watch.get() == null);
            }
            """, output: ["true", "true"]);

        Case(results, "collect_in_destructor", """
            class Node {
                ~Node() {
                    Memory.collectCycles();
                }
            }

            void main() {
                mut var n = new Node();
                n = new Node();
            }
            """, panic: "Memory.collectCycles cannot be called from a destructor", panicAtLine: 3);

        Case(results, "cycle_resurrection", """
            class Keeper {
                mut Node? kept;
            }

            class Node {
                mut Node? next;
                Keeper keeper;

                Node(Keeper keeper) {
                    this.keeper = keeper;
                }

                ~Node() {
                    keeper.kept = next;
                }
            }

            void main() {
                var keeper = new Keeper();
                mut var a = new Node(keeper);
                a.next = new Node(keeper);
                a.next!.next = a;
                a = new Node(new Keeper());
                Memory.collectCycles();
            }
            """, panic: "a destructor kept a reference to an object of the cycle being collected");

        Case(results, "uncollected_warning", """
            class Node {
                mut Node? next;
            }

            void main() {
                var a = new Node();
                a.next = a;
            }
            """, warnings: ["at exit, 1 objects in cycles were never freed: 1 Node."]);

        Case(results, "stack_slot", """
            class Node {
                mut Node? next;

                ~Node() {
                    print("destroyed");
                }
            }

            void drop<T>(T value) {
            }

            void main() {
                var local = new Node();
                drop(local);
                print(Memory.cycleStats().buffered);
            }
            """, optimized: true, output: ["0", "destroyed"]);

        results.Add(Registry());
        results.Add(Budget());
        results.Add(AllocationBudget());
        return results;
    }

    /// <summary>A budget is inconclusive, like a timeout: it stops the interpretation, not the suite.</summary>
    static TestResult Budget()
    {
        const string name = "interpreter/budget";
        var source = new SourceText("budget.wyl", """
            void main() {
                mut int i = 0;
                while (true) {
                    i = i + 1;
                }
            }
            """);
        var interpreted = Compilation.Interpret(source, new CompileOptions(Optimize: false), new InterpreterOptions(StepBudget: 10_000));
        if (interpreted.Interpretation is not { } result)
            return new(name, "the pipeline produced no IR");
        return new(name, result.Status == InterpretationStatus.BudgetExceeded
            ? null
            : $"expected the budget to run out, got {result.Status}: {result.Error.Trim()}");
    }

    /// <summary>
    /// Storage the program asks for is work the budget bounds too, so a count no run could afford
    /// is inconclusive rather than the host running out of memory: an `integer-edge` mutation of a
    /// length must not fail as a finding about the fuzzer's machine.
    /// </summary>
    static TestResult AllocationBudget()
    {
        const string name = "interpreter/allocation_budget";
        var source = new SourceText("budget.wyl", """
            import std::Memory;

            void main() {
                unsafe {
                    int* values = Memory.allocate<int>(20_000);
                    Memory.free(values);
                }
            }
            """);
        var interpreted = Compilation.Interpret(source, new CompileOptions(Optimize: false), new InterpreterOptions(StepBudget: 1_000));
        if (interpreted.Interpretation is not { } result)
            return new(name, "the pipeline produced no IR");
        return new(name, result.Status == InterpretationStatus.BudgetExceeded
            ? null
            : $"expected the allocation to run out of budget, got {result.Status}: {result.Error.Trim()}");
    }

    /// <summary>
    /// Every extern the standard library declares, and every builtin, has a handler: a stdlib method
    /// added without one fails the suite instead of a program meeting it at run time.
    /// </summary>
    static TestResult Registry()
    {
        const string name = "interpreter/registry";
        var lowered = Compilation.Lower(new SourceText("registry.wyl", "void main() { }"), new CompileOptions(Optimize: false));
        if (lowered.Program is not { } program)
            return new(name, "the empty program did not bind");
        var symbols = program.Types
            .SelectMany(type => type.Members)
            .Where(member => member.IsBuiltin)
            .Concat(Builtins.All)
            .Concat([
                Builtins.Interpolate([]),
                Builtins.ToString(WylType.Int),
                Builtins.CompareTo(WylType.Int),
                Builtins.Formatted(WylType.Int),
            ]);
        var missing = symbols.Select(Interpreter.RuntimeKey).Distinct().Where(key => !Interpreter.Handles(key)).Order().ToList();
        return new(name, missing.Count == 0 ? null : "no handler for: " + string.Join(", ", missing));
    }

    static void Case(
        List<TestResult> results, string name, string source, string? panic = null, int? panicAtLine = null,
        bool release = false, string? undefined = null, IReadOnlyList<string>? warnings = null, bool optimized = false, params string[] output) =>
        results.Add(new TestResult("interpreter/" + name, Check(name, source, output, panic, panicAtLine, release, undefined, warnings, optimized)));

    static string? Check(
        string name, string source, IReadOnlyList<string> expected, string? panic, int? panicAtLine, bool release,
        string? undefined = null, IReadOnlyList<string>? warningsExpected = null, bool optimized = false)
    {
        var text = new SourceText(name + ".wyl", TestPrograms.WithStd(source));
        var interpreted = Compilation.Interpret(text, new CompileOptions(Optimize: optimized), new InterpreterOptions(Release: release));
        var errors = interpreted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
            return "did not compile: " + string.Join(" | ", errors);
        if (interpreted.Interpretation is not { } result)
            return "the pipeline produced no IR";
        if (result.Status == InterpretationStatus.Unsupported)
            return "unsupported: " + result.Error.Trim();
        if (undefined is not null)
            return result.Status == InterpretationStatus.UndefinedBehavior && result.Error.Contains(undefined, StringComparison.Ordinal)
                ? null
                : $"expected undefined behavior '{undefined}', got {result.Status}: {result.Error.Trim()}";
        var problems = new List<string>();
        var actual = result.OutputText.Replace("\r\n", "\n").Split('\n').ToList();
        if (actual.Count > 0 && actual[^1] == "")
            actual.RemoveAt(actual.Count - 1);
        if (!actual.SequenceEqual(expected))
            problems.Add($"expected [{string.Join(" | ", expected)}], got [{string.Join(" | ", actual)}]");
        if (panic is not null)
        {
            var at = panicAtLine is { } line ? $"\n    at {name}.wyl:{line}:" : "";
            if (result.Status != InterpretationStatus.Panicked || !result.Error.Contains("panic: " + panic, StringComparison.Ordinal) || !result.Error.Contains(at, StringComparison.Ordinal))
                problems.Add($"expected panic '{panic}'{(at.Length > 0 ? $" at line {panicAtLine}" : "")}, got {result.Status}: {result.Error.Trim()}");
        }
        else if (result.Status != InterpretationStatus.Completed)
        {
            problems.Add($"expected completion, got {result.Status}: {result.Error.Trim()}");
        }

        // A case that lists warnings checks them exactly; one that doesn't leaves them unchecked,
        // since the interpret mode over the suite already checks every test's.
        if (warningsExpected is not null)
        {
            const string warningPrefix = "performance warning: ";
            var warnings = result.Error.Split('\n').Where(l => l.StartsWith(warningPrefix, StringComparison.Ordinal)).Select(l => l[warningPrefix.Length..]).ToList();
            foreach (var warning in warningsExpected)
            {
                int index = warnings.FindIndex(w => w.Contains(warning, StringComparison.Ordinal));
                if (index >= 0)
                    warnings.RemoveAt(index);
                else
                    problems.Add($"missing warning: {warning}");
            }
            problems.AddRange(warnings.Select(w => $"unexpected warning: {w}"));
        }

        return problems.Count == 0 ? null : string.Join('\n', problems);
    }
}
