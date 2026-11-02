using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;

// The forms the optimizations leave behind, which the verifier must accept: the pipeline verifies
// every pass's output, but only on the forms the tests reach, and a form it rejects makes the
// compiler throw after the pass. Each is beside the near-miss it must still reject.
static partial class IrSelfTests
{
    static IEnumerable<Case> PassOutputs(Builder b)
    {
        var tagged = b.Type("Tagged");
        var constructors = tagged.Symbol!.Constructors;

        // The body has a source location, which is what makes the end of the constructor check its fields.
        // HoldElision folds 'declare self; { store self = construct ... }' of a chained struct
        // constructor into 'let self = construct ...'.
        IrFunction Chained(string name, Func<IrLocal, IrLocal, IrStatement[]> start)
        {
            var self = b.Local("self", tagged);
            var a = b.Parameter("a", b.Type("Obj"));
            var function = new IrFunction(new FunctionSymbol(name, [], WylType.Void) { IsConstructor = true, ContainingType = tagged.Symbol }, name)
            {
                Struct = self,
                Body = new IrBlock([.. start(self, a), new IrReturn(IrRead.Copy(new IrPlace(self))) { Origin = null }]) { Origin = b.Loc.Source!.Value },
            };
            function.Parameters.Add(a);
            return function;
        }

        yield return new("pass_output_chained_struct_constructor_let", () => Chained("pass_output_chained_struct_constructor_let", (self, a) =>
            [new IrLet(self, new IrConstruct(tagged, new MethodReference(constructors[0], tagged, []), [IrRead.Copy(new IrPlace(a))])) { Origin = null }]));
        yield return new("chained_struct_constructor_store", () =>
        {
            var made = b.Temp(tagged);
            return Chained("chained_struct_constructor_store", (self, a) => [
                    new IrDeclare(self) { Origin = null },
                    new IrBlock([
                        new IrLet(made, new IrConstruct(tagged, new MethodReference(constructors[0], tagged, []), [IrRead.Copy(new IrPlace(a))])) { Origin = null },
                        new IrStore(new IrPlace(self), IrRead.Move(new IrPlace(made))) { Origin = null },
                    ]) { Origin = null },
                ]);
        });
        // Starting from the default value assigns no field, however the let is written.
        yield return new("chained_struct_constructor_let_default", () => Chained("chained_struct_constructor_let_default", (self, _) =>
            [new IrLet(self, new IrDefault(tagged)) { Origin = null }]), Error: "field 'a' must be assigned");
    }
}
