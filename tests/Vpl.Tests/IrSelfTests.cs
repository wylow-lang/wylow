using Vpl.Compiler;
using Vpl.Compiler.Ir;
using Vpl.Compiler.Semantics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

// Hand-built IR the verifier must accept, mostly the listings of docs/design/ir.md, and IR it must
// reject, each for one reason, so that each of its checks is known to fire.
static class IrSelfTests
{
    const string Fixture = """
        class Obj {
            mut int x;
            Obj() { }
        }

        class Holder {
            mut Obj b;
            Obj fixed;

            Holder(Obj b) {
                this.b = b;
                fixed = b;
            }
        }

        struct Vec {
            int x;
        }

        int f() { return 1; }
        void use(int value) { }
        void take(Obj o) { }
        void two(ref Vec? a, in int b) { }

        void main() { }
        """;

    sealed record Case(string Name, Func<IrFunction> Build, string? Error = null, string? Printed = null);

    public static List<TestResult> Run()
    {
        var source = new SourceText("ir_self_test.vpl", Fixture);
        var compilation = Compilation.Compile(source);
        if (compilation.Program is not { } program)
            return [new("ir/fixture", string.Join('\n', compilation.Diagnostics))];
        var b = new Builder(program, source);
        return [.. Cases(b).Select(c => Check(program, c))];
    }

    static TestResult Check(BoundProgram bound, Case test)
    {
        var name = $"ir/{test.Name}";
        try
        {
            var function = test.Build();
            var program = new IrProgram(bound);
            program.Functions.Add(function);
            var printed = IrPrinter.Print(program);
            var errors = IrVerifier.Verify(program);
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
        var list = b.Type("List").Symbol!.Construct([VplType.Int]);
        var span = b.Program.Stdlib.ReadOnlySpan!.Construct([VplType.Int]);
        var x = b.Field("Obj", "x");
        var field = b.Field("Holder", "b");
        var fixedField = b.Field("Holder", "fixed");
        var vecX = b.Field("Vec", "x");
        var index = b.Method(list, SyntaxFacts.IndexerName);
        var get = b.Method(list, "get");

        yield return new("straight_line", () =>
        {
            var o = b.Parameter("obj", obj);
            var (t0, t1, t2) = (b.Temp(VplType.Int), b.Temp(VplType.Int), b.Temp(VplType.Int));
            return b.Function("straight_line", [o],
                new IrLet(t0, Use(IrRead.Copy(IrPlace.Deref(o).Field(x)))),
                new IrLet(t1, b.Call("f")),
                new IrLet(t2, new IrBinary(BoundBinaryOperator.Add, Move(t0), Move(t1), VplType.Int, b.Loc)),
                new IrDo(b.Call("use", Move(t2))));
        }, Printed: """
            fn straight_line(obj: Obj) {
              let %0: int = copy (*obj).x
              let %1: int = call f()
              let %2: int = add move %0, move %1 @ir_self_test.vpl:1:1
              do call use(move %2)
            }
            """);

        yield return new("hold_before_value", () =>
        {
            var a = b.Parameter("a", holder);
            var h = b.Temp(obj, IrLocalKind.Hold);
            var v = b.Temp(VplType.Int);
            return b.Function("hold_before_value", [a],
                new IrLet(h, new IrHold(IrPlace.Deref(a).Field(field), borrows: false)),
                new IrLet(v, b.Call("f")),
                new IrStore(IrPlace.Deref(h).Field(x), Move(v)));
        });

        yield return new("borrowed_list_store", () =>
        {
            var l = b.Parameter("list", list);
            var h = b.Temp(list, IrLocalKind.Hold);
            var (i, v) = (b.Temp(VplType.Int), b.Temp(VplType.Int));
            var r = b.Temp(VplType.Int, IrLocalKind.Reference, RefKind.Ref);
            return b.Function("borrowed_list_store", [l],
                new IrLet(h, new IrHold(new IrPlace(l), borrows: true)),
                new IrLet(i, b.Call("f")),
                new IrLet(r, new IrCall(index, IrRead.Borrow(new IrPlace(h)), [Move(i)], b.Loc)),
                new IrLet(v, b.Call("f")),
                new IrStore(IrPlace.Deref(r), Move(v)));
        });

        // Only the whole-program analysis knows that the call doesn't write the element.
        IrFunction ElementAcrossCall(string name, bool unwritten)
        {
            var l = b.Parameter("list", list);
            var h = b.Temp(list, IrLocalKind.Hold);
            var r = b.Temp(VplType.Int, IrLocalKind.Reference, RefKind.Ref);
            var e = new IrLocal("e", VplType.Int, IrLocalKind.Borrowed) { IsUnwrittenElement = unwritten };
            return b.Function(name, [l],
                new IrLet(h, new IrHold(new IrPlace(l), borrows: true)),
                new IrLet(r, new IrCall(index, IrRead.Borrow(new IrPlace(h)), [IrConstant.Int(0)], b.Loc)),
                new IrLet(e, Use(IrRead.Borrow(IrPlace.Deref(r)))),
                new IrDo(b.Call("f")),
                new IrDo(b.Call("use", IrRead.Copy(new IrPlace(e)))));
        }
        yield return new("unwritten_element_across_call", () => ElementAcrossCall("unwritten_element_across_call", unwritten: true));
        yield return new("alias_across_call", () => ElementAcrossCall("alias_across_call", unwritten: false), Error: "the place it borrows may be written");

        // Nothing runs between taking the reference and storing through it, so no borrow is needed.
        yield return new("unborrowed_store_running_nothing", () =>
        {
            var l = b.Parameter("list", list);
            var r = b.Temp(VplType.Int, IrLocalKind.Reference, RefKind.Ref);
            return b.Function("unborrowed_store_running_nothing", [l],
                new IrLet(r, new IrCall(index, IrRead.Borrow(new IrPlace(l)), [IrConstant.Int(0)], b.Loc)),
                new IrStore(IrPlace.Deref(r), IrConstant.Int(5)));
        });

        yield return new("reference_test_lets_go_early", () =>
        {
            var l = b.Parameter("list", list);
            var h = b.Temp(list, IrLocalKind.Hold);
            var slot = b.Local("slot", VplType.Int.NullableReference(RefKind.Ref), IrLocalKind.Reference, RefKind.Ref);
            var found = b.Temp(VplType.Bool);
            return b.Function("reference_test_lets_go_early", [l],
                new IrBlock([
                    new IrLet(h, new IrHold(new IrPlace(l), borrows: true)),
                    new IrLet(slot, new IrCall(get, IrRead.Borrow(new IrPlace(h)), [IrConstant.Int(2)])),
                    new IrLet(found, new IrTest(IrTestKind.NotNull, IrRead.Copy(new IrPlace(slot)))),
                    new IrIf(Move(found),
                        new IrBlock([new IrStore(IrPlace.Deref(slot), IrConstant.Int(1)), new IrDo(b.Call("f"))]),
                        new IrBlock([new IrEnd(h), new IrDo(b.Call("f"))])),
                ]));
        });

        yield return new("span_loop", () =>
        {
            var l = b.Parameter("list", list);
            var h = b.Temp(list, IrLocalKind.Hold);
            var s = b.Local("span", span);
            var i = b.Local("i", VplType.Int);
            var (n, more, next) = (b.Temp(VplType.Int), b.Temp(VplType.Bool), b.Temp(VplType.Int));
            var p = b.Temp(VplType.Int, IrLocalKind.Reference, RefKind.In);
            var e = b.Local("e", VplType.Int);
            var loop = new IrLoop();
            loop.Body.Statements.AddRange([
                new IrLet(n, new IrCall(b.Method(span, "length"), new IrRefArgument(RefKind.In, new IrPlace(s)), [])),
                new IrLet(more, new IrBinary(BoundBinaryOperator.Less, IrRead.Copy(new IrPlace(i)), Move(n), VplType.Bool)),
                new IrIf(Move(more), new IrBlock(), new IrBlock([new IrBreak(loop)])),
                new IrBlock([
                    new IrLet(p, new IrCall(b.Method(span, SyntaxFacts.IndexerName), new IrRefArgument(RefKind.In, new IrPlace(s)), [IrRead.Copy(new IrPlace(i))], b.Loc)),
                    new IrLet(e, Use(IrRead.Copy(IrPlace.Deref(p)))),
                    new IrDo(b.Call("use", IrRead.Copy(new IrPlace(e)))),
                ]),
            ]);
            loop.Continue = new IrBlock([
                new IrLet(next, new IrBinary(BoundBinaryOperator.Add, IrRead.Copy(new IrPlace(i)), IrConstant.Int(1), VplType.Int, b.Loc)),
                new IrStore(new IrPlace(i), Move(next)),
            ]);
            return b.Function("span_loop", [l],
                new IrLet(h, new IrHold(new IrPlace(l), borrows: true)),
                new IrLet(s, new IrCall(b.Method(list, "asReadOnlySpan"), IrRead.Borrow(new IrPlace(h)), [])),
                new IrLet(i, Use(IrConstant.Int(0))),
                loop);
        });

        yield return new("value_out_of_control_flow", () =>
        {
            var c = b.Parameter("c", VplType.Bool);
            var r = b.Temp(VplType.Int);
            return b.Function("value_out_of_control_flow", [c],
                new IrDeclare(r),
                new IrIf(IrRead.Copy(new IrPlace(c)),
                    new IrBlock([new IrStore(new IrPlace(r), IrConstant.Int(1))]),
                    new IrBlock([new IrStore(new IrPlace(r), IrConstant.Int(2))])),
                new IrDo(b.Call("use", IrRead.Copy(new IrPlace(r)))));
        });

        // Flow analysis knows the name is read only where its pattern matched, which the verifier trusts.
        yield return new("binding_stored_where_matched", () =>
        {
            var c = b.Parameter("c", VplType.Bool);
            var x = b.Local("x", VplType.Int);
            return b.Function("binding_stored_where_matched", [c],
                new IrDeclare(x) { IsBinding = true },
                new IrIf(IrRead.Copy(new IrPlace(c)), new IrBlock([new IrStore(new IrPlace(x), IrConstant.Int(1))])),
                new IrIf(IrRead.Copy(new IrPlace(c)), new IrBlock([new IrDo(b.Call("use", IrRead.Copy(new IrPlace(x))))])));
        });

        // The store makes the loop's entry state change, so its body is verified a second time.
        yield return new("loop_verified_twice", () =>
        {
            var c = b.Parameter("c", VplType.Bool);
            var x = b.Local("x", VplType.Int);
            var t = b.Temp(VplType.Int);
            var loop = new IrLoop();
            loop.Body.Statements.AddRange([
                new IrLet(t, Use(IrConstant.Int(1))),
                new IrStore(new IrPlace(x), Move(t)),
                new IrIf(IrRead.Copy(new IrPlace(c)), new IrBlock([new IrBreak(loop)])),
            ]);
            return b.Function("loop_verified_twice", [c], new IrDeclare(x), loop);
        });

        yield return new("borrowed_fixed_field_across_call", () =>
        {
            var a = b.Parameter("a", holder);
            return b.Function("borrowed_fixed_field_across_call", [a],
                new IrDo(b.Call("take", IrRead.Borrow(IrPlace.Deref(a).Field(fixedField)))));
        });

        yield return new("temporary_stored", () =>
        {
            var t = b.Temp(VplType.Int);
            return b.Function("temporary_stored", [],
                new IrLet(t, Use(IrConstant.Int(1))),
                new IrStore(new IrPlace(t), IrConstant.Int(2)));
        }, Error: "is a temporary, defined once");

        yield return new("outside_scope", () =>
        {
            var t = b.Temp(VplType.Int);
            return b.Function("outside_scope", [],
                new IrBlock([new IrLet(t, Use(IrConstant.Int(1)))]),
                new IrDo(b.Call("use", IrRead.Copy(new IrPlace(t)))));
        }, Error: "is used outside its scope");

        yield return new("moved_twice", () =>
        {
            var t = b.Temp(VplType.Int);
            return b.Function("moved_twice", [],
                new IrLet(t, Use(IrConstant.Int(1))),
                new IrDo(b.Call("use", Move(t))),
                new IrDo(b.Call("use", Move(t))));
        }, Error: "is used after it is moved");

        yield return new("deref_not_at_base", () =>
        {
            var a = b.Parameter("a", holder);
            var t = b.Temp(VplType.Int);
            return b.Function("deref_not_at_base", [a],
                new IrLet(t, Use(IrRead.Copy(IrPlace.Deref(a).Field(field).Field(x)))));
        }, Error: "dereferences only at its base");

        yield return new("reference_after_hold_ends", () =>
        {
            var l = b.Parameter("list", list);
            var h = b.Temp(list, IrLocalKind.Hold);
            var r = b.Temp(VplType.Int, IrLocalKind.Reference, RefKind.Ref);
            return b.Function("reference_after_hold_ends", [l],
                new IrLet(h, new IrHold(new IrPlace(l), borrows: true)),
                new IrLet(r, new IrCall(index, IrRead.Borrow(new IrPlace(h)), [IrConstant.Int(0)], b.Loc)),
                new IrEnd(h),
                new IrStore(IrPlace.Deref(r), IrConstant.Int(1)));
        }, Error: "was ended");

        yield return new("unborrowed_reference_across_call", () =>
        {
            var l = b.Parameter("list", list);
            var r = b.Temp(VplType.Int, IrLocalKind.Reference, RefKind.Ref);
            var v = b.Temp(VplType.Int);
            return b.Function("unborrowed_reference_across_call", [l],
                new IrLet(r, new IrCall(index, IrRead.Borrow(new IrPlace(l)), [IrConstant.Int(0)], b.Loc)),
                new IrLet(v, b.Call("f")),
                new IrStore(IrPlace.Deref(r), Move(v)));
        }, Error: "points into a Borrowable that isn't borrowed");

        yield return new("unborrowed_reference_across_release", () =>
        {
            var l = b.Parameter("list", list);
            var p = b.Parameter("p", obj);
            var r = b.Temp(VplType.Int, IrLocalKind.Reference, RefKind.Ref);
            var o = b.Temp(obj);
            return b.Function("unborrowed_reference_across_release", [l, p],
                new IrLet(r, new IrCall(index, IrRead.Borrow(new IrPlace(l)), [IrConstant.Int(0)], b.Loc)),
                new IrBlock([new IrLet(o, Use(IrRead.Copy(new IrPlace(p))))]),
                new IrStore(IrPlace.Deref(r), IrConstant.Int(1)));
        }, Error: "a release at the end of a block");

        yield return new("borrowed_local_written", () =>
        {
            var o = b.Parameter("o", obj);
            var alias = b.Local("x", VplType.Int, IrLocalKind.Borrowed);
            return b.Function("borrowed_local_written", [o],
                new IrLet(alias, Use(IrRead.Borrow(IrPlace.Deref(o).Field(x)))),
                new IrStore(IrPlace.Deref(o).Field(x), IrConstant.Int(2)),
                new IrDo(b.Call("use", IrRead.Copy(new IrPlace(alias)))));
        }, Error: "the place it borrows may be written");

        yield return new("borrowed_mut_field_across_call", () =>
        {
            var a = b.Parameter("a", holder);
            return b.Function("borrowed_mut_field_across_call", [a],
                new IrDo(b.Call("take", IrRead.Borrow(IrPlace.Deref(a).Field(field)))));
        }, Error: "is used during the call of 'take'");

        yield return new("reference_into_replaced_nullable", () =>
        {
            var w = b.Parameter("w", vec.Nullable);
            var v = b.Local("v", vec.Nullable);
            var r = b.Local("r", VplType.Int, IrLocalKind.Reference, RefKind.Ref);
            return b.Function("reference_into_replaced_nullable", [w],
                new IrLet(v, Use(IrRead.Copy(new IrPlace(w)))),
                new IrLet(r, new IrRef(RefKind.Ref, new IrPlace(v).Assumed().Field(vecX))),
                new IrStore(new IrPlace(v), IrRead.Copy(new IrPlace(w))),
                new IrStore(IrPlace.Deref(r), IrConstant.Int(1)));
        }, Error: "the nullable value or variant it points into may be replaced");

        yield return new("ref_argument_replaces_nullable_in_use", () =>
        {
            var w = b.Parameter("w", vec.Nullable);
            var v = b.Local("v", vec.Nullable);
            return b.Function("ref_argument_replaces_nullable_in_use", [w],
                new IrLet(v, Use(IrRead.Copy(new IrPlace(w)))),
                new IrDo(new IrCall(b.Function("two"), null, [
                    new IrRefArgument(RefKind.Ref, new IrPlace(v)),
                    new IrRefArgument(RefKind.In, new IrPlace(v).Assumed().Field(vecX)),
                ])));
        }, Error: "is used during the call of 'two'");

        yield return new("slot_in_nested_loop", () =>
        {
            var s = b.Temp(obj, IrLocalKind.Slot);
            var o = b.Temp(obj);
            var loop = new IrLoop();
            loop.Body.Statements.AddRange([new IrLet(o, new IrNew(obj, b.Constructor("Obj"), []) { Slot = s }), new IrBreak(loop)]);
            return b.Function("slot_in_nested_loop", [], new IrDeclare(s), loop);
        }, Error: "made inside a loop or lambda nested in the slot's block");

        yield return new("join_stored_twice", () =>
        {
            var r = b.Temp(VplType.Int);
            return b.Function("join_stored_twice", [],
                new IrDeclare(r),
                new IrStore(new IrPlace(r), IrConstant.Int(1)),
                new IrStore(new IrPlace(r), IrConstant.Int(2)));
        }, Error: "may be stored more than once");

        yield return new("join_not_stored_on_every_path", () =>
        {
            var c = b.Parameter("c", VplType.Bool);
            var r = b.Temp(VplType.Int);
            return b.Function("join_not_stored_on_every_path", [c],
                new IrDeclare(r),
                new IrIf(IrRead.Copy(new IrPlace(c)), new IrBlock([new IrStore(new IrPlace(r), IrConstant.Int(1))])),
                new IrDo(b.Call("use", IrRead.Copy(new IrPlace(r)))));
        }, Error: "may be read before it is stored");

        yield return new("temporary_binding", () =>
        {
            var r = b.Temp(VplType.Int);
            return b.Function("temporary_binding", [], new IrDeclare(r) { IsBinding = true });
        }, Error: "only a value local can be a pattern's binding");

        yield return new("break_outside_loop", () =>
        {
            var loop = new IrLoop();
            loop.Body.Statements.Add(new IrBreak(loop));
            return b.Function("break_outside_loop", [], loop, new IrBreak(loop));
        }, Error: "isn't inside the loop it names");

        yield return new("store_type_mismatch", () =>
        {
            var o = b.Parameter("o", obj);
            return b.Function("store_type_mismatch", [o], new IrStore(IrPlace.Deref(o).Field(x), IrConstant.Bool(true)));
        }, Error: "stores a 'bool' in a 'int'");

        yield return new("falls_off_the_end", () => b.Function("falls_off_the_end", [], VplType.Int),
            Error: "the end of the function is reachable");
    }

    static IrUse Use(IrOperand operand) => new(operand);
    static IrRead Move(IrLocal local) => IrRead.Move(new IrPlace(local));

    sealed class Builder(BoundProgram program, SourceText source)
    {
        public BoundProgram Program { get; } = program;
        public IrLoc Loc { get; } = new(new Location(source, new TextSpan(0, 0)));

        public VplType Type(string name) => Program.Types.First(t => t.Name == name).Type;

        public FieldSymbol Field(string type, string name) => Program.Types.First(t => t.Name == type).FindField(name)!;

        public MethodReference Method(VplType owner, string name) => new(owner.Symbol!.Methods[name][0], owner, []);

        public MethodReference Function(string name) =>
            new(Program.Functions.First(f => f.Symbol.Name == name && f.Symbol.ContainingType is null).Symbol, null, []);

        public MethodReference Constructor(string type)
        {
            var symbol = Program.Types.First(t => t.Name == type);
            return new(symbol.Constructors[0], symbol.Type, []);
        }

        public IrCall Call(string function, params IrArgument[] arguments) => new(Function(function), null, arguments);

        public IrLocal Temp(VplType type, IrLocalKind kind = IrLocalKind.Value, RefKind refKind = RefKind.None) =>
            new("t", type, kind) { IsTemporary = true, RefKind = refKind };

        public IrLocal Local(string name, VplType type, IrLocalKind kind = IrLocalKind.Value, RefKind refKind = RefKind.None) =>
            new(name, type, kind) { RefKind = refKind };

        public IrLocal Parameter(string name, VplType type) => new(name, type, IrLocalKind.Value) { IsParameter = true };

        public IrFunction Function(string name, IrLocal[] parameters, params IrStatement[] body) => Function(name, parameters, VplType.Void, body);

        public IrFunction Function(string name, IrLocal[] parameters, VplType returnType, params IrStatement[] body)
        {
            var function = new IrFunction(new FunctionSymbol(name, [], returnType), name) { Body = new IrBlock(body) };
            function.Parameters.AddRange(parameters);
            return function;
        }
    }
}
