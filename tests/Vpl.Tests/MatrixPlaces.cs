// The places x operations matrix: where a value lives, crossed with what is done through it.
// A place is a location (a local, a field, an element, a reference, a nullable value, a capture,
// a receiver's field); an operation reads it, writes it, passes it, refers to it, views it, loops
// over it, captures it, narrows it, returns it, or reassigns it while a reference holds it.
// Each cell prints what it stored, so a snapshot shows the value every operation left behind;
// a cell where the spec calls for an error records the error instead.
static class MatrixPlaces
{
    static readonly string[] Operations =
    [
        "read", "assign", "compound", "pass", "pass-ref-in", "take-ref-in", "take-ref-nullable",
        "span", "loop", "capture", "capture-escaping", "narrow", "return", "return-ref", "reassign",
    ];

    public static Matrix Build()
    {
        var places = new List<Place>
        {
            Local(), MutLocal(), Field(), FixedElement(), ListElement(), SpanElement(),
            RefLocal(), InLocal(), RefNullableLocal(), InNullableLocal(),
            NullableValue(), CapturedLocal(), ThisField(), ChainedField(),
        };
        var cells = new List<MatrixCell>();
        foreach (var place in places)
        {
            foreach (var operation in Operations)
            {
                var source = $"// matrix: places x operations, cell: {place.Name}/{operation}\n\n{World}\n{place.Build(operation)}";
                cells.Add(new MatrixCell($"{place.Name}/{operation}", source));
            }
        }
        return new Matrix(
            "places",
            "places x operations: a place is where a value lives; an operation is what is done through it.",
            cells);
    }

    /// <summary>How a place looks in one shape: the value type, how it is set up, and its expression.</summary>
    sealed record Use(
        string Type,
        string Setup,
        string Expr,
        bool Mutable,
        string? NullableExpr = null,
        bool NeedsNarrowing = false)
    {
        public string Value => Type is "int" or "int?" ? "5" : "[7, 8, 9]";

        /// <summary>Statements printing the value at <paramref name="expr"/>.</summary>
        public string Read(string expr) => Type switch
        {
            "int" => $"print({expr});",
            "int?" => $"print({expr} ?? -1);",
            "int[3]" => $"print({expr}[0]);\nprint({expr}[1]);\nprint({expr}[2]);",
            "int[3]?" => $"if ({expr} != null) {{\nprint({expr}[0]);\nprint({expr}[1]);\nprint({expr}[2]);\n}} else {{\nprint(-1);\n}}",
            _ => throw new ArgumentOutOfRangeException(nameof(expr), Type, "no reader for the type"),
        };
    }

    sealed record Place(
        string Name,
        Use Scalar,
        Use Array,
        Func<Use, string, string, string> Host,
        Func<Use, string> ReturnRef)
    {
        public string Build(string operation)
        {
            var use = operation is "span" or "loop" ? Array : Scalar;
            if (operation == "return-ref")
                return ReturnRef(use);
            var body = Body(use, operation);
            if (use.NeedsNarrowing && operation is not ("assign" or "narrow" or "return"))
                body = $"if ({use.NullableExpr} != null) {{\n{body}\n}}";
            return Host(use, operation, body);
        }
    }

    static Place InMain(string name, Use scalar, Use array, Func<Use, string>? returnRef = null) =>
        new(name, scalar, array, MainHost, returnRef ?? GenericReturnRef);

    static Use U(string type, string setup, string expr, bool mutable, string? nullableExpr = null, bool needsNarrowing = false) =>
        new(type, setup, expr, mutable, nullableExpr, needsNarrowing);

    static Place Local() => InMain("local",
        U("int", "int x = 1;", "x", mutable: false),
        U("int[3]", "int[3] x = [1, 2, 3];", "x", mutable: false));

    static Place MutLocal() => InMain("mut-local",
        U("int", "mut int x = 1;", "x", mutable: true),
        U("int[3]", "mut int[3] x = [1, 2, 3];", "x", mutable: true));

    static Place Field() => InMain("field",
        U("int", "mut Pair p = Pair();", "p.value", mutable: true),
        U("int[3]", "mut Pair p = Pair();", "p.values", mutable: true),
        FieldReturnRef);

    static Place FixedElement() => InMain("fixed-element",
        U("int", "mut int[3] a = [1, 2, 3];", "a[0]", mutable: true),
        U("int[3]", "mut int[2][3] g = [[1, 2, 3], [4, 5, 6]];", "g[0]", mutable: true),
        FixedElementReturnRef);

    static Place ListElement() => InMain("list-element",
        U("int", "var list = new List<int>();\nlist.add(1);\nlist.add(2);\nlist.add(3);", "list[0]", mutable: true),
        U("int[3]", "var list = new List<int[3]>();\nlist.add([1, 2, 3]);\nlist.add([4, 5, 6]);", "list[0]", mutable: true),
        ListElementReturnRef);

    static Place SpanElement() => InMain("span-element",
        U("int", "mut int[3] a = [1, 2, 3];\nSpan<int> s = a;", "s[0]", mutable: true),
        U("int[3]", "mut int[2][3] g = [[1, 2, 3], [4, 5, 6]];\nSpan<int[3]> s = g;", "s[0]", mutable: true),
        SpanElementReturnRef);

    static Place RefLocal() => InMain("ref-local",
        U("int", "mut int x = 1;\nref int r = x;", "r", mutable: true),
        U("int[3]", "mut int[3] x = [1, 2, 3];\nref int[3] r = x;", "r", mutable: true));

    static Place InLocal() => InMain("in-local",
        U("int", "mut int x = 1;\nin int r = x;", "r", mutable: false),
        U("int[3]", "mut int[3] x = [1, 2, 3];\nin int[3] r = x;", "r", mutable: false));

    static Place RefNullableLocal() => InMain("ref-nullable-local",
        U("int", "mut int x = 1;\nref? int q = x;", "q!", mutable: true, nullableExpr: "q"),
        U("int[3]", "mut int[3] x = [1, 2, 3];\nref? int[3] q = x;", "q!", mutable: true, nullableExpr: "q"));

    static Place InNullableLocal() => InMain("in-nullable-local",
        U("int", "mut int x = 1;\nin? int q = x;", "q!", mutable: false, nullableExpr: "q"),
        U("int[3]", "mut int[3] x = [1, 2, 3];\nin? int[3] q = x;", "q!", mutable: false, nullableExpr: "q"));

    static Place NullableValue() => InMain("nullable-value",
        U("int?", "mut int? v = 1;", "v", mutable: true, nullableExpr: "v", needsNarrowing: true),
        U("int[3]?", "mut int[3]? v = [1, 2, 3];", "v", mutable: true, nullableExpr: "v", needsNarrowing: true));

    static Place CapturedLocal() => new("captured-local",
        U("int", "mut int x = 1;", "x", mutable: true),
        U("int[3]", "mut int[3] x = [1, 2, 3];", "x", mutable: true),
        CapturedHost, GenericReturnRef);

    static Place ThisField() => new("this-field",
        U("int", "", "value", mutable: true),
        U("int[3]", "", "values", mutable: true),
        MethodHost, ThisFieldReturnRef);

    static Place ChainedField() => InMain("chained-field",
        U("int", "var node = new Node();", "node.inner.value", mutable: true),
        U("int[3]", "var node = new Node();", "node.inner.values", mutable: true),
        ChainedFieldReturnRef);

    /// <summary>The statements of one operation, with the place's own expression substituted.</summary>
    static string Body(Use use, string operation)
    {
        var place = use.Expr;
        var value = use.Value;
        var slot = "slot";
        return operation switch
        {
            "read" => $"print({place});",
            "assign" => $"{place} = {value};",
            "compound" => $"{place} += 2;",
            "pass" => $"print(take({place}));",
            "pass-ref-in" => use.Mutable ? $"bump(ref {place});" : $"print(peek({place}));",
            "take-ref-in" => use.Mutable
                ? $"ref {use.Type} {slot} = {place};\n{slot} = {value};"
                : $"in {use.Type} {slot} = {place};\nprint({slot});",
            "take-ref-nullable" => use.Mutable
                ? $"ref? {use.Type} {slot} = {place};\n{slot}! = {value};"
                : $"in? {use.Type} {slot} = {place};\nprint({slot}!);",
            "span" => use.Mutable
                ? $"Span<int> view = {place};\nview[0] = 5;"
                : $"ReadOnlySpan<int> view = {place};\nprint(view[0]);",
            "loop" => use.Mutable
                ? $"for (ref int element : {place}) {{\nelement += 1;\n}}"
                : $"for (in int element : {place}) {{\nprint(element);\n}}",
            "capture" => Capture(use, escaping: false),
            "capture-escaping" => Capture(use, escaping: true),
            "narrow" => use.NullableExpr is { } nullable
                ? $"if ({nullable} != null) {{\nprint({nullable});\n}}"
                : $"print({place}!);",
            "return" => $"return {place};",
            "reassign" => $"ref {use.Type} {slot} = {place};\n{place} = {value};\nprint({slot});",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "no such operation"),
        };
    }

    static string Capture(Use use, bool escaping)
    {
        var body = use.Mutable ? $"{use.Expr} = {use.Value};" : $"print({use.Expr});";
        var prefix = escaping ? "@Escaping " : "";
        return $"{prefix}() -> void action = () -> {{\n{body}\n}};\naction();";
    }

    static string MainHost(Use use, string operation, string body) => operation == "return"
        ? $"{use.Type} get() {{\n{use.Setup}\n{body}\n}}\n\nvoid main() {{\n{use.Type} result = get();\n{use.Read("result")}\n}}\n"
        : $"void main() {{\n{use.Setup}\n{body}\n{use.Read(use.Expr)}\n}}\n";

    static string CapturedHost(Use use, string operation, string body) => operation == "return"
        ? $"void main() {{\n{use.Setup}\n() -> {use.Type} runBody = () -> {{\n{body}\n}};\n{use.Type} result = runBody();\n{use.Read("result")}\n}}\n"
        : $"void main() {{\n{use.Setup}\n() -> void runBody = () -> {{\n{body}\n}};\nrunBody();\n{use.Read(use.Expr)}\n}}\n";

    static string MethodHost(Use use, string operation, string body)
    {
        var method = operation == "return"
            ? $"{use.Type} run() {{\n{body}\n}}"
            : $"void run() {{\n{body}\n{use.Read(use.Expr)}\n}}";
        var call = operation == "return"
            ? $"var result = widget.run();\n{use.Read("result")}"
            : "widget.run();";
        return $"class Widget {{\n    internal {{\n        mut int value = 1;\n        mut int[3] values = [1, 2, 3];\n\n        {method}\n    }}\n}}\n\nvoid main() {{\n    var widget = new Widget();\n    {call}\n}}\n";
    }

    // A reference may only be returned into the receiver's own storage, so a place outside a
    // receiver can't be returned at all; that error is what most places' cell records.
    static string GenericReturnRef(Use use) =>
        $"struct Owner {{\n    internal {{\n        mut ref {use.Type} at() {{\n{use.Setup}\n            return ref {use.Expr};\n        }}\n    }}\n}}\n\nvoid main() {{\n    mut Owner owner = Owner();\n    owner.at() = {use.Value};\n}}\n";

    static string FieldReturnRef(Use use) => """
        struct Owned {
            internal {
                int value = 1;

                mut ref int at() {
                    return ref value;
                }
            }
        }

        void main() {
            mut Owned owned = Owned();
            owned.at() = 5;
            print(owned.value);
        }
        """;

    static string FixedElementReturnRef(Use use) => """
        struct Fixed {
            internal {
                int[3] a = [1, 2, 3];

                mut ref int at() {
                    return ref a[0];
                }
            }
        }

        void main() {
            mut Fixed fixedValue = Fixed();
            fixedValue.at() = 5;
            print(fixedValue.a[0]);
        }
        """;

    static string ListElementReturnRef(Use use) => """
        class Store {
            internal {
                List<int> list = new List<int>();

                Store() {
                    list.add(1);
                    list.add(2);
                }

                ref int at() {
                    return ref list[0];
                }
            }
        }

        void main() {
            var store = new Store();
            store.at() = 5;
            print(store.list[0]);
        }
        """;

    static string SpanElementReturnRef(Use use) => """
        struct Fixed {
            internal {
                int[3] a = [1, 2, 3];

                mut ref int at() {
                    Span<int> view = a;
                    return ref view[0];
                }
            }
        }

        void main() {
            mut Fixed fixedValue = Fixed();
            fixedValue.at() = 5;
            print(fixedValue.a[0]);
        }
        """;

    static string ThisFieldReturnRef(Use use) => """
        class Widget {
            internal {
                mut int value = 1;
                mut int[3] values = [1, 2, 3];

                ref int at() {
                    return ref value;
                }
            }
        }

        void main() {
            var widget = new Widget();
            widget.at() = 5;
            print(widget.value);
        }
        """;

    static string ChainedFieldReturnRef(Use use) => """
        class Chain {
            internal {
                mut Inner inner = Inner();

                ref int at() {
                    return ref inner.value;
                }
            }
        }

        void main() {
            var chain = new Chain();
            chain.at() = 5;
            print(chain.inner.value);
        }
        """;

    const string World = """
        struct Pair {
            internal {
                int value = 1;
                int[3] values = [1, 2, 3];
            }
        }

        struct Inner {
            internal {
                int value = 1;
                int[3] values = [1, 2, 3];
            }
        }

        class Node {
            internal {
                mut Inner inner = Inner();
            }
        }

        int take(int value) {
            return value;
        }

        void bump(ref int value) {
            value += 10;
        }

        int peek(in int value) {
            return value;
        }
        """;
}
