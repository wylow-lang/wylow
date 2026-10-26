using Vpl.Compiler.Analysis;
using Vpl.Compiler.Ir;

namespace Vpl.Compiler.Optimize;

/// <summary>
/// Lowering assumes any lambda that captures a writable local may change it. This clears that for
/// the locals no lambda's body writes, from the same effects every other analysis asks, so that
/// calls after the lambda is made needn't be treated as writing them. Runs first, since hold
/// elision and the passes after it ask <see cref="IrLocal.IsChangedByLambda"/>.
/// </summary>
public static class LambdaWrites
{
    public static void Apply(IrProgram program)
    {
        HashSet<IrLocal> written = [];
        foreach (var lambda in program.Functions.Where(f => f.IsLambda))
        {
            var own = Writes.OwnLocals(lambda);
            foreach (var statement in IrWalk.Descendants(lambda.Body, intoLambdas: false))
            {
                if (statement is IrIf or IrLoop)
                    continue;
                var effects = statement is IrBlock block ? IrEffects.Ending(block) : IrEffects.Of(statement);
                foreach (var write in effects.Writes.Where(w => !w.Place.IsDeref && !own.Contains(w.Place.Local)))
                    written.Add(write.Place.Local);
            }
        }
        foreach (var function in program.Functions)
        {
            foreach (var local in function.Parameters.Concat(function.Captures).Concat(Writes.OwnLocals(function)))
            {
                if (!written.Contains(local))
                    local.IsChangedByLambda = false;
            }
        }
    }
}
