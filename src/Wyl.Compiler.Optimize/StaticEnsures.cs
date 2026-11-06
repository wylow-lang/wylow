using Wyl.Compiler.Analysis;
using Wyl.Compiler.Ir;

namespace Wyl.Compiler.Optimize;

static class StaticEnsures
{
    public static void Apply(IrProgram program, InitializerReach reach)
    {
        foreach (var function in program.Functions)
        {
            if (reach.MayRun(function))
                continue;
            foreach (var block in IrWalk.Descendants(function.Body, intoLambdas: false).OfType<IrBlock>().ToList())
                block.Statements.RemoveAll(statement => statement is IrDo { Value: IrCall call } && program.StaticOf(call.Method.Symbol) is not null);
        }
    }
}
