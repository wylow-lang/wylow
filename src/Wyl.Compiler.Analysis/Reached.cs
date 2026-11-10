using Wyl.Compiler.Ir;
using Wyl.Compiler.Semantics;
using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.Analysis;

/// <summary>
/// The lowered functions a program may run. Every function outside <c>std</c> counts, so that the
/// verifier reports the errors of a function that nothing calls. A function of <c>std</c> counts if
/// a function that counts may run it, or if code outside the IR may name it: a class's methods,
/// which its vtable and forwarders name, its <c>asReadOnlySpan</c>, which the cycle collector calls,
/// <c>programArguments</c>, which the emitted <c>main</c> calls, and the content constructor of
/// <c>Isolated</c>, which a conversion to an isolated box runs. Functions without a declaration,
/// such as static initializers and default makers, always count.
/// </summary>
internal static class Reached
{
    /// <summary>The functions that count, or null if a function that counts may run code the analysis can't follow.</summary>
    public static IReadOnlySet<IrFunction>? Find(IrProgram program)
    {
        var callees = new Callees(program);
        var reached = new HashSet<IrFunction>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<IrFunction>();
        void Reach(IrFunction function)
        {
            if (reached.Add(function))
                pending.Enqueue(function);
        }
        foreach (var function in program.Functions.Where(f => IsRoot(program.Bound.Stdlib, f)))
            Reach(function);
        while (pending.TryDequeue(out var function))
        {
            var runs = function.Parameters.SelectMany(p => IrEffects.Releasing(p).Runs).ToList();
            foreach (var statement in IrWalk.Descendants(function.Body, intoLambdas: false))
            {
                runs.AddRange(IrEffects.Itself(statement).Runs);
                // A lambda runs only if the code that makes it runs.
                if (statement is IrLet { Value: IrLambda lambda })
                    Reach(lambda.Function);
                else if (statement is IrLet { Value: IrClosure closure })
                    Reach(closure.Function);
            }
            for (int i = 0; i < runs.Count; i++)
            {
                if (runs[i] is IrRunFunctionValue)
                    continue;
                if (callees.Of(runs[i]) is not { } found)
                    return null;
                foreach (var callee in found.Functions)
                    Reach(callee.Function);
                if (found.RunsExtern && runs[i] is IrRunMethod { Method: var method })
                    runs.AddRange(callees.ExternRuns(method));
            }
        }
        return reached;
    }

    static bool IsRoot(StdlibSymbols stdlib, IrFunction function)
    {
        if (function.IsLambda)
            return false;
        var symbol = function.Symbol;
        if (symbol.Declaration is not { } declaration || !declaration.Location.Source.Module.Package.IsStd)
            return true;
        return symbol.ContainingType?.Kind is TypeKind.Class or TypeKind.Interface || symbol == stdlib.ProgramArguments || symbol == stdlib.IsolatedContentConstructor;
    }
}
