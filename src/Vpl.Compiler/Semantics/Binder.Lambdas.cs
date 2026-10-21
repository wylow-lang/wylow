using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

public sealed partial class Binder
{
    /// <summary>
    /// Until escaping functions exist (step 5 of docs/handoff/lambdas.md), only parameters and
    /// locals hold functions, which a lambda's captures outlive since it can't leave the call.
    /// </summary>
    bool CheckNotStored(VplType type, Location location, string what)
    {
        if (!type.NonNull.IsFunction)
            return true;
        Error(location, $"a function cannot be {what} yet; until escaping functions are implemented, only parameters and local variables can hold one");
        return false;
    }

    VplType BindFunctionType(TypeSyntax syntax)
    {
        var parameters = new List<FunctionParameterType>();
        bool failed = false;
        foreach (var parameter in syntax.Parameters)
        {
            var type = BindType(parameter.Type, allowVoid: false);
            failed |= type == VplType.Error || !CheckParameterMode(parameter.RefKind, type, parameter.Type.Location);
            parameters.Add(new FunctionParameterType(parameter.RefKind, type));
        }
        var returnType = BindType(syntax.Element!, allowVoid: true);
        if (returnType.IsRefStruct)
        {
            Error(syntax.Element!.Location, $"a function cannot return a '{returnType}', since there is nothing it could view");
            return VplType.Error;
        }
        if (failed || returnType == VplType.Error || !CheckNotStored(returnType, syntax.Element!.Location, "returned"))
            return VplType.Error;
        return returnType.FunctionReturning(parameters);
    }

    bool CheckParameterMode(RefKind refKind, VplType type, Location location)
    {
        if (refKind == RefKind.None || !(type.IsReference || type.IsFunction))
            return true;
        Error(location, type.IsFunction
            ? $"'{FunctionSymbol.RefPrefix(refKind).Trim()}' cannot be used on a parameter of type '{type}'; functions are already passed without copying"
            : $"'{FunctionSymbol.RefPrefix(refKind).Trim()}' cannot be used on a parameter of type '{type}'; class objects are already passed by reference");
        return false;
    }

    /// <summary>The parameter types a lambda declares, or null if it leaves them to its target.</summary>
    IReadOnlyList<FunctionParameterType>? BindDeclaredParameters(LambdaExpr e)
    {
        if (e.Parameters.Count == 0 || e.Parameters[0].Type is null)
            return null;
        return [.. e.Parameters.Select(p =>
        {
            var type = BindType(p.Type!, allowVoid: false);
            return new FunctionParameterType(p.RefKind, CheckParameterMode(p.RefKind, type, p.Location) ? type : VplType.Error);
        })];
    }

    static bool LambdaFits(BoundPendingLambda lambda, VplType to) =>
        to.NonNull.IsFunction
        && to.NonNull.FunctionParameters.Count == lambda.Syntax.Parameters.Count
        && (lambda.DeclaredParameters is null
            || lambda.DeclaredParameters.Zip(to.NonNull.FunctionParameters).All(p => p.First.Type == VplType.Error || p.First == p.Second));

    BoundExpression BindLambda(LambdaExpr e, VplType type, IReadOnlyList<FunctionParameterType>? declared)
    {
        var expected = type.FunctionParameters;
        if (e.Parameters.Count != expected.Count)
        {
            return ErrorExpression(e.Location,
                $"this lambda has {Count(e.Parameters.Count, "parameter")}, but '{type}' takes {Count(expected.Count, "argument")}");
        }

        var function = LambdaFunction(e, type, declared ?? BindDeclaredParameters(e), type.FunctionReturnType!);
        var body = e.Body ?? new BlockStmt(e.Value!.Location, [function.ReturnType == VplType.Void
            ? new ExprStmt(e.Value.Location, e.Value)
            : new ReturnStmt(e.Value.Location, e.Value)]);
        return new BoundLambda(e.Location, type, function, BindLambdaBody(e, function, () => BindBlock(body)));
    }

    FunctionSymbol LambdaFunction(LambdaExpr e, VplType type, IReadOnlyList<FunctionParameterType>? declared, VplType returnType)
    {
        var expected = type.FunctionParameters;
        var parameters = new List<LocalSymbol>();
        for (int i = 0; i < expected.Count; i++)
        {
            var syntax = e.Parameters[i];
            if (declared is not null && declared[i] != expected[i] && declared[i].Type != VplType.Error)
            {
                Error(syntax.Location, $"'{syntax.Name}' is declared as '{FunctionSymbol.RefPrefix(declared[i].RefKind)}{declared[i].Type}', "
                    + $"but '{type}' passes '{FunctionSymbol.RefPrefix(expected[i].RefKind)}{expected[i].Type}'");
            }
            parameters.Add(new LocalSymbol(syntax.Name, expected[i].Type, isParameter: true, expected[i].RefKind));
        }
        return new FunctionSymbol("lambda", parameters, returnType)
        {
            IsLambda = true,
            IsUnsafe = InUnsafe,
            IsReadOnly = currentFunction?.IsReadOnly == true,
        };
    }

    BoundBlock BindLambdaBody(LambdaExpr e, FunctionSymbol function, Func<BoundBlock> bind)
    {
        // `break` and `continue` cannot leave the lambda, and `return` returns from it.
        var (enclosingFunction, enclosingLoops) = (currentFunction, loopDepth);
        currentFunction = function;
        loopDepth = 0;
        scope = new Scope(scope);
        for (int i = 0; i < function.Parameters.Count; i++)
            Declare(function.Parameters[i], e.Parameters[i].NameLocation);
        var body = bind();
        scope = scope.Parent;
        (currentFunction, loopDepth) = (enclosingFunction, enclosingLoops);
        return body;
    }

    /// <summary>The lambda whose return type is being inferred from its returns, and the values they give.</summary>
    (FunctionSymbol Function, List<BoundExpression> Values)? inferring;

    /// <summary>
    /// Binds a lambda's body before its return type is known, for generic inference. Its returns are
    /// converted once the type is known, by <see cref="RetargetLambda"/>.
    /// </summary>
    InferredLambda BindLambdaInferringReturn(BoundPendingLambda lambda, IReadOnlyList<FunctionParameterType> parameters)
    {
        var e = lambda.Syntax;
        // The return type stands in until it is inferred; returns don't look at it.
        var function = LambdaFunction(e, VplType.Error.FunctionReturning(parameters), lambda.DeclaredParameters, VplType.Error);
        var enclosing = inferring;
        var values = new List<BoundExpression>();
        inferring = (function, values);
        var body = BindLambdaBody(e, function, () =>
        {
            if (e.Body is not null)
                return BindBlock(e.Body);
            // An expression body that has no value is a statement, as in a `void` lambda.
            var value = BindValue(e.Value!, null);
            if (value.Type == VplType.Void)
                return new BoundBlock(e.Value!.Location, [new BoundExpressionStatement(e.Value.Location, value)]);
            values.Add(value);
            return new BoundBlock(e.Value!.Location, [new BoundReturn(e.Value.Location, value)]);
        });
        inferring = enclosing;
        return new InferredLambda(function, body, values);
    }

    /// <summary>An inferred lambda fits a function type with its parameters that each returned value converts to.</summary>
    bool InferredLambdaFits(InferredLambda lambda, VplType to) =>
        to.NonNull.IsFunction
        && to.NonNull.FunctionParameters.SequenceEqual(lambda.Function.Parameters.Select(p => new FunctionParameterType(p.RefKind, p.Type)))
        && (to.NonNull.FunctionReturnType == VplType.Void
            ? lambda.Values.Count == 0
            : lambda.Values.Count > 0 && lambda.Values.All(v => CanConvert(v, to.NonNull.FunctionReturnType!)));

    /// <summary>Gives an inferred lambda its function type, converting what it returns.</summary>
    BoundExpression RetargetLambda(BoundPendingLambda pending, InferredLambda lambda, VplType type)
    {
        if (!InferredLambdaFits(lambda, type))
            return ErrorExpression(pending.Location, $"this lambda cannot be converted to '{type}'");
        var old = lambda.Function;
        var function = new FunctionSymbol(old.Name, old.Parameters, type.FunctionReturnType!) { IsLambda = true, IsUnsafe = old.IsUnsafe, IsReadOnly = old.IsReadOnly };
        return new BoundLambda(pending.Location, type, function, (BoundBlock)ConvertReturns(lambda.Body, function));
    }

    BoundStatement ConvertReturns(BoundStatement statement, FunctionSymbol function) => statement switch
    {
        BoundReturn { Value: null } r when function.ReturnType != VplType.Void => ReportedReturn(r, $"this lambda must return a value of type '{function.ReturnType}'"),
        BoundReturn { Value: { } value } r => new BoundReturn(r.Location, Convert(value, function.ReturnType)),
        BoundBlock block => new BoundBlock(block.Location, [.. block.Statements.Select(s => ConvertReturns(s, function))]),
        BoundIf s => new BoundIf(s.Location, s.Condition, ConvertReturns(s.Then, function), s.Else is null ? null : ConvertReturns(s.Else, function)),
        BoundWhile s => new BoundWhile(s.Location, s.Condition, ConvertReturns(s.Body, function)),
        BoundFor s => new BoundFor(s.Location, s.Initializer, s.Condition, s.Update, ConvertReturns(s.Body, function)),
        BoundForRange s => new BoundForRange(s.Location, s.Local, s.Start, s.End, s.IsInclusive, ConvertReturns(s.Body, function)),
        BoundSwitch s => new BoundSwitch(s.Location, s.Value, [.. s.Cases.Select(c => new BoundSwitchCase<BoundStatement>(c.Location, c.Patterns, ConvertReturns(c.Body, function)))]),
        _ => statement,
    };

    BoundReturn ReportedReturn(BoundReturn r, string message)
    {
        Error(r.Location, message);
        return new BoundReturn(r.Location, null);
    }

    /// <summary>
    /// A branch or case of a <c>?:</c> or switch expression. Without a function type to expect, a
    /// lambda waits for one, as does a nested choice that has lambdas.
    /// </summary>
    BoundExpression BindOption(Expr option, VplType? expected) =>
        expected is not null && (expected.NonNull.IsFunction || expected == VplType.Error) ? BindExpression(option, expected)
        : option switch
        {
            LambdaExpr lambda => new BoundPendingLambda(lambda.Location, lambda, BindDeclaredParameters(lambda)),
            ConditionalExpr conditional => BindConditional(conditional, expected, mayWait: true),
            SwitchExpr switchExpression => BindSwitchExpression(switchExpression, expected, mayWait: true),
            _ => BindExpression(option, expected),
        };

    BoundExpression FinishOption(BoundExpression option, VplType? type) => option switch
    {
        BoundPendingLambda or BoundPendingChoice when type is null || type == VplType.Error => new BoundErrorExpression(option.Location),
        BoundPendingLambda => Convert(option, type!),
        BoundPendingChoice choice => choice.Finish(type!),
        _ => option,
    };

    /// <summary>
    /// Makes a <c>?:</c> or switch expression once its options are bound. If some are lambdas still
    /// waiting for a type, an argument (<paramref name="mayWait"/>) waits for overload resolution
    /// to give it one; anywhere else, the lambdas take another option's function type.
    /// </summary>
    BoundExpression Choose(Location location, IReadOnlyList<BoundExpression> options, bool mayWait, Func<VplType?, BoundExpression> make)
    {
        var leaves = options.SelectMany(Options).ToList();
        if (!leaves.Any(o => o is BoundPendingLambda))
            return make(null);
        if (mayWait)
            return new BoundPendingChoice(location, leaves, type => make(type));
        var type = leaves.FirstOrDefault(o => o.Type.IsFunction || o.Type == VplType.Error)?.Type;
        if (type is null)
        {
            Error(leaves.OfType<BoundPendingLambda>().First().Location, "a lambda needs a function type from where it is used, as in '(int) -> int f = x -> x + 1;'");
            type = VplType.Error;
        }
        return make(type);
    }

    static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    /// <summary>The function or lambda being bound, as messages name it.</summary>
    static string Describe(FunctionSymbol function) => function.IsLambda ? "this lambda" : $"'{function.Name}'";

    /// <summary>Calls a function value: a local, parameter, field, or any expression of function type.</summary>
    BoundExpression BindInvocation(CallExpr e, BoundExpression callee, PendingArguments pending)
    {
        if (callee.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);
        callee = RequireNonNull(callee);
        if (callee.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);

        var type = callee.Type;
        var signature = new MethodReference(
            new FunctionSymbol(TextOf(callee.Location), [.. type.FunctionParameters.Select((p, i) => new LocalSymbol($"p{i}", p.Type, isParameter: true, p.RefKind))], type.FunctionReturnType!),
            null,
            []);
        var arguments = pending.Bind([new Candidate(signature.Symbol, null)]);
        if (AnyError(arguments))
            return new BoundErrorExpression(e.Location);
        if (arguments.Count != type.FunctionParameters.Count)
        {
            return ErrorExpression(e.Location,
                $"'{TextOf(callee.Location)}' takes {Count(type.FunctionParameters.Count, "argument")}, but is given {arguments.Count}");
        }
        if (ExplainRefMismatch(signature, arguments) is { } mismatch)
            return ErrorExpression(mismatch.Location, mismatch.Message);
        return new BoundInvocation(e.Location, callee, signature, ConvertArguments(signature, arguments));
    }
}
