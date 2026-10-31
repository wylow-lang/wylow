using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

static partial class IrSelfTests
{
    /// <summary>
    /// The effect facts the verifier, the passes, and the emitter all decide by. A program test sees
    /// few of them: an instruction that wrongly says it may panic, a release that wrongly says it
    /// runs code, or a call that wrongly says it runs nothing changes behavior only in a case no
    /// test reaches, and every mode agrees on the wrong answer. Mutation testing found these; this
    /// pins them.
    /// </summary>
    static TestResult EffectFacts(Builder b)
    {
        List<string> problems = [];
        void Expect(bool condition, string problem)
        {
            if (!condition)
                problems.Add(problem);
        }
        void Fact(IrEffects effects, bool runs, bool panics, string what)
        {
            if (effects.RunsCode != runs)
                problems.Add($"{what}: runs code is {effects.RunsCode}, expected {runs}");
            if (effects.MayPanic != panics)
                problems.Add($"{what}: may panic is {effects.MayPanic}, expected {panics}");
        }
        static bool Runs(IrEffects effects, IrRun run) =>
            effects.Runs.Count == 1 && effects.Runs[0] == run;

        var obj = b.Type("Obj");
        var holder = b.Type("Holder");
        var counter = b.Type("Counter");
        var vec = b.Type("Vec");
        var x = b.Field("Obj", "x");
        var field = b.Field("Holder", "b");

        // A value that does nothing says so, and a release says what it runs.
        Expect(IrEffects.None.Writes.Count == 0 && !IrEffects.None.RunsCode && !IrEffects.None.MayPanic, "None does nothing");
        Fact(IrEffects.Releasing(obj), runs: true, panics: false, "releasing an object");
        // The fact names the release; whether it runs code is the local's question below.
        Fact(IrEffects.Releasing(WylType.Int), runs: true, panics: false, "releasing a type lists its release");

        var hold = b.Local("h", obj, IrLocalKind.Hold);
        var slot = b.Local("s", obj, IrLocalKind.Slot);
        var value = b.Local("v", obj);
        var integer = b.Local("i", WylType.Int);
        var reference = b.Local("r", WylType.Int, IrLocalKind.Reference, RefKind.Ref);
        var borrowed = b.Local("bl", WylType.Int, IrLocalKind.Borrowed);
        Expect(IrEffects.Releasing(hold).RunsCode, "a hold's release runs its end");
        Expect(IrEffects.Releasing(slot).RunsCode, "a slot's release runs its end");
        Expect(IrEffects.Releasing(value).RunsCode, "an object's release runs its destructor");
        Expect(!IrEffects.Releasing(integer).RunsCode, "an int's release runs nothing");
        Expect(!IrEffects.Releasing(reference).RunsCode, "a reference's release runs nothing");
        Expect(!IrEffects.Releasing(borrowed).RunsCode, "a borrowed local's release runs nothing");

        // A block releases what it declares as it ends, except the local that is already ended.
        var holderArgument = b.Parameter("hd", holder);
        var block = new IrBlock([
            new IrLet(b.Temp(obj, IrLocalKind.Hold), new IrHold(new IrPlace(holderArgument), borrows: false)) { Origin = null },
            new IrLet(b.Temp(WylType.Int), new IrUse(IrConstant.Int(1))) { Origin = null },
        ]) { Origin = null };
        var declared = IrWalk.Declared(block).ToList();
        Fact(IrEffects.Ending(block), runs: true, panics: false, "ending a block with an owned hold");
        Expect(!IrEffects.Ending(block, declared[0]).RunsCode, "ending a block except the hold runs nothing");
        Expect(IrEffects.Ending(block, declared[1]).RunsCode, "ending a block except the int still runs the hold's end");
        Expect(!IrEffects.Itself(new IrIf(IrConstant.Bool(true), new IrBlock { Origin = null }) { Origin = null }).RunsCode,
            "an if itself does nothing");

        // What a statement does.
        var objectArgument = b.Parameter("o", obj);
        Fact(IrEffects.Of(new IrCheck(IrCheckKind.NotNull, [IrRead.Copy(new IrPlace(objectArgument))], b.Loc) { Origin = null }),
            runs: false, panics: true, "a not-null check");
        Fact(IrEffects.Of(new IrCheck(IrCheckKind.Narrowed, [IrRead.Copy(new IrPlace(objectArgument))], null) { Origin = null }),
            runs: false, panics: false, "a narrowed check");
        Expect(IrEffects.Of(new IrUnreachable(b.Loc) { Origin = null }).MayPanic, "reaching unreachable may panic");
        Expect(IrEffects.Of(new IrEnd(hold) { Origin = null }).RunsCode, "ending a hold runs its end");
        Expect(IrEffects.Of(new IrDo(b.Call("f")) { Origin = null }).RunsCode, "doing a call runs it");
        Fact(IrEffects.Of(new IrDeclare(b.Temp(WylType.Int)) { Origin = null }), runs: false, panics: false, "a declare");
        Fact(IrEffects.Of(new IrReturn(IrConstant.Int(1)) { Origin = null }), runs: false, panics: false, "a return");

        var objectStore = new IrStore(IrPlace.Deref(holderArgument).Field(field), IrRead.Copy(new IrPlace(objectArgument))) { Origin = null };
        var stored = IrEffects.Of(objectStore);
        Expect(stored.Writes.Count == 1, "a store writes one place");
        Fact(stored, runs: true, panics: false, "storing an object");
        Fact(IrEffects.Of(new IrStore(IrPlace.Deref(objectArgument).Field(x), IrConstant.Int(1)) { Origin = null }),
            runs: false, panics: false, "storing an int");

        // Moving out of a named local writes it, unless the caller asks for what a statement does alone.
        var moved = b.Local("m", obj);
        var move = new IrDo(new IrUse(IrRead.Move(new IrPlace(moved)))) { Origin = null };
        Expect(IrEffects.Of(move).Writes.Any(write => write.Place.Local == moved), "a move out of a named local writes it");
        Expect(IrEffects.Of(move, moves: false).Writes.Count == 0, "a move is not a write when moves are left out");
        Expect(IrEffects.Of(new IrDo(new IrUse(IrRead.Copy(new IrPlace(moved)))) { Origin = null }).Writes.Count == 0,
            "a copy of a named local writes nothing");

        // What a call runs and whether it may panic.
        var call = IrEffects.Of(b.Call("f"));
        Expect(call.Runs.Count == 1 && call.MayPanic, "calling a function that isn't pure runs it and may panic");
        var pure = new FunctionSymbol("pure", [], WylType.Void) { IsPure = true };
        Fact(IrEffects.Of(new IrCall(new MethodReference(pure, null, []), null, [])), runs: false, panics: false, "a pure call");
        var pureHere = new FunctionSymbol("pureHere", [], WylType.Void) { IsPure = true, PassesLocation = true };
        Fact(IrEffects.Of(new IrCall(new MethodReference(pureHere, null, []), null, [], b.Loc)),
            runs: false, panics: true, "a pure call that passes a location");

        var constructor = b.Constructor("Obj");
        Expect(Runs(IrEffects.Of(new IrNew(obj, constructor, []) { Origin = b.Loc.Source!.Value }), new IrRunMethod(constructor, Direct: true)),
            "making an object runs its constructor directly");

        // A struct's implicit constructor makes the default value, which may run field initializers;
        // a memberwise one runs the constructor itself.
        var built = b.Type("Built");
        var builtImplicit = new MethodReference(built.Symbol!.DefaultConstructor, built, []);
        var builtMemberwise = b.Constructor("Built");
        Expect(built.DefaultRunsCode, "Built's default runs its field initializer");
        Expect(Runs(IrEffects.Of(new IrConstruct(built, builtImplicit, [])), new IrRunDefault(built)),
            "an implicit constructor of a struct with a code-running initializer runs the default");
        Expect(Runs(IrEffects.Of(new IrConstruct(built, builtMemberwise, [])), new IrRunMethod(builtMemberwise, Direct: true)),
            "a memberwise constructor runs the constructor");
        Expect(!IrEffects.Of(new IrConstruct(vec, new MethodReference(vec.Symbol!.DefaultConstructor, vec, []), [])).RunsCode,
            "an implicit constructor of a struct with no initializer runs nothing");

        // An operation may panic exactly when it has a location.
        Fact(IrEffects.Of(new IrUnary(BoundUnaryOperator.Negate, IrConstant.Int(1), b.Loc)), runs: false, panics: true, "negating an int");
        Fact(IrEffects.Of(new IrUnary(BoundUnaryOperator.LogicalNot, IrConstant.Bool(true), b.Loc)), runs: false, panics: false, "not of a bool");
        Fact(IrEffects.Of(new IrBinary(BoundBinaryOperator.Add, IrConstant.Int(1), IrConstant.Int(2), WylType.Int, b.Loc)),
            runs: false, panics: true, "adding ints");
        Fact(IrEffects.Of(new IrBinary(BoundBinaryOperator.Less, IrConstant.Int(1), IrConstant.Int(2), WylType.Bool, b.Loc)),
            runs: false, panics: false, "comparing ints");
        Fact(IrEffects.Of(new IrConvert(WylType.Char, IrConstant.Int(1), b.Loc)), runs: false, panics: true, "an int to char");
        Fact(IrEffects.Of(new IrConvert(WylType.Long, IrConstant.Int(1), b.Loc)), runs: false, panics: false, "an int to long");

        // Holding borrows only when it says so, and a default runs code only when the type has some.
        Fact(IrEffects.Of(new IrHold(new IrPlace(objectArgument), borrows: true)), runs: true, panics: false, "a borrowing hold");
        Fact(IrEffects.Of(new IrHold(new IrPlace(objectArgument), borrows: false)), runs: false, panics: false, "a plain hold");
        Fact(IrEffects.Of(new IrDefault(built)), runs: true, panics: true, "the default of a struct with an initializer");
        Fact(IrEffects.Of(new IrDefault(WylType.Int)), runs: false, panics: false, "the default of an int");
        Fact(IrEffects.Of(new IrDefault(counter)), runs: false, panics: false, "the default of a struct whose initializer is a literal");

        return new("ir/effects", problems.Count == 0 ? null : string.Join('\n', problems.Take(20)));
    }
}
