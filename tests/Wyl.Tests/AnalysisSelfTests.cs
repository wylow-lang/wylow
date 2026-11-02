using Wyl.Compiler;
using Wyl.Compiler.Analysis;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Text;
using Wyl.Harness;

// What each whole-program fact may answer, asked of the real analyses on lowered fixture programs.
// A fact that under-reports miscompiles only when a pass happens to rely on it and a test happens
// to reach that combination, so every way code can reach something has a case here.
//
// A case named `sound/...` is a soundness case: the fact must include the route, and a failure is
// a miscompile waiting for an optimization. A case named `precise/...` pins precision: the fact
// must NOT include something, because a pass (named in the case) gives up an optimization
// otherwise. A later, sharper analysis may legitimately change a precision case; a soundness
// case never.
static class AnalysisSelfTests
{
    const string Fixture = """
        class Keeper {
            mut Keeper? other;
            mut int n;
            Keeper() { }
        }

        // The base constructor passes `this` to code that keeps it, with no receiver in the IR.
        class Base extends Keeper {
            Base(Keeper k) { k.other = this; }
        }

        class Derived extends Base {
            Derived(Keeper k) { super(k); }
            Derived(Keeper k, int unused) { this(k); }
        }

        class Quiet extends Keeper {
            Quiet(Keeper k) { }
        }

        class QuietChild extends Quiet {
            QuietChild(Keeper k) { super(k); }
        }

        interface Sink {
            mut void put(Keeper o);
        }

        class Storing implements Sink {
            mut Keeper? kept;
            Storing() { }
            void put(Keeper o) { kept = o; }
        }

        class Ignoring implements Sink {
            Ignoring() { }
            void put(Keeper o) { }
        }

        class Parent {
            Parent() { }
            virtual void take(Keeper o) { }
        }

        class Child extends Parent {
            mut Keeper? kept;
            Child() { }
            override void take(Keeper o) { kept = o; }
        }

        // Releasing a Dying runs a destructor that writes a Keeper's field.
        class Dying {
            Keeper target;
            Dying(Keeper target) { this.target = target; }
            ~Dying() { target.n = 1; }
        }

        class Shelf { mut Pairing? held; Shelf() { } }
        struct Pairing { Keeper a; }

        void store(Keeper h, Keeper o) { h.other = o; }
        Keeper pass(Keeper o) { return o; }
        void viaCall(Keeper h, Keeper o) { store(h, o); }
        void viaInterface(Sink s, Keeper o) { s.put(o); }
        void viaVirtual(Parent p, Keeper o) { p.take(o); }
        void viaStruct(Shelf s, Keeper o) { s.held = Pairing(o); }
        void viaNew(Keeper h) { h.other = new Keeper(); }
        void readOnly(Keeper o) { int x = o.n; }
        void bump(Keeper k) { k.n = 1; }
        void bumpViaCall(Keeper k) { bump(k); }
        void bumpViaSink(Sink s, Keeper k) { s.put(k); }
        void keep(escaping fn() -> void g) { }
        void run(fn() -> void g) { g(); }
        void counts() {
            mut int count = 0;
            run(() -> { count += 1; });
        }
        void capturesAndKeeps(Keeper h, Keeper o) {
            keep(() -> { store(h, o); });
        }

        void main() { }
        """;

    sealed class Facts(IrProgram ir, BoundProgram program)
    {
        public IrProgram Ir { get; } = ir;
        public BoundProgram Program { get; } = program;
        public Callees Callees { get; } = new(ir);
        public Escapes Escapes { get; } = Escapes.Find(new Callees(ir));
        public Writes Writes { get; } = Writes.Find(new Callees(ir));

        public WylType Type(string name) => Program.Types.First(t => t.Name == name).Type;

        public FieldSymbol Field(string type, string name) => Program.Types.First(t => t.Name == type).FindField(name)!;

        public IrFunction Function(string name) =>
            Ir.Functions.First(f => f.Symbol.Name == name && f.Symbol.ContainingType is null && !f.IsLambda);

        public IrFunction Constructor(string type, int index) =>
            Ir.Functions.Of(Program.Types.First(t => t.Name == type).Constructors[index]).Single();

        public IrFunction Method(string type, string name) =>
            Ir.Functions.Of(Program.Types.First(t => t.Name == type).Methods[name][0]).Single();

        /// <summary>The lambda a function makes, which lowering lists as a function of its own.</summary>
        public IrFunction LambdaIn(string name) =>
            IrWalk.Descendants(Function(name).Body, intoLambdas: false).OfType<IrLet>().Select(l => l.Value switch
            {
                IrLambda lambda => lambda.Function,
                IrClosure closure => closure.Function,
                _ => null,
            }).OfType<IrFunction>().Single();

        public IrRun Run(IrFunction function, bool direct = true) =>
            new IrRunMethod(new MethodReference(function.Symbol, function.Symbol.ContainingType?.Type, []), direct);

        public IrRun Dispatched(string type, string name) =>
            new IrRunMethod(new MethodReference(Program.Types.First(t => t.Name == type).Methods[name][0], Type(type), []), Direct: false);

        /// <summary>The access to a field of any object of its class, as a write through a parameter makes it.</summary>
        public IrAccess FieldAccess(string type, string name)
        {
            var holder = new IrLocal("o", Type(type), IrLocalKind.Value, IrLocalRole.Parameter);
            return IrAccess.Target(IrPlace.Deref(holder).Field(Field(type, name)));
        }

        public bool MayWrite(IrRun run, IrAccess access) => Writes.Of([run]).MayWrite(access);

        public IReadOnlySet<string> Runs(IrRun run) =>
            Callees.Of(run)!.Functions.Select(c => $"{c.Function.Symbol.ContainingType?.Name}.{c.Function.Symbol.Name}").ToHashSet();
    }

    public static List<TestResult> Run()
    {
        var source = new SourceText("analysis_self_test.wyl", TestPrograms.WithStd(Fixture));
        var compilation = Compilation.Compile(source);
        if (compilation.Program is not { } program || compilation.Ir is not { } ir)
            return [new("analysis/fixture", string.Join('\n', compilation.Diagnostics))];
        var facts = new Facts(ir, program);
        List<(string Name, Func<string?> Check)> cases =
        [
            .. EscapeCases(facts),
            .. WriteCases(facts),
            .. CalleeCases(facts),
            .. LambdaAndCycleCases(facts),
        ];
        return [.. cases.Select(c => Run(c.Name, c.Check)), .. Stale(program, ir)];
    }

    static TestResult Run(string name, Func<string?> check)
    {
        try
        {
            return new($"analysis/{name}", check());
        }
        catch (Exception e)
        {
            return new($"analysis/{name}", $"exception: {e}");
        }
    }

    static string? Require(bool condition, string problem) => condition ? null : problem;

    static string? EscapesParameter(Facts f, IrFunction function, int parameter, bool expected) =>
        Require(f.Escapes.SummaryOf(function).Parameters.Contains(parameter) == expected,
            $"{function.Name}: parameter {parameter} {(expected ? "must" : "must not")} be reported as escaping");

    static string? EscapesThis(Facts f, IrFunction function, bool expected) =>
        Require(f.Escapes.SummaryOf(function).This == expected,
            $"{function.Name}: this {(expected ? "must" : "must not")} be reported as escaping");

    static IEnumerable<(string, Func<string?>)> EscapeCases(Facts f)
    {
        yield return ("sound/escapes/stored_in_field", () => EscapesParameter(f, f.Function("store"), 1, true));
        yield return ("sound/escapes/stored_through_a_call", () => EscapesParameter(f, f.Function("viaCall"), 1, true));
        yield return ("sound/escapes/stored_through_an_interface_call", () => EscapesParameter(f, f.Function("viaInterface"), 1, true));
        yield return ("sound/escapes/stored_through_a_virtual_call", () => EscapesParameter(f, f.Function("viaVirtual"), 1, true));
        yield return ("sound/escapes/stored_in_a_struct", () => EscapesParameter(f, f.Function("viaStruct"), 1, true));
        yield return ("sound/escapes/returned", () =>
            Require(f.Escapes.SummaryOf(f.Function("pass")).ReturnedParameters.Contains(0), "a returned parameter must be reported as returned"));
        // The base constructor call has no receiver in the IR: it passes `this` all the same.
        yield return ("sound/escapes/base_constructor_stores_this", () => EscapesThis(f, f.Constructor("Base", 0), true));
        yield return ("sound/escapes/super_call_passes_this", () => EscapesThis(f, f.Constructor("Derived", 0), true));
        yield return ("sound/escapes/chained_call_passes_this", () => EscapesThis(f, f.Constructor("Derived", 1), true));
        yield return ("sound/escapes/closure_capture_kept", () =>
        {
            var captures = f.Escapes.SummaryOf(f.LambdaIn("capturesAndKeeps")).Captures;
            return Require(captures.Any(c => c.Name == "o"), "a capture the closure stores must be reported");
        });
        // StackSlots keeps an object on the stack only if nothing it is given is kept.
        yield return ("precise/escapes/read_only_parameter", () => EscapesParameter(f, f.Function("readOnly"), 0, false));
        yield return ("precise/escapes/target_of_a_store_is_not_stored", () => EscapesParameter(f, f.Function("store"), 0, false));
        yield return ("precise/escapes/constructor_that_keeps_nothing", () => EscapesThis(f, f.Constructor("QuietChild", 0), false));
        yield return ("precise/escapes/closure_capture_not_kept", () =>
        {
            var captures = f.Escapes.SummaryOf(f.LambdaIn("capturesAndKeeps")).Captures;
            return Require(captures.All(c => c.Name != "h"), "a capture the closure only writes through is not kept");
        });
    }

    static IEnumerable<(string, Func<string?>)> WriteCases(Facts f)
    {
        string? Writes(IrFunction function, string type, string field, bool expected) =>
            Require(f.MayWrite(f.Run(function), f.FieldAccess(type, field)) == expected,
                $"{function.Name} {(expected ? "must" : "must not")} be reported as writing {type}.{field}");

        yield return ("sound/writes/direct", () => Writes(f.Function("bump"), "Keeper", "n", true));
        yield return ("sound/writes/through_a_call", () => Writes(f.Function("bumpViaCall"), "Keeper", "n", true));
        yield return ("sound/writes/through_an_interface_call", () => Writes(f.Function("bumpViaSink"), "Storing", "kept", true));
        yield return ("sound/writes/through_a_virtual_call", () => Writes(f.Function("viaVirtual"), "Child", "kept", true));
        yield return ("sound/writes/base_constructor", () => Writes(f.Constructor("Derived", 0), "Keeper", "other", true));
        yield return ("sound/writes/chained_constructor", () => Writes(f.Constructor("Derived", 1), "Keeper", "other", true));
        yield return ("sound/writes/destructor_by_release", () =>
            Require(f.Writes.Of([new IrRunRelease(f.Type("Dying"))]).MayWrite(f.FieldAccess("Keeper", "n")),
                "releasing a Dying must be reported as running its destructor's write"));
        yield return ("sound/writes/destructor_by_unknown_release", () =>
            Require(f.Writes.Of([new IrRunRelease(null)]).MayWrite(f.FieldAccess("Keeper", "n")),
                "releasing any value must be reported as running any destructor's write"));
        // The verifier and passes keep a read across a call that can't write what it reads.
        yield return ("precise/writes/other_field", () => Writes(f.Function("bump"), "Keeper", "other", false));
        yield return ("precise/writes/reader_writes_nothing", () => Writes(f.Function("readOnly"), "Keeper", "n", false));
        yield return ("precise/writes/release_of_a_destructorless_class", () =>
            Require(!f.Writes.Of([new IrRunRelease(f.Type("Keeper"))]).MayWrite(f.FieldAccess("Keeper", "n")),
                "releasing a Keeper, which has no destructor, writes nothing"));
    }

    static IEnumerable<(string, Func<string?>)> CalleeCases(Facts f)
    {
        yield return ("sound/callees/interface_call_runs_every_implementation", () =>
            Require(f.Runs(f.Dispatched("Sink", "put")).IsSupersetOf(["Storing.put", "Ignoring.put"]), "an interface call must run each implementation"));
        yield return ("sound/callees/virtual_call_runs_the_overrides", () =>
            Require(f.Runs(f.Dispatched("Parent", "take")).IsSupersetOf(["Parent.take", "Child.take"]), "a virtual call must run the method and its overrides"));
        yield return ("sound/callees/direct_call_runs_only_the_method", () =>
            Require(f.Runs(f.Run(f.Method("Parent", "take"))).SetEquals(["Parent.take"]), "a direct call runs exactly its method"));
        yield return ("sound/callees/release_runs_the_destructor", () =>
            Require(f.Runs(new IrRunRelease(f.Type("Dying"))).Any(r => r.EndsWith("Dying")), "releasing a Dying must run its destructor"));
    }

    static IEnumerable<(string, Func<string?>)> LambdaAndCycleCases(Facts f)
    {
        yield return ("sound/lambda_writes/assigned_capture", () =>
            Require(f.Writes.Lambdas.Around(f.LambdaIn("counts")).Any(l => l.Name == "count"), "a lambda that assigns a local around it must be reported as changing it"));
        yield return ("sound/lambda_writes/may_change", () =>
            Require(f.Writes.Lambdas.MayChange(f.Writes.Lambdas.Around(f.LambdaIn("counts")).First(l => l.Name == "count")), "MayChange must agree with Around"));
        yield return ("precise/lambda_writes/read_only_capture", () =>
            Require(!f.Writes.Lambdas.Around(f.LambdaIn("capturesAndKeeps")).Any(l => l.Name is "h" or "o"), "a lambda that only writes through a capture doesn't change the local"));
        yield return ("sound/cycles/self_reference", () =>
            Require(f.Callees.Cycles.IsCyclic(f.Type("Keeper")), "a class holding its own type may be part of a cycle"));
        yield return ("sound/cycles/destroyable_includes_itself", () =>
            Require(f.Callees.Cycles.Destroyable(f.Type("Dying"))?.Contains(f.Type("Dying")) == true, "releasing a Dying may destroy a Dying"));
        yield return ("precise/cycles/leaf_class", () =>
            Require(!f.Callees.Cycles.IsCyclic(f.Type("Dying")), "a class holding only a Keeper, which holds no Dying, is not cyclic"));
    }

    /// <summary>Facts found from the IR refuse to answer once a pass has changed it, whichever fact asks.</summary>
    static IEnumerable<TestResult> Stale(BoundProgram bound, IrProgram fixture)
    {
        var program = new IrProgram(bound);
        program.Functions.AddRange(fixture.Functions);
        var callees = new Callees(program);
        var escapes = Escapes.Find(callees);
        var function = program.Functions.First(f => f.Symbol.Name == "store");
        program.Changed();
        yield return Run("stale/escapes", () =>
        {
            try
            {
                escapes.Flow(function);
                return "an escape flow of an earlier generation was given";
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        });
        yield return Run("stale/cycles", () =>
        {
            try
            {
                _ = callees.Cycles;
                return "cycles of an earlier generation answered";
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        });
    }
}
