using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

public sealed partial class Binder
{
    /// <summary>
    /// A function type as written, which is escaping; see <see cref="BindVariableType"/> for
    /// parameters and locals. Its own parameters are non-escaping unless declared <c>escaping</c>.
    /// </summary>
    WylType BindFunctionType(TypeSyntax syntax)
    {
        var parameters = new List<FunctionParameterType>();
        bool failed = false;
        foreach (var parameter in syntax.Parameters)
        {
            var type = BindVariableType(parameter.Type, parameter.Escaping, parameter.Annotations, isParameter: true);
            failed |= type == WylType.Error || !CheckParameterMode(parameter.RefKind, type, parameter.Type.Location);
            parameters.Add(new FunctionParameterType(parameter.RefKind, type));
        }
        var returnType = BindType(syntax.Element!, allowVoid: true);
        if (returnType.IsRefStruct)
        {
            Error(syntax.Element!.Location, $"a function cannot return a '{returnType}', since there is nothing it could view");
            return WylType.Error;
        }
        if (failed || returnType == WylType.Error)
            return WylType.Error;
        return returnType.FunctionReturning(parameters).Escaping;
    }

    bool CheckParameterMode(RefKind refKind, WylType type, Location location)
    {
        if (refKind == RefKind.None || !type.PassedWithoutCopying)
            return true;
        var target = type.Referred.NonNull;
        Error(location, target.IsFunction
            ? $"'{FunctionSymbol.RefPrefix(refKind).Trim()}' cannot be used on a parameter of type '{target}'; functions are already passed without copying"
            : $"'{FunctionSymbol.RefPrefix(refKind).Trim()}' cannot be used on a parameter of type '{target}'; class objects are already passed by reference");
        return false;
    }

    /// <summary>The parameter types a lambda declares, or null if it leaves them to its target.</summary>
    IReadOnlyList<FunctionParameterType>? BindDeclaredParameters(LambdaExpr e)
    {
        if (e.Parameters.Count == 0 || e.Parameters[0].Type is null)
            return null;
        return [.. e.Parameters.Select(p =>
        {
            var type = BindVariableType(p.Type!, p.Escaping, p.Annotations, isParameter: true);
            return new FunctionParameterType(p.RefKind, CheckParameterMode(p.RefKind, type, p.Location) ? type : WylType.Error);
        })];
    }

    static bool LambdaFits(BoundPendingLambda lambda, WylType to) =>
        to.NonNull is FunctionType function
        && function.Parameters.Count == lambda.Syntax.Parameters.Count
        && (lambda.DeclaredParameters is null
            || lambda.DeclaredParameters.Zip(function.Parameters).All(p => p.First.Type == WylType.Error || p.First == p.Second));

    BoundExpression BindLambda(LambdaExpr e, FunctionType type, IReadOnlyList<FunctionParameterType>? declared)
    {
        var expected = type.Parameters;
        if (e.Parameters.Count != expected.Count)
        {
            return ErrorExpression(e.Location,
                $"this lambda has {Count(e.Parameters.Count, "parameter")}, but '{type}' takes {Count(expected.Count, "argument")}");
        }

        var function = LambdaFunction(e, type, declared ?? BindDeclaredParameters(e), type.ReturnType);
        var body = e.Body ?? new BlockStmt(e.Value!.Location, [function.ReturnType == WylType.Void
            ? new ExprStmt(e.Value.Location, e.Value)
            : new ReturnStmt(e.Value.Location, e.Value)]);
        return new BoundLambda(e.Location, type, function, BindLambdaBody(e, function, () => BindBlock(body)));
    }

    FunctionSymbol LambdaFunction(LambdaExpr e, FunctionType type, IReadOnlyList<FunctionParameterType>? declared, WylType returnType)
    {
        var expected = type.Parameters;
        var parameters = new List<LocalSymbol>();
        for (int i = 0; i < expected.Count; i++)
        {
            var syntax = e.Parameters[i];
            if (syntax.Type is null)
            {
                foreach (var annotation in syntax.Annotations)
                    ReportMisplacedAnnotation(annotation);
                if (syntax.Escaping is { } escaping)
                    Error(escaping, "an untyped lambda parameter takes its form from the lambda's type, so it can't be declared 'escaping'; give it a type");
            }
            if (declared is not null && declared[i] != expected[i] && declared[i].Type != WylType.Error)
            {
                Error(syntax.Location, $"'{syntax.Name}' is declared as '{declared[i]}', "
                    + $"but '{type}' passes '{expected[i]}'");
            }
            if (syntax.IsMutable && expected[i].RefKind != RefKind.None)
                Error(syntax.Location, $"'{syntax.Name}' is passed as '{FunctionSymbol.RefPrefix(expected[i].RefKind).Trim()}', so it cannot be 'mut'");
            else if (syntax.IsMutable && expected[i].Type.NonNull.IsNonEscapingFunction)
                Error(syntax.Location, NonEscapingMutMessage);
            parameters.Add(new LocalSymbol(syntax.Name, expected[i].Type, isParameter: true, expected[i].RefKind)
            {
                IsTemporary = e.IsFunctionReference,
                IsMutable = syntax.IsMutable && expected[i].RefKind == RefKind.None,
            });
        }
        return new FunctionSymbol("lambda", parameters, returnType)
        {
            IsLambda = true,
            IsEscaping = type.IsEscaping,
            Enclosing = currentFunction,
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
    InferredLambda BindLambdaInferringReturn(BoundPendingLambda lambda, IReadOnlyList<FunctionParameterType> parameters, bool escaping)
    {
        var e = lambda.SyntaxFor(parameters);
        // The return type stands in until it is inferred; returns don't look at it.
        var function = LambdaFunction(e, WylType.Error.FunctionReturning(parameters).WithEscaping(escaping), lambda.DeclaredParameters, WylType.Error);
        var enclosing = inferring;
        var values = new List<BoundExpression>();
        inferring = (function, values);
        var body = BindLambdaBody(e, function, () =>
        {
            if (e.Body is not null)
                return BindBlock(e.Body);
            // An expression body that has no value is a statement, as in a `void` lambda.
            var value = BindValue(e.Value!, null);
            if (value.Type == WylType.Void)
                return new BoundBlock(e.Value!.Location, [new BoundExpressionStatement(e.Value.Location, value)]);
            values.Add(value);
            return new BoundBlock(e.Value!.Location, [new BoundReturn(e.Value.Location, value)]);
        });
        inferring = enclosing;
        return new InferredLambda(function, body, values);
    }

    /// <summary>An inferred lambda fits a function type with its parameters that each returned value converts to.</summary>
    bool InferredLambdaFits(InferredLambda lambda, WylType to) =>
        to.NonNull is FunctionType function
        && function.Parameters.SequenceEqual(lambda.Function.Parameters.Select(p => new FunctionParameterType(p.RefKind, p.Type)))
        && (function.ReturnType == WylType.Void
            ? lambda.Values.Count == 0
            : lambda.Values.Count > 0 && lambda.Values.All(v => CanConvert(v, function.ReturnType)));

    /// <summary>Gives an inferred lambda its function type, converting what it returns. Overload resolution has checked that it fits.</summary>
    BoundLambda RetargetLambda(BoundPendingLambda pending, InferredLambda lambda, FunctionType type)
    {
        if (!InferredLambdaFits(lambda, type))
            throw new InvalidOperationException($"an inferred lambda was converted to '{type}', which it doesn't fit");
        // The body's locals and lambdas refer to this symbol, so it keeps being the lambda's.
        lambda.Function.InferReturnType(type.ReturnType);
        return new BoundLambda(pending.Location, type, lambda.Function, (BoundBlock)ConvertReturns(lambda.Body, lambda.Function));
    }

    BoundStatement ConvertReturns(BoundStatement statement, FunctionSymbol function) => statement switch
    {
        BoundReturn { Value: null } r when function.ReturnType != WylType.Void => ReportedReturn(r, $"this lambda must return a value of type '{function.ReturnType}'"),
        BoundReturn { Value: { } value } r => new BoundReturn(r.Location, Convert(value, function.ReturnType)),
        BoundBlock block => new BoundBlock(block.Location, [.. block.Statements.Select(s => ConvertReturns(s, function))]),
        BoundIf s => new BoundIf(s.Location, s.Condition, ConvertReturns(s.Then, function), s.Else is null ? null : ConvertReturns(s.Else, function)),
        BoundWhile s => new BoundWhile(s.Location, s.Condition, ConvertReturns(s.Body, function)),
        BoundFor s => new BoundFor(s.Location, s.Initializer, s.Condition, s.Update, ConvertReturns(s.Body, function)),
        BoundForRange s => new BoundForRange(s.Location, s.Local, s.Start, s.End, s.IsInclusive, ConvertReturns(s.Body, function)),
        BoundSwitch s => new BoundSwitch(s.Location, s.Value, [.. s.Cases.Select(c => c with { Body = ConvertReturns(c.Body, function) })]),
        // A statement kind added later could hold returns that would otherwise be left unconverted.
        _ when BoundTree.InnerStatements(statement).Any() => throw new InvalidOperationException($"returns in a {statement.GetType().Name} are not converted"),
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
    BoundExpression BindOption(Expr option, WylType? expected) =>
        expected is not null && (expected.NonNull.IsFunction || expected == WylType.Error) ? BindExpression(option, expected)
        : option switch
        {
            LambdaExpr lambda => new BoundPendingLambda(lambda.Location, lambda, BindDeclaredParameters(lambda)),
            _ when ReferencedFunctions(option) is { } referenced => BindFunctionReference(option, referenced, null),
            ConditionalExpr conditional => BindConditional(conditional, expected, mayWait: true),
            SwitchExpr switchExpression => BindSwitchExpression(switchExpression, expected, mayWait: true),
            _ => BindExpression(option, expected),
        };

    BoundExpression FinishOption(BoundExpression option, WylType? type) => option switch
    {
        BoundPendingLambda or BoundPendingChoice when type is null || type == WylType.Error => new BoundErrorExpression(option.Location),
        BoundPendingLambda => Convert(option, type!),
        BoundPendingChoice choice => choice.Finish(type!),
        _ => option,
    };

    /// <summary>
    /// Makes a <c>?:</c> or switch expression once its options are bound. If some are lambdas still
    /// waiting for a type, an argument (<paramref name="mayWait"/>) waits for overload resolution
    /// to give it one; anywhere else, the lambdas take another option's function type.
    /// </summary>
    BoundExpression Choose(Location location, IReadOnlyList<BoundExpression> options, WylType? expected, bool mayWait, Func<WylType?, BoundExpression> make)
    {
        var leaves = options.SelectMany(Options).ToList();
        if (!leaves.Any(o => o is BoundPendingLambda))
            return make(null);
        if (mayWait)
            return new BoundPendingChoice(location, leaves, type => make(type));
        var type = leaves.FirstOrDefault(o => o.Type.IsFunction || o.Type == WylType.Error)?.Type;
        if (type is null)
        {
            var lambda = leaves.OfType<BoundPendingLambda>().First();
            Error(lambda.Location, expected is not null && expected != WylType.Error ? NotFunctionTypeMessage(lambda, expected)
                : lambda.Syntax.IsFunctionReference
                ? $"'{TextOf(lambda.Location)}' is a function, which can only be used as a value where a function type is expected"
                : "a lambda needs a function type from where it is used, as in 'fn(int) -> int f = x -> x + 1;'");
            type = WylType.Error;
        }
        return make(type);
    }

    static string NotFunctionTypeMessage(BoundPendingLambda lambda, WylType to) =>
        $"{(lambda.Syntax.IsFunctionReference ? "a function" : "a lambda")} cannot be converted to '{to}', which is not a function type";

    static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    /// <summary>An argument as a message lists it: its type, or what a lambda still waiting for one takes.</summary>
    static string DescribeArgument(BoundExpression argument) => argument switch
    {
        BoundPendingLambda { DeclaredParameters: { } declared } => $"lambda taking ({string.Join(", ", declared)})",
        BoundPendingLambda lambda => $"lambda taking {Count(lambda.Syntax.Parameters.Count, "argument")}",
        BoundPendingChoice => "choice of lambdas",
        _ => argument.Type.ToString(),
    };

    /// <summary>The function or lambda being bound, as messages name it.</summary>
    static string Describe(FunctionSymbol function) => function.IsLambda ? "this lambda" : $"'{function.Name}'";

    /// <summary>Calls a function value: a local, parameter, field, or any expression of function type.</summary>
    BoundExpression BindInvocation(CallExpr e, BoundExpression callee, PendingArguments pending)
    {
        if (callee.Type == WylType.Error)
            return new BoundErrorExpression(e.Location);
        callee = RequireNonNull(callee);
        if (callee.Type == WylType.Error)
            return new BoundErrorExpression(e.Location);

        var type = (FunctionType)callee.Type;
        var signature = new MethodReference(
            new FunctionSymbol(TextOf(callee.Location), [.. type.Parameters.Select((p, i) => new LocalSymbol($"p{i}", p.Type, isParameter: true, p.RefKind))], type.ReturnType),
            null,
            []);
        if (e.Arguments.OfType<NamedArgExpr>().FirstOrDefault() is { } named)
        {
            pending.Bind(null);
            return ErrorExpression(named.NameLocation, $"arguments to '{TextOf(callee.Location)}' cannot be named, since a function type's parameters have no names");
        }
        var arguments = pending.Bind([new Candidate(signature.Symbol, null)]);
        if (AnyError(arguments))
            return new BoundErrorExpression(e.Location);
        if (arguments.Count != type.Parameters.Count)
        {
            return ErrorExpression(e.Location,
                $"'{TextOf(callee.Location)}' takes {Count(type.Parameters.Count, "argument")}, but is given {arguments.Count}");
        }
        if (ExplainRefMismatch(signature, arguments) is { } mismatch)
            return ErrorExpression(mismatch.Location, mismatch.Message);
        return new BoundInvocation(e.Location, callee, signature, ConvertArguments(signature, arguments));
    }
}
