using Wyl.Compiler.Ir;

namespace Wyl.Compiler.Analysis;

public sealed class InitializerReach
{
    readonly HashSet<IrFunction>? functions;

    InitializerReach(HashSet<IrFunction>? functions) => this.functions = functions;

    public bool MayRun(IrFunction function) => functions is null || functions.Contains(function);

    internal static InitializerReach Find(Callees callees)
    {
        var reached = new HashSet<IrFunction>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<IrFunction>();
        bool known = true;

        void Reach(IrFunction function)
        {
            if (reached.Add(function))
                pending.Enqueue(function);
        }

        void Follow(IrRun run)
        {
            if (!known)
                return;
            if (callees.Of(run) is not { } targets)
            {
                known = false;
                return;
            }
            foreach (var callee in targets.Functions)
                Reach(callee.Function);
            if (targets.RunsExtern && run is IrRunMethod { Method: var method })
                foreach (var inner in callees.ExternRuns(method))
                    Follow(inner);
        }

        foreach (var statik in callees.Program.Statics)
            Reach(statik.Initializer);

        while (known && pending.TryDequeue(out var function))
        {
            foreach (var parameter in function.Parameters)
                foreach (var run in IrEffects.Releasing(parameter).Runs)
                    Follow(run);
            foreach (var statement in IrWalk.Descendants(function.Body, intoLambdas: false))
                foreach (var run in IrEffects.Itself(statement).Runs)
                    Follow(run);
        }

        return new InitializerReach(known ? reached : null);
    }
}
