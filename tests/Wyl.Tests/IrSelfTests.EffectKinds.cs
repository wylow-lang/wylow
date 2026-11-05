using System.Reflection;
using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;
using Wyl.Harness;

static partial class IrSelfTests
{
    /// <summary>
    /// One effect case per kind of statement and rvalue, from docs/design/ir.md: whether it runs
    /// code, may panic, and how many places it writes. The kinds are the visitors' methods, so a new
    /// instruction fails here until it has its case, and a case for a kind that is gone fails too.
    /// </summary>
    static List<TestResult> EffectKinds(Builder b)
    {
        var obj = b.Type("Obj");
        var holder = b.Type("Holder");
        var shape = b.Type("Shape");
        var fn = b.Function("call").ParameterType(1);
        var objectLocal = b.Parameter("o", obj);
        var integer = b.Parameter("i", WylType.Int);
        var flag = b.Parameter("flag", WylType.Bool);
        var callback = b.Parameter("g", fn);
        var holderLocal = b.Parameter("h", holder);
        var empty = new IrBlock([]) { Origin = null };
        var one = IrConstant.Int(1);
        var loop = new IrLoop { Origin = null };
        var lambda = new IrFunction(new FunctionSymbol("lambda", [], WylType.Void) { IsLambda = true }, "lambda") { Body = empty };
        var circle = shape.Symbol!.Variants.First(v => v.Name == "Circle");
        var array = WylType.Int.FixedArrayOf(1);

        // (runs code, may panic, places written)
        var cases = new Dictionary<string, (IrEffects Effects, bool Runs, bool Panics, int Writes)>
        {
            // Statements; a compound one does nothing beyond its parts.
            [nameof(IIrStatementVisitor<int>.Block)] = (IrEffects.Itself(empty), false, false, 0),
            [nameof(IIrStatementVisitor<int>.Let)] = (IrEffects.Of(new IrLet(b.Temp(WylType.Int), new IrUse(one)) { Origin = null }), false, false, 0),
            [nameof(IIrStatementVisitor<int>.Declare)] = (IrEffects.Of(new IrDeclare(b.Temp(WylType.Int)) { Origin = null }), false, false, 0),
            [nameof(IIrStatementVisitor<int>.Store)] = (IrEffects.Of(new IrStore(new IrPlace(integer), one) { Origin = null }), false, false, 1),
            [nameof(IIrStatementVisitor<int>.Do)] = (IrEffects.Of(new IrDo(b.Call("f")) { Origin = null }), true, true, 0),
            [nameof(IIrStatementVisitor<int>.Check)] = (IrEffects.Of(new IrCheck(IrCheckKind.NotNull, [IrRead.Copy(new IrPlace(objectLocal))], b.Loc) { Origin = null }), false, true, 0),
            [nameof(IIrStatementVisitor<int>.If)] = (IrEffects.Itself(new IrIf(IrRead.Copy(new IrPlace(flag)), empty) { Origin = null }), false, false, 0),
            [nameof(IIrStatementVisitor<int>.Loop)] = (IrEffects.Itself(loop), false, false, 0),
            [nameof(IIrStatementVisitor<int>.Break)] = (IrEffects.Of(new IrBreak(loop) { Origin = null }), false, false, 0),
            [nameof(IIrStatementVisitor<int>.Continue)] = (IrEffects.Of(new IrContinue(loop) { Origin = null }), false, false, 0),
            [nameof(IIrStatementVisitor<int>.Return)] = (IrEffects.Of(new IrReturn() { Origin = null }), false, false, 0),
            [nameof(IIrStatementVisitor<int>.End)] = (IrEffects.Of(new IrEnd(b.Local("e", obj)) { Origin = null }), true, false, 0),
            [nameof(IIrStatementVisitor<int>.Discard)] = (IrEffects.Of(new IrDiscard(b.Local("d", obj)) { Origin = null }), false, false, 1),
            [nameof(IIrStatementVisitor<int>.Unreachable)] = (IrEffects.Of(new IrUnreachable(b.Loc) { Origin = null }), false, true, 0),

            // Rvalues.
            [nameof(IIrRvalueVisitor<int>.Use)] = (IrEffects.Of(new IrUse(one)), false, false, 0),
            [nameof(IIrRvalueVisitor<int>.Unary)] = (IrEffects.Of(new IrUnary(BoundUnaryOperator.Negate, one, b.Loc)), false, true, 0),
            [nameof(IIrRvalueVisitor<int>.Binary)] = (IrEffects.Of(new IrBinary(BoundBinaryOperator.Add, one, one, WylType.Int, b.Loc)), false, true, 0),
            [nameof(IIrRvalueVisitor<int>.Call)] = (IrEffects.Of(b.Call("f")), true, true, 0),
            [nameof(IIrRvalueVisitor<int>.Invoke)] = (IrEffects.Of(new IrInvoke(IrRead.Copy(new IrPlace(callback)), b.Function("use"), [])), true, true, 0),
            [nameof(IIrRvalueVisitor<int>.New)] = (IrEffects.Of(new IrNew(obj, b.Constructor("Obj"), []) { Origin = b.Loc.Source!.Value }), true, true, 0),
            [nameof(IIrRvalueVisitor<int>.Construct)] = (IrEffects.Of(new IrConstruct(b.Type("Built"), b.Constructor("Built"), [])), true, true, 0),
            [nameof(IIrRvalueVisitor<int>.Variant)] = (IrEffects.Of(new IrVariant(shape, circle, [one])), false, false, 0),
            [nameof(IIrRvalueVisitor<int>.Array)] = (IrEffects.Of(new IrArray(array, [one])), false, false, 0),
            [nameof(IIrRvalueVisitor<int>.Default)] = (IrEffects.Of(new IrDefault(WylType.Int)), false, false, 0),
            [nameof(IIrRvalueVisitor<int>.Zeroed)] = (IrEffects.Of(new IrZeroed(b.Type("Vec"))), false, false, 0),
            [nameof(IIrRvalueVisitor<int>.Closure)] = (IrEffects.Of(new IrClosure(lambda, [], fn) { Name = "closure", Origin = b.Loc.Source!.Value }), false, false, 0),
            [nameof(IIrRvalueVisitor<int>.Lambda)] = (IrEffects.Of(new IrLambda(lambda, fn)), false, false, 0),
            [nameof(IIrRvalueVisitor<int>.Choose)] = (IrEffects.Of(new IrChoose(one, [one, one], WylType.Int)), false, false, 0),
            [nameof(IIrRvalueVisitor<int>.Ref)] = (IrEffects.Of(new IrRef(RefKind.Ref, new IrPlace(integer))), false, false, 0),
            [nameof(IIrRvalueVisitor<int>.Addr)] = (IrEffects.Of(new IrAddr(RefKind.Ref, new IrPlace(integer))), false, false, 0),
            [nameof(IIrRvalueVisitor<int>.Data)] = (IrEffects.Of(new IrData(new IrPlace(b.Local("a", array)))), false, false, 0),
            [nameof(IIrRvalueVisitor<int>.Test)] = (IrEffects.Of(new IrTest(IrTestKind.NotNull, IrRead.Copy(new IrPlace(objectLocal)))), false, false, 0),
            [nameof(IIrRvalueVisitor<int>.Convert)] = (IrEffects.Of(new IrConvert(WylType.Char, one, b.Loc)), false, true, 0),
            [nameof(IIrRvalueVisitor<int>.Hold)] = (IrEffects.Of(new IrHold(new IrPlace(holderLocal), borrows: false)), false, false, 0),
            [nameof(IIrRvalueVisitor<int>.InUse)] = (IrEffects.Of(new IrInUse(new IrPlace(holderLocal))), false, false, 0),
        };

        var kinds = new[] { typeof(IIrStatementVisitor<>), typeof(IIrRvalueVisitor<>) }
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)).Select(m => m.Name).ToHashSet();
        List<TestResult> results =
        [
            new("ir/effects_kinds/coverage", kinds.Except(cases.Keys).Concat(cases.Keys.Except(kinds)).ToList() is { Count: > 0 } off
                ? $"effect cases and instruction kinds differ: {string.Join(", ", off)}" : null),
        ];
        foreach (var (kind, (effects, runs, panics, writes)) in cases)
        {
            var wrong = effects.RunsCode != runs ? $"runs code is {effects.RunsCode}"
                : effects.MayPanic != panics ? $"may panic is {effects.MayPanic}"
                : effects.Writes.Count != writes ? $"writes {effects.Writes.Count} places"
                : null;
            results.Add(new($"ir/effects_kinds/{kind}", wrong));
        }
        return results;
    }
}
