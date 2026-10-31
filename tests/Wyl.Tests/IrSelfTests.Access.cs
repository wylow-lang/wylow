using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

static partial class IrSelfTests
{
    /// <summary>A place path's value is what it decomposes: its last step, or its root with no steps.</summary>
    static TestResult PlacePathFacts()
    {
        var source = new SourceText("path_self_test.wyl", "void main() { }");
        var location = new Location(source, new TextSpan(0, 0));
        var root = new BoundLiteral(location, WylType.Int, 1);
        var step = new BoundLiteral(location, WylType.Int, 2);
        var withStep = new PlacePath(PlaceBase.Local, root, [step], []);
        var withoutStep = new PlacePath(PlaceBase.Local, root, [], []);
        return new("ir/place_path", withStep.Value == step && withoutStep.Value == root
            ? null : "a path's value is its last step, or its root when it has none");
    }

    /// <summary>
    /// The storage facts the verifier, the passes, and nesting decide by: what a place touches,
    /// which accesses may be the same storage, and whether two instructions may run in either
    /// order. A wrong answer here is a miscompile no mode disagrees with, and mutation testing
    /// found branches no program test reaches; this pins them.
    /// </summary>
    static TestResult AccessFacts(Builder b)
    {
        List<string> problems = [];
        void Expect(bool condition, string problem)
        {
            if (!condition)
                problems.Add(problem);
        }

        var obj = b.Type("Obj");
        var holder = b.Type("Holder");
        var boxed = b.Type("Boxed");
        var vec = b.Type("Vec");
        var x = b.Field("Obj", "x");
        var fixedField = b.Field("Holder", "fixed");
        var pair = b.Field("Holder", "pair");
        var item = b.Field("Boxed", "item");
        var nestField = b.Field("Nest", "inner");
        var holderB = b.Field("Holder", "b");
        var objLocal = b.Parameter("obj", obj);
        var holderLocal = b.Parameter("holder", holder);
        var otherHolderLocal = b.Parameter("other", holder);
        var intArray = WylType.Int.FixedArrayOf(2);

        IrAccess At(IrStorage storage, IrLocal local, FieldSymbol? field, IReadOnlyList<IrProjection> path, WylType? type) =>
            new(storage, local, field, path, type);

        try
        {
            // A deref place reads the local that refers and the storage it refers to.
            var deref = IrPlace.Deref(objLocal);
            var derefAccesses = IrAccess.Of(deref).ToList();
            Expect(derefAccesses.Count == 2, $"a deref place touches two stores, not {derefAccesses.Count}");
            Expect(derefAccesses[0].Equals(At(IrStorage.Frame, objLocal, null, [], obj)), "a deref place reads its local");
            Expect(derefAccesses[1].Storage == IrStorage.Field, "a deref of an object is field storage");

            // Finding a place reads the paths to it, not what is in it.
            var found = IrAccess.Finding(IrPlace.Deref(objLocal).Field(x)).ToList();
            Expect(found.Count == 1 && found[0].Storage == IrStorage.Frame, "finding a field reads only the local it is in");

            // A deref's first field names the field and starts its path; any other projection stays in the path.
            var fieldTarget = IrAccess.Target(IrPlace.Deref(objLocal).Field(x));
            Expect(fieldTarget.Field == x && fieldTarget.Path.Count == 0 && fieldTarget.Type == WylType.Int,
                "a deref's first field names the field, and its type is the field's");
            var elementTarget = IrAccess.Target(IrPlace.Deref(objLocal).Element(IrConstant.Int(0)));
            Expect(elementTarget.Field is null && elementTarget.Path.Count == 1 && elementTarget.Path[0] is IrElement,
                "a projection that isn't a field stays in the path");
            Expect(elementTarget.Type == obj, "without a first field, a deref's type is the object's");

            // A summary of a frame local is storage of the type where its path ends.
            var frame = At(IrStorage.Frame, b.Local("local", holder), null, [new IrField(holderB, obj)], holder);
            Expect(frame.Summarized(TypeMap.Empty).Type == obj, "a summary takes the type at the end of a frame local's path");

            // Places of the same kind overlap only by the same storage.
            var frameA = At(IrStorage.Frame, holderLocal, null, [], holder);
            var frameOther = At(IrStorage.Frame, otherHolderLocal, null, [], holder);
            Expect(!frameA.Overlaps(frameOther), "different locals are different storage");
            Expect(frameA.Overlaps(At(IrStorage.Frame, holderLocal, null, [], holder)), "a local overlaps itself");

            var fieldX = At(IrStorage.Field, holderLocal, x, [], WylType.Int);
            var fieldFixed = At(IrStorage.Field, holderLocal, fixedField, [], obj);
            Expect(!fieldX.Overlaps(fieldFixed), "different fields are different storage");
            Expect(!fieldFixed.Overlaps(fieldX), "different fields are different storage, either way");
            Expect(At(IrStorage.Field, holderLocal, null, [], WylType.Int).Overlaps(fieldX), "an object's unknown field may be one of its fields");
            Expect(fieldFixed.Overlaps(At(IrStorage.Field, holderLocal, null, [], WylType.Int)), "an object's unknown field may be one of its fields, either way");
            Expect(!frameA.Overlaps(fieldX), "a local is not a field");
            Expect(!fieldX.Overlaps(frameA), "a field is not a local");
            var pathX = At(IrStorage.Field, holderLocal, x, [new IrField(x, WylType.Int)], WylType.Int);
            var pathFixed = At(IrStorage.Field, holderLocal, x, [new IrField(fixedField, obj)], WylType.Int);
            Expect(!pathX.Overlaps(pathFixed), "the same field's different inner paths don't overlap");

            // Paths overlap when a step is the same field, element, or assumed value.
            IrAccess FramePath(IrProjection step) => At(IrStorage.Frame, holderLocal, null, [step], holder);
            var stepX = new IrField(x, WylType.Int);
            var stepOther = new IrField(fixedField, WylType.Int);
            Expect(FramePath(stepX).Overlaps(FramePath(new IrField(x, WylType.Int))), "the same field step overlaps");
            Expect(!FramePath(stepX).Overlaps(FramePath(stepOther)), "different field steps don't overlap");
            Expect(!FramePath(stepX).Overlaps(FramePath(new IrAssumed(WylType.Int))), "an assumed step is not a field step");
            var shape = b.Type("Shape").Symbol!;
            var circle = shape.FindVariant("Circle")!;
            var circleField = circle.Fields[0];
            Expect(FramePath(new IrVariantField(circle, circleField, WylType.Int))
                    .Overlaps(FramePath(new IrVariantField(circle, circleField, WylType.Int))),
                "the same variant field step overlaps");

            // Equality describes the same storage the same way, and a write set is deduplicated by it.
            var equalA = At(IrStorage.Field, holderLocal, x, [stepX], WylType.Int);
            var equalB = At(IrStorage.Field, holderLocal, x, [new IrField(x, WylType.Int)], WylType.Int);
            Expect(equalA.Equals(equalB), "equal accesses are equal");
            Expect(!equalA.Equals(At(IrStorage.Frame, holderLocal, x, [stepX], WylType.Int)), "storage is part of equality");
            Expect(!equalA.Equals(At(IrStorage.Field, otherHolderLocal, x, [stepX], WylType.Int)), "the local is part of equality");
            Expect(!equalA.Equals(At(IrStorage.Field, holderLocal, fixedField, [stepX], WylType.Int)), "the field is part of equality");
            Expect(!equalA.Equals(At(IrStorage.Field, holderLocal, x, [stepX], obj)), "the type is part of equality");
            Expect(!equalA.Equals(At(IrStorage.Field, holderLocal, x, [], WylType.Int)), "the path length is part of equality");
            Expect(!equalA.Equals(At(IrStorage.Field, holderLocal, x, [new IrField(x, obj)], WylType.Int)), "a step's type is part of equality");
            Expect(!equalA.Equals(At(IrStorage.Field, holderLocal, x, [new IrAssumed(WylType.Int)], WylType.Int)), "a step's kind is part of equality");
            Expect(!equalA.Equals(At(IrStorage.Field, holderLocal, x, [stepOther], WylType.Int)), "a step's field is part of equality");
            Expect(new HashSet<IrAccess> { equalA, equalB }.Count == 1, "equal accesses are one entry of a write set");
            IrAccess? noAccess = null;
            Expect(!equalA.Equals(noAccess), "an access isn't equal to null");

            // Storage a function can't see reaches a place by type, or by the types inside it.
            Expect(IrAccess.UnknownOf(obj).Overlaps(At(IrStorage.Field, holderLocal, fixedField, [], obj)),
                "an unseen value may be a field of its type");
            Expect(IrAccess.Unknown.Overlaps(At(IrStorage.Field, holderLocal, fixedField, [], obj)),
                "storage of any type may be a field");
            Expect(IrAccess.UnknownOf(obj).Overlaps(At(IrStorage.Field, holderLocal, fixedField, [], null)),
                "an unseen value may be storage of any type");
            Expect(IrAccess.UnknownOf(obj).Overlaps(IrAccess.UnknownOf(boxed)), "unseen storage may be inside storage of a containing type");
            Expect(IrAccess.UnknownOf(obj).Overlaps(At(IrStorage.Field, holderLocal, item, [], boxed)),
                "an unseen value may be a field of a field, without being an array element");
            Expect(!IrAccess.Element(vec).Overlaps(At(IrStorage.Field, holderLocal, nestField, [new IrAssumed(vec)], vec)),
                "an array element is not a field");
            Expect(!IrAccess.Element(obj).Overlaps(At(IrStorage.Field, holderLocal, item, [], boxed)),
                "an array element is not a field inside a value");
            Expect(IrAccess.UnknownOf(WylType.Int).Overlaps(At(IrStorage.Field, holderLocal, holderB, [new IrField(x, WylType.Int)], obj)),
                "unseen storage may be a field of a field");
            Expect(IrAccess.Element(WylType.Int).Overlaps(At(IrStorage.Field, holderLocal, pair, [], intArray)),
                "an array element may be an element of a field's array");

            // Only a mut field, storage code can't see, or a local a lambda assigns is written by code.
            NoChanges none = new();
            Expect(!fieldFixed.WrittenByCode(none), "a non-mut field isn't written by code");
            Expect(fieldX.WrittenByCode(none), "a mut field may be written by code");
            Expect(At(IrStorage.Field, holderLocal, null, [], obj).WrittenByCode(none), "an unknown field may be written by code");

            // A footprint's own code and reads, and what adding another footprint keeps.
            Expect(!new IrFootprint().RunsCode, "an empty footprint runs no code");
            var merged = new IrFootprint();
            merged.Add(new IrFootprint { Reads = { fieldX } });
            Expect(merged.Reads.Count == 1 && merged.Reads[0].Equals(fieldX), "adding a footprint adds its reads");
            var panics = new IrFootprint();
            panics.Add(new IrFootprint { MayPanic = true });
            Expect(panics.MayPanic, "adding a footprint keeps that it may panic");
            var owners = new IrFootprint();
            owners.Add(new IrFootprint { CreatesOwner = true });
            Expect(owners.CreatesOwner, "adding a footprint keeps that it creates an owner");
            var effects = new IrFootprint();
            effects.Add(IrEffects.Of(new IrUnary(BoundUnaryOperator.Negate, IrConstant.Int(1), b.Loc)));
            Expect(effects.MayPanic, "adding effects keeps that they may panic");

            // Two instructions conflict exactly where running them in either order can differ.
            IrFootprint Foot(bool runs = false, bool panics = false, bool owner = false, IrAccess? read = null, IrAccess? write = null)
            {
                var footprint = new IrFootprint { MayPanic = panics, CreatesOwner = owner };
                if (runs)
                    footprint.Runs.Add(new IrRunRelease(null));
                if (read is not null)
                    footprint.Reads.Add(read);
                if (write is not null)
                    footprint.Writes.Add(write);
                return footprint;
            }
            Expect(Foot(runs: true).ConflictsWith(Foot(runs: true), none), "two pieces of code that run code conflict");
            Expect(!Foot(runs: true).ConflictsWith(Foot(), none), "code that runs and code that does nothing don't conflict");
            Expect(!Foot(panics: true).ConflictsWith(Foot(), none), "a possible panic and nothing don't conflict");
            Expect(!Foot().ConflictsWith(Foot(panics: true), none), "nothing and a possible panic don't conflict");
            Expect(Foot(panics: true).ConflictsWith(Foot(runs: true), none), "a panic before code that runs is observable");
            Expect(Foot(runs: true).ConflictsWith(Foot(panics: true), none), "code that runs before a panic is observable");
            Expect(Foot(owner: true).ConflictsWith(Foot(owner: true), none), "two constructed owners have an order");
            Expect(!Foot(owner: true).ConflictsWith(Foot(), none), "one owner and nothing don't conflict");
            var writeFrame = At(IrStorage.Frame, otherHolderLocal, null, [], holder);
            Expect(Foot(write: writeFrame).ConflictsWith(Foot(read: writeFrame), none), "a write to what another reads conflicts");
            Expect(Foot(read: writeFrame).ConflictsWith(Foot(write: writeFrame), none), "a read and a write of it conflict, either way");
            Expect(!Foot(write: writeFrame).ConflictsWith(Foot(read: fieldX), none), "a write that doesn't overlap doesn't conflict");
            Expect(Foot(runs: true).ConflictsWith(Foot(read: fieldX), none), "code that runs conflicts with a read of a field it may write");

            // A footprint is a statement's own work, not the bodies of the lambdas it makes.
            var lambda = new IrFunction(new FunctionSymbol("lambda", [], WylType.Void) { IsLambda = true }, "lambda")
            {
                Body = new IrBlock([new IrDo(b.Call("f")) { Origin = null }]) { Origin = null },
            };
            var functionType = b.Function("call").ParameterType(1);
            var makesLambda = new IrLet(b.Temp(functionType), new IrLambda(lambda, functionType)) { Origin = null };
            Expect(!IrWalk.Footprint(makesLambda).RunsCode, "a footprint leaves out the body of a lambda it makes");

            var read = new IrDo(new IrUse(IrRead.Copy(new IrPlace(holderLocal)))) { Origin = null };
            Expect(IrWalk.Footprint(read).Reads.Count == 1, "a footprint adds what a statement reads");
        }
        catch (Exception e)
        {
            problems.Add($"exception: {e.GetType().Name}: {e.Message}");
        }

        return new("ir/access", problems.Count == 0 ? null : string.Join('\n', problems.Take(20)));
    }

    sealed class NoChanges : IIrLambdaChanges
    {
        public bool MayChange(IrLocal local) => false;
    }
}
