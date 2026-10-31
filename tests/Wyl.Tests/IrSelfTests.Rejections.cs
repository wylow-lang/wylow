using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

// One function per verifier check that only a compiler bug can fail, each breaking that rule alone.
static partial class IrSelfTests
{
    static IEnumerable<Case> Rejections(Builder b)
    {
        var obj = b.Type("Obj");
        var vec = b.Type("Vec");
        var cell = b.Type("Cell");
        var shape = b.Type("Shape");
        var x = b.Field("Obj", "x");
        var vecX = b.Field("Vec", "x");
        var span = b.Program.Stdlib.ReadOnlySpan!.Construct([WylType.Int]);
        var escaping = b.Function("keep").ParameterType(0);
        var nonEscaping = b.Function("call").ParameterType(1);
        var location = b.Loc.Source!.Value;

        IrFunction Lambda(string name, bool escaping, params IrLocal[] captures)
        {
            var lambda = new IrFunction(new FunctionSymbol("lambda", [], WylType.Void) { IsLambda = true, IsEscaping = escaping }, $"{name}.lambda");
            lambda.Captures.AddRange(captures);
            return lambda;
        }
        IrClosure Closure(IrFunction lambda, params IrOperand[] captures) => new(lambda, captures, escaping) { Name = "closure", Origin = location };
        IrNew New(IrLocal? slot = null) => new(obj, b.Constructor("Obj"), []) { Slot = slot, Origin = location };
        IrRead ObjX(IrLocal o) => IrRead.Copy(IrPlace.Deref(o).Field(x));

        // Statements.
        yield return new("parameter_twice", () =>
        {
            var p = b.Parameter("p", WylType.Int);
            return b.Function("parameter_twice", [p, p]);
        }, Error: "is declared twice");

        yield return new("statement_after_return", () => b.Function("statement_after_return", [], Return(), Do(b.Call("f"))),
            Error: "the statement is unreachable");

        yield return new("borrowed_declared_without_value", () => b.Function("borrowed_declared_without_value", [],
            new IrDeclare(b.Local("x", WylType.Int, IrLocalKind.Borrowed)) { Origin = null }),
            Error: "a borrowed local needs its value where it is declared");

        yield return new("continue_in_continue_block", () =>
        {
            var c = b.Parameter("c", WylType.Bool);
            var loop = new IrLoop { Origin = null };
            loop.Body.Statements.Add(new IrIf(IrRead.Copy(new IrPlace(c)), Block(new IrBreak(loop) { Origin = null })) { Origin = null });
            loop.Continue = Block(new IrContinue(loop) { Origin = null });
            return b.Function("continue_in_continue_block", [c], loop);
        }, Error: "'continue' inside the loop's continue block");

        yield return new("let_twice", () =>
        {
            var v = b.Local("v", WylType.Int);
            return b.Function("let_twice", [], Let(v, new IrUse(IrConstant.Int(1))), Let(v, new IrUse(IrConstant.Int(2))));
        }, Error: "is declared twice");

        yield return new("declared_hold_made_outside_scope", () =>
        {
            var o = b.Parameter("o", obj);
            var h = b.Temp(obj, IrLocalKind.Hold);
            return b.Function("declared_hold_made_outside_scope", [o],
                Block(new IrDeclare(h) { Origin = null }),
                Let(h, new IrHold(new IrPlace(o), borrows: false)));
        }, Error: "is used outside its scope");

        yield return new("let_of_other_type", () => b.Function("let_of_other_type", [], Let(b.Local("v", WylType.Int), new IrUse(IrConstant.Bool(true)))),
            Error: "is a 'int', but is defined with a 'bool'");

        yield return new("store_of_borrow", () =>
        {
            var o = b.Parameter("o", obj);
            var v = b.Local("v", WylType.Int);
            return b.Function("store_of_borrow", [o],
                Let(v, new IrUse(IrConstant.Int(1))),
                new IrStore(new IrPlace(v), IrRead.Borrow(IrPlace.Deref(o).Field(x))) { Origin = null });
        }, Error: "a store needs a value of its own");

        yield return new("store_into_field_of_join", () =>
        {
            var v = b.Local("v", vec);
            var t = b.Temp(vec);
            return b.Function("store_into_field_of_join", [],
                Let(v, new IrDefault(vec)),
                new IrDeclare(t) { Origin = null },
                new IrStore(new IrPlace(t), IrRead.Copy(new IrPlace(v))) { Origin = null },
                new IrStore(new IrPlace(t).Field(vecX), IrConstant.Int(1)) { Origin = null });
        }, Error: "is a value out of control flow, stored only as a whole");

        yield return new("reference_stored_whole", () =>
        {
            var v = b.Local("v", WylType.Int);
            var r = b.Local("r", WylType.Int, IrLocalKind.Reference, RefKind.Ref);
            return b.Function("reference_stored_whole", [],
                Let(v, new IrUse(IrConstant.Int(1))),
                Let(r, new IrRef(RefKind.Ref, new IrPlace(v))),
                new IrStore(new IrPlace(r), IrConstant.Int(2)) { Origin = null });
        }, Error: "is a reference, which can't be made to refer elsewhere");

        yield return new("hold_stored_whole", () =>
        {
            var o = b.Parameter("o", obj);
            var h = b.Local("h", obj, IrLocalKind.Hold);
            return b.Function("hold_stored_whole", [o],
                Let(h, new IrHold(new IrPlace(o), borrows: false)),
                new IrStore(new IrPlace(h), IrRead.Copy(new IrPlace(o))) { Origin = null });
        }, Error: "is a hold local, which can't be stored");

        yield return new("index_check_of_bool", () => b.Function("index_check_of_bool", [],
            new IrCheck(IrCheckKind.Index, [IrConstant.Bool(true), IrConstant.Int(1)], b.Loc) { Origin = null }),
            Error: "the operands don't fit a 'Index' check");

        yield return new("check_without_location", () => b.Function("check_without_location", [],
            new IrCheck(IrCheckKind.Index, [IrConstant.Int(0), IrConstant.Int(1)], null) { Origin = null }),
            Error: "a check that may fail, and only one, carries a location");

        yield return new("if_on_int", () => b.Function("if_on_int", [], new IrIf(IrConstant.Int(1), Block()) { Origin = null }),
            Error: "the condition is a 'int', not a 'bool'");

        yield return new("returns_nothing_from_int", () => b.Function("returns_nothing_from_int", [], WylType.Int, Return()),
            Error: "returns nothing from a function returning 'int'");

        yield return new("returns_value_from_void", () => b.Function("returns_value_from_void", [], Return(IrConstant.Int(1))),
            Error: "returns a value from a function returning nothing");

        yield return new("returns_bool_from_int", () => b.Function("returns_bool_from_int", [], WylType.Int, Return(IrConstant.Bool(true))),
            Error: "returns a 'bool' from a function returning 'int'");

        yield return new("reference_returned_as_constant", () =>
            new IrFunction(new FunctionSymbol("reference_returned_as_constant", [], WylType.Int) { ReturnRefKind = RefKind.Ref }, "reference_returned_as_constant")
            {
                Body = Block(Return(IrConstant.Int(1))),
            }, Error: "a function returning a reference returns a reference local");

        yield return new("returns_borrow", () =>
        {
            var o = b.Parameter("o", obj);
            return b.Function("returns_borrow", [o], WylType.Int, Return(IrRead.Borrow(IrPlace.Deref(o).Field(x))));
        }, Error: "a return needs a value of its own");

        yield return new("end_of_value", () =>
        {
            var v = b.Local("v", WylType.Int);
            return b.Function("end_of_value", [], Let(v, new IrUse(IrConstant.Int(1))), new IrEnd(v) { Origin = null });
        }, Error: "only a hold or a slot can be ended");

        yield return new("caller_location_not_given", () => b.Function("caller_location_not_given", [],
            new IrCheck(IrCheckKind.Index, [IrConstant.Int(0), IrConstant.Int(1)], IrLoc.Caller) { Origin = null }),
            Error: "uses the caller's location in a function that isn't given one");

        // Places and operands.
        yield return new("undeclared_local", () => b.Function("undeclared_local", [], Do(b.Call("use", IrRead.Copy(new IrPlace(b.Local("v", WylType.Int)))))),
            Error: "isn't declared");

        yield return new("hold_used_after_end", () =>
        {
            var o = b.Parameter("o", obj);
            var h = b.Temp(obj, IrLocalKind.Hold);
            return b.Function("hold_used_after_end", [o],
                Let(h, new IrHold(new IrPlace(o), borrows: false)),
                new IrEnd(h) { Origin = null },
                Do(b.Call("take", IrRead.Copy(new IrPlace(h)))));
        }, Error: "is used after it ends");

        yield return new("deref_of_int", () =>
        {
            var p = b.Parameter("p", WylType.Int);
            return b.Function("deref_of_int", [p], Do(b.Call("use", IrRead.Copy(IrPlace.Deref(p)))));
        }, Error: "can't be dereferenced");

        yield return new("projection_on_reference_local", () =>
        {
            var v = b.Local("v", vec);
            var r = b.Local("r", vec, IrLocalKind.Reference, RefKind.Ref);
            return b.Function("projection_on_reference_local", [],
                Let(v, new IrDefault(vec)),
                Let(r, new IrRef(RefKind.Ref, new IrPlace(v))),
                Do(b.Call("use", IrRead.Copy(new IrPlace(r).Field(vecX)))));
        }, Error: "is a reference: the place it refers to is");

        yield return new("element_index_of_field", () =>
        {
            var a = b.Parameter("a", WylType.Int.FixedArrayOf(2));
            var o = b.Parameter("o", obj);
            return b.Function("element_index_of_field", [a, o], Do(b.Call("use", IrRead.Copy(new IrPlace(a).Element(ObjX(o))))));
        }, Error: "an element's index is an integer constant or local");

        yield return new("projection_not_applying", () =>
        {
            var p = b.Parameter("p", WylType.Int);
            return b.Function("projection_not_applying", [p], Do(new IrUse(IrRead.Copy(new IrPlace(p).Assumed()))));
        }, Error: "a projection doesn't apply to a 'int'");

        yield return new("field_moved", () =>
        {
            var o = b.Parameter("o", obj);
            return b.Function("field_moved", [o], Do(b.Call("use", IrRead.Move(IrPlace.Deref(o).Field(x)))));
        }, Error: "only a local's whole value can be moved");

        yield return new("moved_twice_in_one_binary", () =>
        {
            var t = b.Temp(WylType.Int);
            return b.Function("moved_twice_in_one_binary", [],
                Let(t, new IrUse(IrConstant.Int(1))),
                Do(new IrBinary(BoundBinaryOperator.Add, Move(t), Move(t), WylType.Int, b.Loc)));
        }, Error: "is moved twice");

        yield return new("borrow_not_kept_in_borrowed_local", () =>
        {
            var o = b.Parameter("o", obj);
            return b.Function("borrow_not_kept_in_borrowed_local", [o], Do(new IrUse(IrRead.Borrow(IrPlace.Deref(o).Field(x)))));
        }, Error: "a borrowed value is only kept in a borrowed local");

        // Rvalues.
        yield return new("invoke_of_int", () => b.Function("invoke_of_int", [], Do(new IrInvoke(IrConstant.Int(1), b.Function("f"), []))),
            Error: "invokes a 'int', which isn't a function");

        yield return new("new_of_struct", () => b.Function("new_of_struct", [],
            Do(new IrNew(vec, b.Constructor("Vec"), [IrConstant.Int(1)]) { Origin = location })),
            Error: "'new' makes an object, not a 'Vec'");

        yield return new("variant_of_wrong_field", () => b.Function("variant_of_wrong_field", [],
            Do(new IrVariant(shape, shape.Symbol!.FindVariant("Circle")!, [IrConstant.Bool(true)]))),
            Error: "the fields don't fit variant 'Circle'");

        yield return new("array_too_short", () => b.Function("array_too_short", [], Do(new IrArray(WylType.Int.FixedArrayOf(2), [IrConstant.Int(1)]))),
            Error: "the elements don't fit a 'int[2]'");

        yield return new("closure_of_non_escaping_lambda", () =>
        {
            var lambda = Lambda("closure_of_non_escaping_lambda", escaping: false);
            return b.Function("closure_of_non_escaping_lambda", [], Let(b.Temp(escaping), Closure(lambda)));
        }, Error: "a closure is made from an escaping lambda");

        yield return new("closure_missing_capture", () =>
        {
            var lambda = Lambda("closure_missing_capture", escaping: true, new IrLocal("c", WylType.Int, IrLocalKind.Value, IrLocalRole.Capture));
            return b.Function("closure_missing_capture", [], Let(b.Temp(escaping), Closure(lambda)));
        }, Error: "the captures don't fit the lambda's");

        yield return new("lambda_of_escaping_lambda", () =>
        {
            var lambda = Lambda("lambda_of_escaping_lambda", escaping: true);
            return b.Function("lambda_of_escaping_lambda", [], Let(b.Temp(nonEscaping), new IrLambda(lambda, nonEscaping)));
        }, Error: "an escaping lambda is made by a closure");

        yield return new("choice_by_bool", () =>
        {
            var g = b.Parameter("g", nonEscaping);
            return b.Function("choice_by_bool", [g],
                Let(b.Temp(nonEscaping), new IrChoose(IrConstant.Bool(true), [IrRead.Copy(new IrPlace(g)), IrRead.Copy(new IrPlace(g))], nonEscaping)));
        }, Error: "a choice selects a function with an integer");

        yield return new("data_of_int", () =>
        {
            var p = b.Parameter("p", WylType.Int);
            return b.Function("data_of_int", [p], Do(new IrData(new IrPlace(p))));
        }, Error: "'data' points into a fixed array, not a 'int'");

        yield return new("null_test_of_int", () => b.Function("null_test_of_int", [], Do(new IrTest(IrTestKind.NotNull, IrConstant.Int(1)))),
            Error: "a 'NotNull' test doesn't apply to a 'int'");

        yield return new("hold_of_int", () =>
        {
            var p = b.Parameter("p", WylType.Int);
            return b.Function("hold_of_int", [p], Let(b.Temp(WylType.Int, IrLocalKind.Hold), new IrHold(new IrPlace(p), borrows: false)));
        }, Error: "a hold holds an object or a string, not a 'int'");

        // Calls.
        yield return new("method_without_receiver", () => b.Function("method_without_receiver", [],
            Do(new IrCall(b.Method(span, "length"), null, []))),
            Error: "calls method 'length' without a receiver");

        yield return new("function_with_receiver", () =>
        {
            var o = b.Parameter("o", obj);
            return b.Function("function_with_receiver", [o], Do(new IrCall(b.Function("f"), IrRead.Copy(new IrPlace(o)), [])));
        }, Error: "calls 'f' with a receiver");

        yield return new("reference_from_copied_receiver", () =>
        {
            var c = b.Local("c", cell);
            return b.Function("reference_from_copied_receiver", [],
                Let(c, new IrDefault(cell)),
                Let(b.Temp(WylType.Int, IrLocalKind.Reference, RefKind.Ref), new IrCall(b.Method(cell, "at"), IrRead.Copy(new IrPlace(c)), [])));
        }, Error: "a reference or view a method returns points into its receiver");

        yield return new("reference_from_free_function", () =>
        {
            var free = new FunctionSymbol("free", [], WylType.Int) { ReturnRefKind = RefKind.Ref };
            return b.Function("reference_from_free_function", [],
                Let(b.Temp(WylType.Int, IrLocalKind.Reference, RefKind.Ref), new IrCall(new MethodReference(free, null, []), null, [])));
        }, Error: "only a method returns a reference, into its receiver");

        yield return new("caller_location_not_passed", () => b.Function("caller_location_not_passed", [], Do(b.Call("here"))),
            Error: "'here' is given its caller's location, which the call doesn't pass");

        yield return new("location_passed_to_function_without_one", () => b.Function("location_passed_to_function_without_one", [],
            Do(new IrCall(b.Function("f"), null, [], b.Loc))),
            Error: "passes a location to 'f', which takes none");

        yield return new("too_few_arguments", () => b.Function("too_few_arguments", [], Do(b.Call("use"))),
            Error: "passes 0 arguments to 'use', which takes 1");

        yield return new("argument_of_wrong_type", () => b.Function("argument_of_wrong_type", [], Do(b.Call("use", IrConstant.Bool(true)))),
            Error: "argument 1 doesn't fit parameter 'int value'");

        // Slots and lambdas.
        yield return new("new_into_value_local", () =>
        {
            var s = b.Temp(obj);
            return b.Function("new_into_value_local", [], new IrDeclare(s) { Origin = null }, Let(b.Temp(obj), New(s)));
        }, Error: "isn't a slot");

        yield return new("slot_given_two_objects", () =>
        {
            var s = b.Temp(obj, IrLocalKind.Slot);
            return b.Function("slot_given_two_objects", [], new IrDeclare(s) { Origin = null }, Let(b.Temp(obj), New(s)), Let(b.Temp(obj), New(s)));
        }, Error: "is given more than one object");

        yield return new("lambda_made_twice", () =>
        {
            var lambda = Lambda("lambda_made_twice", escaping: false);
            return b.Function("lambda_made_twice", [],
                Let(b.Temp(nonEscaping), new IrLambda(lambda, nonEscaping)),
                Let(b.Temp(nonEscaping), new IrLambda(lambda, nonEscaping)));
        }, Error: "is made twice");

        yield return new("closure_made_twice", () =>
        {
            var lambda = Lambda("closure_made_twice", escaping: true);
            return b.Function("closure_made_twice", [], Let(b.Temp(escaping), Closure(lambda)), Let(b.Temp(escaping), Closure(lambda)));
        }, Error: "is made twice");
    }

    static IrLet Let(IrLocal local, IrRvalue value) => new(local, value) { Origin = null };
    static IrDo Do(IrRvalue value) => new(value) { Origin = null };
    static IrReturn Return(IrOperand? value = null) => new(value) { Origin = null };
    static IrBlock Block(params IrStatement[] statements) => new(statements) { Origin = null };
}
