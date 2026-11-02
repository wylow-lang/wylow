using Wyl.Compiler;
using Wyl.Compiler.Analysis;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;
using Wyl.Harness;

// Hand-built IR the verifier must accept, mostly the listings of docs/design/ir.md, and IR it must
// reject, each for one reason, so that each of its checks is known to fire.
static partial class IrSelfTests
{
    const string Fixture = """
        class Obj {
            mut int x;
            Obj() { }
        }

        class Holder {
            mut Obj b;
            Obj fixed;
            int[2] pair;

            Holder(Obj b) {
                this.b = b;
                fixed = b;
            }
        }

        struct Vec { int x; }
        struct Boxed { Obj? item; }
        struct Counter { int n = 1; }
        // Obj has no default, so Tagged's constructors must assign `a`.
        struct Tagged { Obj a; Tagged(Obj a) { this.a = a; } Tagged(Obj a, int b) { this(a); } }
        struct Tally { Counter c; }
        struct Built { int n = f(); }
        struct Nest { Vec? inner; }
        struct Cell { int v; mut ref int at() { return ref v; } }
        enum Shape { Circle(int r), Dot }

        int f() { return 1; }
        void use(int value) { }
        void take(Obj o) { }
        void two(ref Vec? a, in int b) { }
        void reset(ref Nest a, in int b) { }
        void replace(Holder h, Obj o) { h.b = new Obj(); }
        void call(Holder h, fn() -> void g) { h.b = new Obj(); g(); }
        void setFirst(List<int> list) { list[0] = 2; }
        void keep(escaping fn() -> void g) { }
        @CallerLocation
        void here() { }

        void main() { }
        """;

    sealed record Case(string Name, Func<IrFunction> Build, string? Error = null, string? Printed = null);

    public static List<TestResult> Run()
    {
        var source = new SourceText("ir_self_test.wyl", TestPrograms.WithStd(Fixture));
        var compilation = Compilation.Compile(source);
        if (compilation.Program is not { } program)
            return [new("ir/fixture", string.Join('\n', compilation.Diagnostics))];
        var b = new Builder(program, source);
        return [.. Cases(b).Concat(PassOutputs(b)).Concat(Rejections(b)).Select(c => Check(program, compilation.Ir!, c)), StaleFacts(program, compilation.Ir!), LetRule(b), PanicLocations(), EffectFacts(b), AccessFacts(b), PlacePathFacts(), .. EffectKinds(b)];
    }

    /// <summary>Facts found from the IR must refuse to answer once a pass has changed it.</summary>
    static TestResult StaleFacts(BoundProgram bound, IrProgram fixture)
    {
        var program = new IrProgram(bound);
        program.Functions.AddRange(fixture.Functions);
        var writes = Writes.Find(new Callees(program));
        IrRun[] runs = [new IrRunRelease(null)];
        writes.Of(runs);
        program.Changed();
        try
        {
            writes.Of(runs);
            return new("ir/stale_facts", "facts of an earlier generation answered");
        }
        catch (InvalidOperationException)
        {
            return new("ir/stale_facts", null);
        }
    }

    /// <summary>
    /// A let's local is of the kind its value makes, however the let is made: a copy of a <c>ref?</c>
    /// is one too, which lowering's copies of reads once made as a value.
    /// </summary>
    static TestResult LetRule(Builder b)
    {
        var slot = b.Local("slot", WylType.Int.NullableReference(RefKind.Ref), IrLocalKind.Reference, RefKind.Ref);
        var copy = new IrLet(new IrUse(IrRead.Copy(new IrPlace(slot)))) { Origin = null };
        if (copy.Local is not { Kind: IrLocalKind.Reference, RefKind: RefKind.Ref })
            return new("ir/let_rule", $"a copy of a 'ref?' defines a {copy.Local.Kind} local");
        try
        {
            _ = new IrLet(b.Temp(WylType.Int), new IrRef(RefKind.Ref, new IrPlace(b.Temp(WylType.Int)))) { Origin = null };
            return new("ir/let_rule", "a value local was defined by a reference");
        }
        catch (InvalidOperationException)
        {
            return new("ir/let_rule", null);
        }
    }

    /// <summary>An operation has a location exactly when it may panic, which its IR node decides, so the emitter can follow it.</summary>
    static TestResult PanicLocations()
    {
        var (one, loc) = (IrConstant.Int(1), new IrLoc((Location?)null));
        if (new IrBinary(BoundBinaryOperator.Less, one, one, WylType.Bool, loc).Loc is not null
            || new IrBinary(BoundBinaryOperator.Add, one, one, WylType.Int, loc).Loc is null)
            return new("ir/panic_locations", "a binary operation's location doesn't say whether it may panic");
        try
        {
            _ = new IrUnary(BoundUnaryOperator.Negate, one);
            return new("ir/panic_locations", "an integer negation was made without a location");
        }
        catch (ArgumentException)
        {
            return new("ir/panic_locations", null);
        }
    }

    static TestResult Check(BoundProgram bound, IrProgram fixture, Case test)
    {
        var name = $"ir/{test.Name}";
        try
        {
            var function = test.Build();
            var program = new IrProgram(bound);
            program.Functions.Add(function);
            var printed = IrPrinter.Print(program);
            // What the fixture's functions write, for the calls the case makes of them.
            var whole = new IrProgram(bound);
            // Lowering lists each lambda as a function of its own.
            var lambdas = IrWalk.Descendants(function.Body).OfType<IrLet>().Select(l => l.Value switch
            {
                IrLambda lambda => lambda.Function,
                IrClosure closure => closure.Function,
                _ => null,
            }).OfType<IrFunction>();
            whole.Functions.AddRange([.. fixture.Functions, function, .. lambdas]);
            var errors = IrVerifier.Verify(program, Writes.Find(new Callees(whole)));
            var shown = $"\n{printed}errors:\n  {string.Join("\n  ", errors)}";
            if (test.Error is null && errors.Count > 0)
                return new(name, "unexpected verifier errors:" + shown);
            if (test.Error is not null && (errors.Count != 1 || !errors[0].Contains(test.Error, StringComparison.Ordinal)))
                return new(name, $"expected exactly one error, containing '{test.Error}':" + shown);
            if (test.Printed is not null && printed.Replace("\r\n", "\n") != test.Printed.Replace("\r\n", "\n") + "\n\n")
                return new(name, $"printed differently; expected:\n{test.Printed}\ngot:\n{printed}");
            return new(name, null);
        }
        catch (Exception e)
        {
            return new(name, $"exception: {e}");
        }
    }

    static IEnumerable<Case> Cases(Builder b)
    {
        var obj = b.Type("Obj");
        var holder = b.Type("Holder");
        var vec = b.Type("Vec");
        var list = b.Type("List").Symbol!.Construct([WylType.Int]);
        var span = b.Program.Stdlib.ReadOnlySpan!.Construct([WylType.Int]);
        var x = b.Field("Obj", "x");
        var field = b.Field("Holder", "b");
        var fixedField = b.Field("Holder", "fixed");
        var vecX = b.Field("Vec", "x");
        var nest = b.Type("Nest");
        var inner = b.Field("Nest", "inner");
        var index = b.Method(list, SyntaxFacts.IndexerName);
        var get = b.Method(list, "get");

        yield return new("straight_line", () =>
        {
            var o = b.Parameter("obj", obj);
            var (t0, t1, t2) = (b.Temp(WylType.Int), b.Temp(WylType.Int), b.Temp(WylType.Int));
            return b.Function("straight_line", [o],
                new IrLet(t0, Use(IrRead.Copy(IrPlace.Deref(o).Field(x)))) { Origin = null },
                new IrLet(t1, b.Call("f")) { Origin = null },
                new IrLet(t2, new IrBinary(BoundBinaryOperator.Add, Move(t0), Move(t1), WylType.Int, b.Loc)) { Origin = null },
                new IrDo(b.Call("use", Move(t2))) { Origin = null });
        }, Printed: """
            fn straight_line(obj: Obj) {
              let %0: int = copy (*obj).x
              let %1: int = call f()
              let %2: int = add move %0, move %1 @ir_self_test.wyl:1:1
              do call use(move %2)
            }
            """);

        yield return new("hold_before_value", () =>
        {
            var a = b.Parameter("a", holder);
            var h = b.Temp(obj, IrLocalKind.Hold);
            var v = b.Temp(WylType.Int);
            return b.Function("hold_before_value", [a],
                new IrLet(h, new IrHold(IrPlace.Deref(a).Field(field), borrows: false)) { Origin = null },
                new IrLet(v, b.Call("f")) { Origin = null },
                new IrStore(IrPlace.Deref(h).Field(x), Move(v)) { Origin = null });
        });

        yield return new("borrowed_list_store", () =>
        {
            var l = b.Parameter("list", list);
            var h = b.Temp(list, IrLocalKind.Hold);
            var (i, v) = (b.Temp(WylType.Int), b.Temp(WylType.Int));
            var r = b.Temp(WylType.Int, IrLocalKind.Reference, RefKind.Ref);
            return b.Function("borrowed_list_store", [l],
                new IrLet(h, new IrHold(new IrPlace(l), borrows: true)) { Origin = null },
                new IrLet(i, b.Call("f")) { Origin = null },
                new IrLet(r, new IrCall(index, IrRead.Borrow(new IrPlace(h)), [Move(i)], b.Loc)) { Origin = null },
                new IrLet(v, b.Call("f")) { Origin = null },
                new IrStore(IrPlace.Deref(r), Move(v)) { Origin = null });
        });

        // Only the whole-program analysis knows that the call doesn't write the element.
        IrFunction ElementAcrossCall(string name, bool element, string callee = "f")
        {
            var l = b.Parameter("list", list);
            var h = b.Temp(list, IrLocalKind.Hold);
            var r = b.Temp(WylType.Int, IrLocalKind.Reference, RefKind.Ref);
            var e = b.Local("e", WylType.Int, IrLocalKind.Borrowed);
            var borrow = element ? IrRead.BorrowElement(IrPlace.Deref(r)) : IrRead.Borrow(IrPlace.Deref(r));
            return b.Function(name, [l],
                new IrLet(h, new IrHold(new IrPlace(l), borrows: true)) { Origin = null },
                new IrLet(r, new IrCall(index, IrRead.Borrow(new IrPlace(h)), [IrConstant.Int(0)], b.Loc)) { Origin = null },
                new IrLet(e, Use(borrow)) { Origin = null },
                new IrDo(callee == "f" ? b.Call("f") : b.Call(callee, IrRead.Copy(new IrPlace(l)))) { Origin = null },
                new IrDo(b.Call("use", IrRead.Copy(new IrPlace(e)))) { Origin = null });
        }
        yield return new("unwritten_element_across_call", () => ElementAcrossCall("unwritten_element_across_call", element: true));
        yield return new("written_element_across_call", () => ElementAcrossCall("written_element_across_call", element: true, callee: "setFirst"),
            Error: "an element of its type, or a part of one, may be written");
        yield return new("alias_across_call", () => ElementAcrossCall("alias_across_call", element: false), Error: "the place it borrows may be written");

        // A lambda's body runs during the call it is passed to, so what it refers into must last that long.
        yield return new("lambda_reference_across_call", () => LambdaReferenceAcrossCall(b, "lambda_reference_across_call", held: true));
        yield return new("lambda_reference_unheld_across_call", () => LambdaReferenceAcrossCall(b, "lambda_reference_unheld_across_call", held: false),
            Error: "runs during the call of 'call'");

        // Nothing runs between taking the reference and storing through it, so no borrow is needed.
        yield return new("unborrowed_store_running_nothing", () =>
        {
            var l = b.Parameter("list", list);
            var r = b.Temp(WylType.Int, IrLocalKind.Reference, RefKind.Ref);
            return b.Function("unborrowed_store_running_nothing", [l],
                new IrLet(r, new IrCall(index, IrRead.Borrow(new IrPlace(l)), [IrConstant.Int(0)], b.Loc)) { Origin = null },
                new IrStore(IrPlace.Deref(r), IrConstant.Int(5)) { Origin = null });
        });

        yield return new("let_kind_changed_after", () =>
        {
            var t = b.Temp(WylType.Int);
            var let = new IrLet(t, new IrUse(IrConstant.Int(1))) { Origin = null };
            t.Kind = IrLocalKind.Hold;
            return b.Function("let_kind_changed_after", [], let);
        }, Error: "a hold local is defined by what makes a value one");

        yield return new("reference_test_lets_go_early", () =>
        {
            var l = b.Parameter("list", list);
            var h = b.Temp(list, IrLocalKind.Hold);
            var slot = b.Local("slot", WylType.Int.NullableReference(RefKind.Ref), IrLocalKind.Reference, RefKind.Ref);
            var found = b.Temp(WylType.Bool);
            return b.Function("reference_test_lets_go_early", [l],
                new IrBlock([
                    new IrLet(h, new IrHold(new IrPlace(l), borrows: true)) { Origin = null },
                    new IrLet(slot, new IrCall(get, IrRead.Borrow(new IrPlace(h)), [IrConstant.Int(2)])) { Origin = null },
                    new IrLet(found, new IrTest(IrTestKind.NotNull, IrRead.Copy(new IrPlace(slot)))) { Origin = null },
                    new IrIf(Move(found),
                        new IrBlock([new IrStore(IrPlace.Deref(slot), IrConstant.Int(1)) { Origin = null }, new IrDo(b.Call("f")) { Origin = null }]) { Origin = null },
                        new IrBlock([new IrEnd(h) { Origin = null }, new IrDo(b.Call("f")) { Origin = null }]) { Origin = null }) { Origin = null },
                ]) { Origin = null });
        });

        yield return new("span_loop", () =>
        {
            var l = b.Parameter("list", list);
            var h = b.Temp(list, IrLocalKind.Hold);
            var s = b.Local("span", span);
            var i = b.Local("i", WylType.Int);
            var (n, more, next) = (b.Temp(WylType.Int), b.Temp(WylType.Bool), b.Temp(WylType.Int));
            var p = b.Temp(WylType.Int, IrLocalKind.Reference, RefKind.In);
            var e = b.Local("e", WylType.Int);
            var loop = new IrLoop { Origin = null };
            loop.Body.Statements.AddRange([
                new IrLet(n, new IrCall(b.Method(span, "length"), new IrRefArgument(RefKind.In, new IrPlace(s)), [])) { Origin = null },
                new IrLet(more, new IrBinary(BoundBinaryOperator.Less, IrRead.Copy(new IrPlace(i)), Move(n), WylType.Bool)) { Origin = null },
                new IrIf(Move(more), new IrBlock { Origin = null }, new IrBlock([new IrBreak(loop) { Origin = null }]) { Origin = null }) { Origin = null },
                new IrBlock([
                    new IrLet(p, new IrCall(b.Method(span, SyntaxFacts.IndexerName), new IrRefArgument(RefKind.In, new IrPlace(s)), [IrRead.Copy(new IrPlace(i))], b.Loc)) { Origin = null },
                    new IrLet(e, Use(IrRead.Copy(IrPlace.Deref(p)))) { Origin = null },
                    new IrDo(b.Call("use", IrRead.Copy(new IrPlace(e)))) { Origin = null },
                ]) { Origin = null },
            ]);
            loop.Continue = new IrBlock([
                new IrLet(next, new IrBinary(BoundBinaryOperator.Add, IrRead.Copy(new IrPlace(i)), IrConstant.Int(1), WylType.Int, b.Loc)) { Origin = null },
                new IrStore(new IrPlace(i), Move(next)) { Origin = null },
            ]) { Origin = null };
            return b.Function("span_loop", [l],
                new IrLet(h, new IrHold(new IrPlace(l), borrows: true)) { Origin = null },
                new IrLet(s, new IrCall(b.Method(list, "asReadOnlySpan"), IrRead.Borrow(new IrPlace(h)), [])) { Origin = null },
                new IrLet(i, Use(IrConstant.Int(0))) { Origin = null },
                loop);
        });

        yield return new("value_out_of_control_flow", () =>
        {
            var c = b.Parameter("c", WylType.Bool);
            var r = b.Temp(WylType.Int);
            return b.Function("value_out_of_control_flow", [c],
                new IrDeclare(r) { Origin = null },
                new IrIf(IrRead.Copy(new IrPlace(c)),
                    new IrBlock([new IrStore(new IrPlace(r), IrConstant.Int(1)) { Origin = null }]) { Origin = null },
                    new IrBlock([new IrStore(new IrPlace(r), IrConstant.Int(2)) { Origin = null }]) { Origin = null }) { Origin = null },
                new IrDo(b.Call("use", IrRead.Copy(new IrPlace(r)))) { Origin = null });
        });

        // An `is ref` name's hold is declared with it and made in the test, which ends it only where the name is null.
        IrFunction ReferenceBinding(string name, bool endsWhereFound)
        {
            var l = b.Parameter("list", list);
            var h = b.Temp(list, IrLocalKind.Hold);
            var slotType = WylType.Int.NullableReference(RefKind.Ref);
            var slot = b.Local("slot", slotType, IrLocalKind.Reference, RefKind.Ref);
            var r = b.Temp(slotType, IrLocalKind.Reference, RefKind.Ref);
            var (found, matched) = (b.Temp(WylType.Bool), b.Temp(WylType.Bool));
            IrBlock Ends() => new([new IrEnd(h) { Origin = null }]) { Origin = null };
            return b.Function(name, [l],
                new IrDeclare(h) { Origin = null },
                new IrDeclare(slot) { Origin = null },
                new IrDeclare(matched) { Origin = null },
                new IrBlock([
                    new IrLet(h, new IrHold(new IrPlace(l), borrows: true)) { Origin = null },
                    new IrLet(r, new IrCall(get, IrRead.Borrow(new IrPlace(h)), [IrConstant.Int(2)])) { Origin = null },
                    new IrStore(new IrPlace(slot), IrRead.Copy(new IrPlace(r))) { Origin = null },
                    new IrLet(found, new IrTest(IrTestKind.NotNull, IrRead.Copy(new IrPlace(slot)))) { Origin = null },
                    endsWhereFound ? new IrIf(IrRead.Copy(new IrPlace(found)), Ends(), new IrBlock { Origin = null }) { Origin = null } : new IrIf(IrRead.Copy(new IrPlace(found)), new IrBlock { Origin = null }, Ends()) { Origin = null },
                    new IrStore(new IrPlace(matched), Move(found)) { Origin = null },
                ]) { Origin = null },
                new IrIf(Move(matched), new IrBlock([new IrStore(IrPlace.Deref(slot), IrConstant.Int(1)) { Origin = null }]) { Origin = null }) { Origin = null });
        }
        yield return new("reference_binding_outlives_its_test", () => ReferenceBinding("reference_binding_outlives_its_test", endsWhereFound: false));
        yield return new("reference_binding_hold_ended_where_found", () => ReferenceBinding("reference_binding_hold_ended_where_found", endsWhereFound: true),
            Error: "was ended");

        // A name is read only where the condition it was stored under is true, however that condition was passed on.
        yield return new("binding_stored_where_matched", () =>
        {
            var c = b.Parameter("c", WylType.Bool);
            var x = b.Local("x", WylType.Int);
            var (matched, again, negated) = (b.Local("matched", WylType.Bool), b.Temp(WylType.Bool), b.Temp(WylType.Bool));
            return b.Function("binding_stored_where_matched", [c],
                new IrDeclare(x) { Origin = null },
                new IrDeclare(matched) { Origin = null },
                new IrIf(IrRead.Copy(new IrPlace(c)),
                    new IrBlock([new IrStore(new IrPlace(x), IrConstant.Int(1)) { Origin = null }, new IrStore(new IrPlace(matched), IrConstant.Bool(true)) { Origin = null }]) { Origin = null },
                    new IrBlock([new IrStore(new IrPlace(matched), IrConstant.Bool(false)) { Origin = null }]) { Origin = null }) { Origin = null },
                new IrLet(again, Use(IrRead.Move(new IrPlace(matched)))) { Origin = null },
                new IrLet(negated, new IrUnary(BoundUnaryOperator.LogicalNot, IrRead.Copy(new IrPlace(again)))) { Origin = null },
                new IrIf(IrRead.Copy(new IrPlace(again)), new IrBlock([new IrDo(b.Call("use", IrRead.Copy(new IrPlace(x)))) { Origin = null }]) { Origin = null }) { Origin = null },
                new IrIf(IrRead.Move(new IrPlace(negated)), new IrBlock { Origin = null }, new IrBlock([new IrDo(b.Call("use", IrRead.Copy(new IrPlace(x)))) { Origin = null }]) { Origin = null }) { Origin = null });
        });

        yield return new("binding_read_where_not_matched", () =>
        {
            var c = b.Parameter("c", WylType.Bool);
            var x = b.Local("x", WylType.Int);
            var matched = b.Local("matched", WylType.Bool);
            return b.Function("binding_read_where_not_matched", [c],
                new IrDeclare(x) { Origin = null },
                new IrDeclare(matched) { Origin = null },
                new IrIf(IrRead.Copy(new IrPlace(c)),
                    new IrBlock([new IrStore(new IrPlace(x), IrConstant.Int(1)) { Origin = null }, new IrStore(new IrPlace(matched), IrConstant.Bool(true)) { Origin = null }]) { Origin = null },
                    new IrBlock([new IrStore(new IrPlace(matched), IrConstant.Bool(false)) { Origin = null }]) { Origin = null }) { Origin = null },
                new IrIf(IrRead.Copy(new IrPlace(matched)), new IrBlock { Origin = null }, new IrBlock([new IrDo(b.Call("use", IrRead.Copy(new IrPlace(x)))) { Origin = null }]) { Origin = null }) { Origin = null });
        }, Error: "may be read before it is stored");

        // The store makes the loop's entry state change, so its body is verified a second time.
        yield return new("loop_verified_twice", () =>
        {
            var c = b.Parameter("c", WylType.Bool);
            var x = b.Local("x", WylType.Int);
            var t = b.Temp(WylType.Int);
            var loop = new IrLoop { Origin = null };
            loop.Body.Statements.AddRange([
                new IrLet(t, Use(IrConstant.Int(1))) { Origin = null },
                new IrStore(new IrPlace(x), Move(t)) { Origin = null },
                new IrIf(IrRead.Copy(new IrPlace(c)), new IrBlock([new IrBreak(loop) { Origin = null }]) { Origin = null }) { Origin = null },
            ]);
            return b.Function("loop_verified_twice", [c], new IrDeclare(x) { Origin = null }, loop);
        });

        yield return new("borrowed_fixed_field_across_call", () =>
        {
            var a = b.Parameter("a", holder);
            return b.Function("borrowed_fixed_field_across_call", [a],
                new IrDo(b.Call("take", IrRead.Borrow(IrPlace.Deref(a).Field(fixedField)))) { Origin = null });
        });

        yield return new("temporary_stored", () =>
        {
            var t = b.Temp(WylType.Int);
            return b.Function("temporary_stored", [],
                new IrLet(t, Use(IrConstant.Int(1))) { Origin = null },
                new IrStore(new IrPlace(t), IrConstant.Int(2)) { Origin = null });
        }, Error: "is a temporary, defined once");

        yield return new("outside_scope", () =>
        {
            var t = b.Temp(WylType.Int);
            return b.Function("outside_scope", [],
                new IrBlock([new IrLet(t, Use(IrConstant.Int(1))) { Origin = null }]) { Origin = null },
                new IrDo(b.Call("use", IrRead.Copy(new IrPlace(t)))) { Origin = null });
        }, Error: "is used outside its scope");

        yield return new("moved_twice", () =>
        {
            var t = b.Temp(WylType.Int);
            return b.Function("moved_twice", [],
                new IrLet(t, Use(IrConstant.Int(1))) { Origin = null },
                new IrDo(b.Call("use", Move(t))) { Origin = null },
                new IrDo(b.Call("use", Move(t))) { Origin = null });
        }, Error: "is used after it is moved");

        yield return new("deref_not_at_base", () =>
        {
            var a = b.Parameter("a", holder);
            var t = b.Temp(WylType.Int);
            return b.Function("deref_not_at_base", [a],
                new IrLet(t, Use(IrRead.Copy(IrPlace.Deref(a).Field(field).Field(x)))) { Origin = null });
        }, Error: "dereferences only at its base");

        yield return new("reference_after_hold_ends", () =>
        {
            var l = b.Parameter("list", list);
            var h = b.Temp(list, IrLocalKind.Hold);
            var r = b.Temp(WylType.Int, IrLocalKind.Reference, RefKind.Ref);
            return b.Function("reference_after_hold_ends", [l],
                new IrLet(h, new IrHold(new IrPlace(l), borrows: true)) { Origin = null },
                new IrLet(r, new IrCall(index, IrRead.Borrow(new IrPlace(h)), [IrConstant.Int(0)], b.Loc)) { Origin = null },
                new IrEnd(h) { Origin = null },
                new IrStore(IrPlace.Deref(r), IrConstant.Int(1)) { Origin = null });
        }, Error: "was ended");

        yield return new("unborrowed_reference_across_call", () =>
        {
            var l = b.Parameter("list", list);
            var r = b.Temp(WylType.Int, IrLocalKind.Reference, RefKind.Ref);
            var v = b.Temp(WylType.Int);
            return b.Function("unborrowed_reference_across_call", [l],
                new IrLet(r, new IrCall(index, IrRead.Borrow(new IrPlace(l)), [IrConstant.Int(0)], b.Loc)) { Origin = null },
                new IrLet(v, b.Call("f")) { Origin = null },
                new IrStore(IrPlace.Deref(r), Move(v)) { Origin = null });
        }, Error: "points into a Borrowable that isn't borrowed");

        yield return new("unborrowed_reference_across_release", () =>
        {
            var l = b.Parameter("list", list);
            var p = b.Parameter("p", obj);
            var r = b.Temp(WylType.Int, IrLocalKind.Reference, RefKind.Ref);
            var o = b.Temp(obj);
            return b.Function("unborrowed_reference_across_release", [l, p],
                new IrLet(r, new IrCall(index, IrRead.Borrow(new IrPlace(l)), [IrConstant.Int(0)], b.Loc)) { Origin = null },
                new IrBlock([new IrLet(o, Use(IrRead.Copy(new IrPlace(p)))) { Origin = null }]) { Origin = null },
                new IrStore(IrPlace.Deref(r), IrConstant.Int(1)) { Origin = null });
        }, Error: "a release at the end of a block");

        yield return new("borrowed_local_written", () =>
        {
            var o = b.Parameter("o", obj);
            var alias = b.Local("x", WylType.Int, IrLocalKind.Borrowed);
            return b.Function("borrowed_local_written", [o],
                new IrLet(alias, Use(IrRead.Borrow(IrPlace.Deref(o).Field(x)))) { Origin = null },
                new IrStore(IrPlace.Deref(o).Field(x), IrConstant.Int(2)) { Origin = null },
                new IrDo(b.Call("use", IrRead.Copy(new IrPlace(alias)))) { Origin = null });
        }, Error: "the place it borrows may be written");

        // Storage of one type is never another's (spec unsafe.aliasing), so a store through a
        // reference to a float leaves a borrowed int alone; a type parameter may be anything.
        IrFunction WrittenThrough(string name, WylType referenced)
        {
            var o = b.Parameter("o", obj);
            var r = new IrLocal("r", referenced, IrLocalKind.Reference, IrLocalRole.Parameter) { RefKind = RefKind.Ref };
            var v = b.Parameter("v", referenced);
            var alias = b.Local("x", WylType.Int, IrLocalKind.Borrowed);
            return b.Function(name, [o, r, v],
                new IrLet(alias, Use(IrRead.Borrow(IrPlace.Deref(o).Field(x)))) { Origin = null },
                new IrStore(IrPlace.Deref(r), IrRead.Copy(new IrPlace(v))) { Origin = null },
                new IrDo(b.Call("use", IrRead.Copy(new IrPlace(alias)))) { Origin = null });
        }
        yield return new("borrowed_local_unrelated_write", () => WrittenThrough("borrowed_local_unrelated_write", WylType.Float));
        yield return new("borrowed_local_generic_write", () => WrittenThrough("borrowed_local_generic_write", b.Type("List").TypeArguments[0]),
            Error: "the place it borrows may be written");

        // Nothing 'take' runs writes the field.
        yield return new("borrowed_mut_field_across_unwriting_call", () =>
        {
            var a = b.Parameter("a", holder);
            return b.Function("borrowed_mut_field_across_unwriting_call", [a],
                new IrDo(b.Call("take", IrRead.Borrow(IrPlace.Deref(a).Field(field)))) { Origin = null });
        });

        yield return new("borrowed_mut_field_across_call", () =>
        {
            var a = b.Parameter("a", holder);
            return b.Function("borrowed_mut_field_across_call", [a],
                new IrDo(b.Call("replace", IrRead.Copy(new IrPlace(a)), IrRead.Borrow(IrPlace.Deref(a).Field(field)))) { Origin = null });
        }, Error: "is used during the call of 'replace'");

        yield return new("reference_into_replaced_nullable", () =>
        {
            var w = b.Parameter("w", vec.Nullable);
            var v = b.Local("v", vec.Nullable);
            var r = b.Local("r", WylType.Int, IrLocalKind.Reference, RefKind.Ref);
            return b.Function("reference_into_replaced_nullable", [w],
                new IrLet(v, Use(IrRead.Copy(new IrPlace(w)))) { Origin = null },
                new IrLet(r, new IrRef(RefKind.Ref, new IrPlace(v).Assumed().Field(vecX))) { Origin = null },
                new IrStore(new IrPlace(v), IrRead.Copy(new IrPlace(w))) { Origin = null },
                new IrStore(IrPlace.Deref(r), IrConstant.Int(1)) { Origin = null });
        }, Error: "the nullable value or variant it points into may be replaced");

        yield return new("ref_argument_replaces_nullable_in_use", () =>
        {
            var w = b.Parameter("w", vec.Nullable);
            var v = b.Local("v", vec.Nullable);
            return b.Function("ref_argument_replaces_nullable_in_use", [w],
                new IrLet(v, Use(IrRead.Copy(new IrPlace(w)))) { Origin = null },
                new IrDo(new IrCall(b.Function("two"), null, [
                    new IrRefArgument(RefKind.Ref, new IrPlace(v)),
                    new IrRefArgument(RefKind.In, new IrPlace(v).Assumed().Field(vecX)),
                ])) { Origin = null });
        }, Error: "is used during the call of 'two'");

        // Writing inside the outer nullable value may replace the inner one the place lies in.
        yield return new("ref_argument_replaces_inner_nullable_in_use", () =>
        {
            var w = b.Parameter("w", nest.Nullable);
            var n = b.Local("n", nest.Nullable);
            return b.Function("ref_argument_replaces_inner_nullable_in_use", [w],
                new IrLet(n, Use(IrRead.Copy(new IrPlace(w)))) { Origin = null },
                new IrDo(new IrCall(b.Function("reset"), null, [
                    new IrRefArgument(RefKind.Ref, new IrPlace(n).Assumed()),
                    new IrRefArgument(RefKind.In, new IrPlace(n).Assumed().Field(inner).Assumed().Field(vecX)),
                ])) { Origin = null });
        }, Error: "is used during the call of 'reset'");

        yield return new("slot_in_nested_loop", () =>
        {
            var s = b.Temp(obj, IrLocalKind.Slot);
            var o = b.Temp(obj);
            var loop = new IrLoop { Origin = null };
            loop.Body.Statements.AddRange([new IrLet(o, new IrNew(obj, b.Constructor("Obj"), []) { Slot = s, Origin = b.Loc.Source!.Value }) { Origin = null }, new IrBreak(loop) { Origin = null }]);
            return b.Function("slot_in_nested_loop", [], new IrDeclare(s) { Origin = null }, loop);
        }, Error: "made inside a loop or lambda nested in the slot's block");

        yield return new("join_stored_twice", () =>
        {
            var r = b.Temp(WylType.Int);
            return b.Function("join_stored_twice", [],
                new IrDeclare(r) { Origin = null },
                new IrStore(new IrPlace(r), IrConstant.Int(1)) { Origin = null },
                new IrStore(new IrPlace(r), IrConstant.Int(2)) { Origin = null });
        }, Error: "may be stored more than once");

        yield return new("join_not_stored_on_every_path", () =>
        {
            var c = b.Parameter("c", WylType.Bool);
            var r = b.Temp(WylType.Int);
            return b.Function("join_not_stored_on_every_path", [c],
                new IrDeclare(r) { Origin = null },
                new IrIf(IrRead.Copy(new IrPlace(c)), new IrBlock([new IrStore(new IrPlace(r), IrConstant.Int(1)) { Origin = null }]) { Origin = null }) { Origin = null },
                new IrDo(b.Call("use", IrRead.Copy(new IrPlace(r)))) { Origin = null });
        }, Error: "may be read before it is stored");

        yield return new("break_outside_loop", () =>
        {
            var loop = new IrLoop { Origin = null };
            loop.Body.Statements.Add(new IrBreak(loop) { Origin = null });
            return b.Function("break_outside_loop", [], loop, new IrBreak(loop) { Origin = null });
        }, Error: "isn't inside the loop it names");

        yield return new("store_type_mismatch", () =>
        {
            var o = b.Parameter("o", obj);
            return b.Function("store_type_mismatch", [o], new IrStore(IrPlace.Deref(o).Field(x), IrConstant.Bool(true)) { Origin = null });
        }, Error: "stores a 'bool' in a 'int'");

        yield return new("falls_off_the_end", () => b.Function("falls_off_the_end", [], WylType.Int),
            Error: "must return a value on every path");

        yield return new("narrowed_after_test", () => Narrowed(b, "narrowed_after_test", tested: true));
        yield return new("narrowed_untested", () => Narrowed(b, "narrowed_untested", tested: false),
            Error: "might be null");
        yield return new("may_be_null_untested", () => Narrowed(b, "may_be_null_untested", tested: false, IrCheckKind.MayBeNull));
        yield return new("may_be_null_after_test", () => Narrowed(b, "may_be_null_after_test", tested: true, IrCheckKind.MayBeNull),
            Error: "'case null' can never match");

        IrFunction StructConstructor(string name, bool returnsSelf)
        {
            var self = b.Local("self", vec);
            var other = b.Local("other", vec);
            var function = new IrFunction(new FunctionSymbol(name, [], WylType.Void) { IsConstructor = true }, name)
            {
                Struct = self,
                Body = new IrBlock([
                    new IrLet(self, new IrDefault(vec)) { Origin = null },
                    new IrLet(other, new IrDefault(vec)) { Origin = null },
                    new IrStore(new IrPlace(self).Field(vecX), new IrConstant(WylType.Int, 1)) { Origin = null },
                    new IrReturn(IrRead.Copy(new IrPlace(returnsSelf ? self : other))) { Origin = null },
                ]) { Origin = null },
            };
            return function;
        }

        IrFunction Built(string name, WylType type, IReadOnlyList<bool> form, IrLocal[] parameters, Func<IrLocal, IrStatement[]> body, IrRvalue? start = null)
        {
            var self = b.Local("self", type);
            var function = new IrFunction(new FunctionSymbol(name, [], WylType.Void) { IsConstructor = true }, name)
            {
                Struct = self,
                Builds = form,
                Body = new IrBlock([new IrLet(self, start ?? new IrZeroed(type)) { Origin = null }, .. body(self), new IrReturn(IrRead.Copy(new IrPlace(self))) { Origin = null }]) { Origin = null },
            };
            function.Parameters.AddRange(parameters);
            return function;
        }

        var counter = b.Type("Counter");
        var counterN = b.Field("Counter", "n");
        yield return new("built_struct", () => Built("built_struct", counter, [false], [],
            self => [new IrStore(new IrPlace(self).Field(counterN), IrConstant.Int(1)) { Origin = null }]));
        yield return new("built_struct_zero_default", () => Built("built_struct_zero_default", vec, [false], [], _ => []));
        yield return new("built_struct_field_left_zeroed", () => Built("built_struct_field_left_zeroed", counter, [false], [], _ => []),
            Error: "field 'n' is left zeroed");
        // Making a Counter runs no code, but its default isn't zeroed storage either.
        yield return new("built_struct_nested_default_left_zeroed", () => Built("built_struct_nested_default_left_zeroed", b.Type("Tally"), [false], [], _ => []),
            Error: "field 'c' is left zeroed");
        yield return new("built_struct_read_early", () =>
        {
            var t = b.Temp(WylType.Int);
            return Built("built_struct_read_early", counter, [false], [], self => [
                new IrLet(t, Use(IrRead.Copy(new IrPlace(self).Field(counterN)))) { Origin = null },
                new IrStore(new IrPlace(self).Field(counterN), IrRead.Move(new IrPlace(t))) { Origin = null },
            ]);
        }, Error: "reads 'self' before it is built");
        yield return new("built_struct_from_default", () => Built("built_struct_from_default", vec, [false], [], _ => [], new IrDefault(vec)),
            Error: "a built struct starts as zeroed storage");
        yield return new("zeroed_outside_built", () =>
        {
            var self = b.Local("self", vec);
            return new IrFunction(new FunctionSymbol("zeroed_outside_built", [], WylType.Void) { IsConstructor = true }, "zeroed_outside_built")
            {
                Struct = self,
                Body = new IrBlock([new IrLet(self, new IrZeroed(vec)) { Origin = null }, new IrReturn(IrRead.Copy(new IrPlace(self))) { Origin = null }]) { Origin = null },
            };
        }, Error: "only a built struct starts as zeroed storage");

        yield return new("struct_constructor", () => StructConstructor("struct_constructor", returnsSelf: true));
        yield return new("struct_constructor_returns_other", () => StructConstructor("struct_constructor_returns_other", returnsSelf: false),
            Error: "a struct constructor returns its value");
    }

    /// <summary>
    /// A nullable source local used as not null, or tested by a <c>case null</c>, where a null test
    /// does or doesn't narrow it.
    /// </summary>
    static IrFunction Narrowed(Builder b, string name, bool tested, IrCheckKind kind = IrCheckKind.Narrowed)
    {
        var type = b.Type("Obj").Nullable;
        var maybe = b.Parameter("maybe", type);
        var local = b.SourceLocal("local", type);
        var test = b.Temp(WylType.Bool);
        IrStatement[] use = [new IrCheck(kind, [IrRead.Borrow(new IrPlace(local))], null) { Origin = null }];
        return b.Function(name, [maybe],
            new IrLet(local, Use(IrRead.Copy(new IrPlace(maybe)))) { Origin = null },
            new IrLet(test, new IrTest(IrTestKind.NotNull, IrRead.Copy(new IrPlace(local)))) { Origin = null },
            new IrIf(IrRead.Move(new IrPlace(test)), new IrBlock(tested ? use : []) { Origin = null }, new IrBlock(tested ? [] : use) { Origin = null }) { Origin = null });
    }

    static IrUse Use(IrOperand operand) => new(operand);
    static IrRead Move(IrLocal local) => IrRead.Move(new IrPlace(local));

    /// <summary>A lambda storing through a reference into a <c>mut</c> field's object, which the call it is passed to may replace.</summary>
    static IrFunction LambdaReferenceAcrossCall(Builder b, string name, bool held)
    {
        var a = b.Parameter("a", b.Type("Holder"));
        var obj = b.Type("Obj");
        var h = b.Temp(obj, held ? IrLocalKind.Hold : IrLocalKind.Borrowed);
        var r = b.Local("r", WylType.Int, IrLocalKind.Reference, RefKind.Ref);
        var call = b.Function("call");
        var lambda = new IrFunction(new FunctionSymbol("lambda", [], WylType.Void) { IsLambda = true }, $"{name}.lambda")
        {
            Body = new IrBlock([new IrStore(IrPlace.Deref(r), IrConstant.Int(1)) { Origin = null }]) { Origin = null },
        };
        var l = b.Temp(call.ParameterType(1));
        var place = IrPlace.Deref(a).Field(b.Field("Holder", "b"));
        var function = b.Function(name, [a],
            new IrLet(h, held ? new IrHold(place, borrows: false) : new IrUse(IrRead.Borrow(place))) { Origin = null },
            new IrLet(r, new IrRef(RefKind.Ref, IrPlace.Deref(h).Field(b.Field("Obj", "x")))) { Origin = null },
            new IrLet(l, new IrLambda(lambda, l.Type)) { Origin = null },
            new IrDo(new IrCall(call, null, [IrRead.Copy(new IrPlace(a)), IrRead.Move(new IrPlace(l))])) { Origin = null });
        return function;
    }

    sealed class Builder(BoundProgram program, SourceText source)
    {
        public BoundProgram Program { get; } = program;
        public IrLoc Loc { get; } = new(new Location(source, new TextSpan(0, 0)));

        public WylType Type(string name) => Program.Types.First(t => t.Name == name).Type;

        public FieldSymbol Field(string type, string name) => Program.Types.First(t => t.Name == type).FindField(name)!;

        public MethodReference Method(WylType owner, string name) => new(owner.Symbol!.Methods[name][0], owner, []);

        public MethodReference Function(string name) =>
            new(Program.Functions.First(f => f.Symbol.Name == name && f.Symbol.ContainingType is null).Symbol, null, []);

        public MethodReference Constructor(string type)
        {
            var symbol = Program.Types.First(t => t.Name == type);
            return new(symbol.Constructors[0], symbol.Type, []);
        }

        public IrCall Call(string function, params IrArgument[] arguments) => new(Function(function), null, arguments);

        public IrLocal Temp(WylType type, IrLocalKind kind = IrLocalKind.Value, RefKind refKind = RefKind.None) =>
            new("t", type, kind, IrLocalRole.Temporary) { RefKind = refKind };

        public IrLocal Local(string name, WylType type, IrLocalKind kind = IrLocalKind.Value, RefKind refKind = RefKind.None) =>
            new(name, type, kind, IrLocalRole.Variable) { RefKind = refKind };

        public IrLocal Parameter(string name, WylType type) => new(name, type, IrLocalKind.Value, IrLocalRole.Parameter);

        /// <summary>A variable the source names, whose rules the flow analysis checks.</summary>
        public IrLocal SourceLocal(string name, WylType type) => new(name, type, IrLocalKind.Value, IrLocalRole.Variable) { Symbol = new LocalSymbol(name, type, isParameter: false) };

        public IrFunction Function(string name, IrLocal[] parameters, params IrStatement[] body) => Function(name, parameters, WylType.Void, body);

        public IrFunction Function(string name, IrLocal[] parameters, WylType returnType, params IrStatement[] body)
        {
            var function = new IrFunction(new FunctionSymbol(name, [], returnType), name) { Body = new IrBlock(body) { Origin = null } };
            function.Parameters.AddRange(parameters);
            return function;
        }
    }
}
