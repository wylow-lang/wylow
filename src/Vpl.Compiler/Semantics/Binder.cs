using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

public sealed class Binder(DiagnosticBag diagnostics)
{
    readonly Dictionary<string, List<FunctionSymbol>> functions = new();
    FunctionSymbol? currentFunction;
    Scope? scope;
    int loopDepth;

    sealed class Scope(Scope? parent)
    {
        readonly Dictionary<string, LocalSymbol> locals = new();

        public Scope? Parent { get; } = parent;

        public LocalSymbol? Lookup(string name)
        {
            for (var s = this; s is not null; s = s.Parent)
            {
                if (s.locals.TryGetValue(name, out var local))
                    return local;
            }
            return null;
        }

        public void Add(LocalSymbol local) => locals[local.Name] = local;
    }

    public BoundProgram BindProgram(CompilationUnit unit)
    {
        foreach (var builtin in Builtins.All)
            AddFunction(builtin);

        var declared = unit.Functions.Select(decl => (Symbol: DeclareFunction(decl), Declaration: decl)).ToList();
        var bound = declared.Select(d => new BoundFunction(d.Symbol, BindBody(d.Symbol, d.Declaration))).ToList();
        return new BoundProgram(unit.Source, bound, FindMain(unit.Source));
    }

    void Error(Location location, string message) => diagnostics.Error(location, message);

    void AddFunction(FunctionSymbol function)
    {
        if (!functions.TryGetValue(function.Name, out var overloads))
            functions[function.Name] = overloads = [];
        overloads.Add(function);
    }

    FunctionSymbol DeclareFunction(FunctionDecl decl)
    {
        var returnType = BindType(decl.ReturnType, allowVoid: true);
        var parameters = new List<LocalSymbol>();
        foreach (var parameter in decl.Parameters)
        {
            if (parameters.Any(p => p.Name == parameter.Name))
                Error(parameter.Location, $"duplicate parameter '{parameter.Name}'");
            parameters.Add(new LocalSymbol(parameter.Name, BindType(parameter.Type, allowVoid: false), isParameter: true));
        }

        var function = new FunctionSymbol(decl.Name, parameters, returnType) { Declaration = decl };
        var existing = functions.GetValueOrDefault(decl.Name)?.FirstOrDefault(f =>
            f.Parameters.Select(p => p.Type).SequenceEqual(parameters.Select(p => p.Type)));
        if (existing is null)
            AddFunction(function);
        else if (existing.IsBuiltin)
            Error(decl.NameLocation, $"'{function.Signature}' conflicts with a built-in function");
        else
            Error(decl.NameLocation, $"function '{function.Signature}' is already defined");
        return function;
    }

    FunctionSymbol? FindMain(SourceText source)
    {
        if (!functions.TryGetValue("main", out var candidates))
        {
            Error(new Location(source, new TextSpan(0, 0)), "program has no 'main' function");
            return null;
        }

        FunctionSymbol? main = null;
        foreach (var candidate in candidates)
        {
            if (candidate.Parameters.Count != 0)
                Error(candidate.Declaration!.NameLocation, "'main' must not take parameters");
            else if (candidate.ReturnType != VplType.Void && candidate.ReturnType != VplType.Int)
                Error(candidate.Declaration!.NameLocation, "'main' must return 'void' or 'int'");
            else
                main = candidate;
        }
        return main;
    }

    VplType BindType(TypeSyntax syntax, bool allowVoid)
    {
        var type = syntax.Name switch
        {
            "void" => VplType.Void,
            "bool" => VplType.Bool,
            "int" => VplType.Int,
            "long" => VplType.Long,
            "float" => VplType.Float,
            "double" => VplType.Double,
            "String" => VplType.String,
            _ => null,
        };
        if (type is null)
        {
            Error(syntax.Location, $"unknown type '{syntax.Name}'");
            return VplType.Error;
        }
        if (type == VplType.Void && !allowVoid)
        {
            Error(syntax.Location, "'void' cannot be used here");
            return VplType.Error;
        }
        return type;
    }

    BoundBlock BindBody(FunctionSymbol function, FunctionDecl decl)
    {
        currentFunction = function;
        scope = new Scope(null);
        foreach (var parameter in function.Parameters)
            scope.Add(parameter);
        var body = BindBlock(decl.Body);
        scope = null;
        currentFunction = null;
        return body;
    }

    void Declare(LocalSymbol local, Location location)
    {
        // Like Java, a local may not shadow another local or parameter.
        if (scope!.Lookup(local.Name) is not null)
            Error(location, $"'{local.Name}' is already defined");
        else
            scope.Add(local);
    }

    BoundStatement BindStatement(Stmt statement) => statement switch
    {
        BlockStmt s => BindBlock(s),
        VarDeclStmt s => BindVariableDeclaration(s),
        IfStmt s => new BoundIf(s.Location, BindCondition(s.Condition), BindBlock(s.Then), s.Else is null ? null : BindStatement(s.Else)),
        WhileStmt s => BindWhile(s),
        ForStmt s => BindFor(s),
        ReturnStmt s => BindReturn(s),
        BreakStmt s => BindJump(s.Location, "break", new BoundBreak(s.Location)),
        ContinueStmt s => BindJump(s.Location, "continue", new BoundContinue(s.Location)),
        ExprStmt s => BindExpressionStatement(s),
        AssignStmt s => BindAssignment(s),
        IncDecStmt s => BindIncDec(s),
        _ => throw new InvalidOperationException($"unexpected statement {statement.GetType().Name}"),
    };

    BoundBlock BindBlock(BlockStmt block)
    {
        scope = new Scope(scope);
        var statements = block.Statements.Select(BindStatement).ToList();
        scope = scope.Parent;
        return new BoundBlock(block.Location, statements);
    }

    BoundStatement BindVariableDeclaration(VarDeclStmt s)
    {
        var initializer = s.Initializer is null ? null : BindExpression(s.Initializer);
        VplType type;
        if (s.Type is not null)
        {
            type = BindType(s.Type, allowVoid: false);
            if (initializer is not null)
                initializer = Convert(initializer, type);
        }
        else if (initializer is null)
        {
            Error(s.Location, "'var' declarations require an initializer");
            type = VplType.Error;
        }
        else if (initializer.Type == VplType.Void)
        {
            Error(initializer.Location, "cannot assign a 'void' value to a variable");
            type = VplType.Error;
        }
        else
        {
            type = initializer.Type;
        }

        var local = new LocalSymbol(s.Name, type, isParameter: false);
        Declare(local, s.NameLocation);
        return new BoundVariableDeclaration(s.Location, local, initializer);
    }

    BoundStatement BindWhile(WhileStmt s)
    {
        var condition = BindCondition(s.Condition);
        loopDepth++;
        var body = BindBlock(s.Body);
        loopDepth--;
        return new BoundWhile(s.Location, condition, body);
    }

    BoundStatement BindFor(ForStmt s)
    {
        scope = new Scope(scope);
        var initializer = s.Initializer is null ? null : BindStatement(s.Initializer);
        var condition = s.Condition is null ? null : BindCondition(s.Condition);
        loopDepth++;
        var body = BindBlock(s.Body);
        loopDepth--;
        var update = s.Update is null ? null : BindStatement(s.Update);
        scope = scope.Parent;
        return new BoundFor(s.Location, initializer, condition, update, body);
    }

    BoundStatement BindReturn(ReturnStmt s)
    {
        var function = currentFunction!;
        if (s.Value is null)
        {
            if (function.ReturnType != VplType.Void && function.ReturnType != VplType.Error)
                Error(s.Location, $"'{function.Name}' must return a value of type '{function.ReturnType}'");
            return new BoundReturn(s.Location, null);
        }

        var value = BindExpression(s.Value);
        if (function.ReturnType == VplType.Void)
        {
            Error(s.Value.Location, $"'{function.Name}' returns 'void' and cannot return a value");
            return new BoundReturn(s.Location, null);
        }
        return new BoundReturn(s.Location, Convert(value, function.ReturnType));
    }

    BoundStatement BindJump(Location location, string keyword, BoundStatement jump)
    {
        if (loopDepth == 0)
            Error(location, $"'{keyword}' can only be used inside a loop");
        return jump;
    }

    BoundStatement BindExpressionStatement(ExprStmt s)
    {
        var expression = BindExpression(s.Expression);
        if (expression is not BoundCall && expression.Type != VplType.Error)
            Error(s.Location, "only calls can be used as statements");
        return new BoundExpressionStatement(s.Location, expression);
    }

    BoundStatement BindAssignment(AssignStmt s)
    {
        var target = BindAssignmentTarget(s.Target);
        var value = BindExpression(s.Value);
        if (target is null)
            return new BoundExpressionStatement(s.Location, new BoundErrorExpression(s.Location));

        if (s.Operator != TokenKind.Assign)
        {
            var current = new BoundLocalAccess(s.Target.Location, target);
            value = BindBinaryOperation(s.OperatorLocation, current, SyntaxFacts.CompoundToBinary(s.Operator), value);
        }
        return new BoundAssignment(s.Location, target, Convert(value, target.Type));
    }

    BoundStatement BindIncDec(IncDecStmt s)
    {
        var target = BindAssignmentTarget(s.Target);
        if (target is null || target.Type == VplType.Error)
            return new BoundExpressionStatement(s.Location, new BoundErrorExpression(s.Location));
        if (!target.Type.IsNumeric)
        {
            Error(s.Location, $"'{(s.IsIncrement ? "++" : "--")}' requires a numeric variable");
            return new BoundExpressionStatement(s.Location, new BoundErrorExpression(s.Location));
        }

        object one = target.Type == VplType.Int ? (object)1
            : target.Type == VplType.Long ? (object)1L
            : target.Type == VplType.Float ? (object)1f
            : (object)1d;
        var current = new BoundLocalAccess(s.Target.Location, target);
        var value = BindBinaryOperation(
            s.Location, current, s.IsIncrement ? TokenKind.Plus : TokenKind.Minus, new BoundLiteral(s.Location, target.Type, one));
        return new BoundAssignment(s.Location, target, value);
    }

    LocalSymbol? BindAssignmentTarget(Expr target)
    {
        if (target is NameExpr name)
        {
            var local = scope!.Lookup(name.Name);
            if (local is null)
                Error(name.Location, $"'{name.Name}' is not defined");
            return local;
        }
        if (target is not ErrorExpr)
            Error(target.Location, "cannot assign to this expression");
        return null;
    }

    BoundExpression BindCondition(Expr condition) => Convert(BindExpression(condition), VplType.Bool);

    BoundExpression BindExpression(Expr expression) => expression switch
    {
        IntLiteralExpr e => BindIntegerLiteral(e, e.Location, negated: false),
        FloatLiteralExpr e => e.IsDouble
            ? new BoundLiteral(e.Location, VplType.Double, e.Value)
            : new BoundLiteral(e.Location, VplType.Float, (float)e.Value),
        BoolLiteralExpr e => new BoundLiteral(e.Location, VplType.Bool, e.Value),
        StringLiteralExpr e => new BoundLiteral(e.Location, VplType.String, e.Value),
        NameExpr e => BindName(e),
        UnaryExpr e => BindUnary(e),
        BinaryExpr e => BindBinaryOperation(e.OperatorLocation, BindExpression(e.Left), e.Operator, BindExpression(e.Right)),
        CallExpr e => BindCall(e),
        CastExpr e => BindCast(e),
        ConditionalExpr e => BindConditional(e),
        ErrorExpr e => new BoundErrorExpression(e.Location),
        _ => throw new InvalidOperationException($"unexpected expression {expression.GetType().Name}"),
    };

    BoundExpression BindIntegerLiteral(IntLiteralExpr e, Location location, bool negated)
    {
        // Negation is folded into the literal so that int.MinValue and long.MinValue can be written.
        ulong limit = (e.IsLong ? (ulong)long.MaxValue : int.MaxValue) + (negated ? 1UL : 0UL);
        if (e.Value > limit)
        {
            Error(location, e.IsLong
                ? "integer literal is too large for 'long'"
                : "integer literal is too large for 'int'; add an 'L' suffix to make it a 'long'");
            return new BoundErrorExpression(location);
        }

        if (e.IsLong)
            return new BoundLiteral(location, VplType.Long, negated ? unchecked((long)(0UL - e.Value)) : (long)e.Value);
        return new BoundLiteral(location, VplType.Int, negated ? unchecked((int)(0U - (uint)e.Value)) : (int)e.Value);
    }

    BoundExpression BindName(NameExpr e)
    {
        if (scope!.Lookup(e.Name) is { } local)
            return new BoundLocalAccess(e.Location, local);
        Error(e.Location, functions.ContainsKey(e.Name)
            ? $"'{e.Name}' is a function; call it with '{e.Name}(...)'"
            : $"'{e.Name}' is not defined");
        return new BoundErrorExpression(e.Location);
    }

    BoundExpression BindUnary(UnaryExpr e)
    {
        if (e.Operator == TokenKind.Minus && e.Operand is IntLiteralExpr literal)
            return BindIntegerLiteral(literal, e.Location, negated: true);

        var operand = BindExpression(e.Operand);
        if (operand.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);

        var (op, valid) = e.Operator switch
        {
            TokenKind.Minus => (BoundUnaryOperator.Negate, operand.Type.IsNumeric),
            TokenKind.Bang => (BoundUnaryOperator.LogicalNot, operand.Type == VplType.Bool),
            TokenKind.Tilde => (BoundUnaryOperator.BitwiseNot, operand.Type.IsInteger),
            _ => throw new InvalidOperationException($"unexpected unary operator {e.Operator}"),
        };
        if (!valid)
        {
            Error(e.Location, $"operator '{SyntaxFacts.GetText(e.Operator)}' cannot be applied to '{operand.Type}'");
            return new BoundErrorExpression(e.Location);
        }
        return new BoundUnary(e.Location, op, operand);
    }

    static BoundBinaryOperator BinaryOperatorFor(TokenKind kind) => kind switch
    {
        TokenKind.Plus => BoundBinaryOperator.Add,
        TokenKind.Minus => BoundBinaryOperator.Subtract,
        TokenKind.Star => BoundBinaryOperator.Multiply,
        TokenKind.Slash => BoundBinaryOperator.Divide,
        TokenKind.Percent => BoundBinaryOperator.Remainder,
        TokenKind.Ampersand => BoundBinaryOperator.BitwiseAnd,
        TokenKind.Pipe => BoundBinaryOperator.BitwiseOr,
        TokenKind.Caret => BoundBinaryOperator.BitwiseXor,
        TokenKind.LessLess => BoundBinaryOperator.ShiftLeft,
        TokenKind.GreaterGreater => BoundBinaryOperator.ShiftRight,
        TokenKind.AmpersandAmpersand => BoundBinaryOperator.LogicalAnd,
        TokenKind.PipePipe => BoundBinaryOperator.LogicalOr,
        TokenKind.EqualEqual => BoundBinaryOperator.Equal,
        TokenKind.BangEqual => BoundBinaryOperator.NotEqual,
        TokenKind.Less => BoundBinaryOperator.Less,
        TokenKind.LessEqual => BoundBinaryOperator.LessOrEqual,
        TokenKind.Greater => BoundBinaryOperator.Greater,
        TokenKind.GreaterEqual => BoundBinaryOperator.GreaterOrEqual,
        _ => throw new InvalidOperationException($"unexpected binary operator {kind}"),
    };

    BoundExpression BindBinaryOperation(Location location, BoundExpression left, TokenKind syntaxOperator, BoundExpression right)
    {
        if (left.Type == VplType.Error || right.Type == VplType.Error)
            return new BoundErrorExpression(location);

        var op = BinaryOperatorFor(syntaxOperator);
        bool bothBool = left.Type == VplType.Bool && right.Type == VplType.Bool;
        bool bothNumeric = left.Type.IsNumeric && right.Type.IsNumeric;
        bool bothInteger = left.Type.IsInteger && right.Type.IsInteger;

        VplType? operandType = null;
        bool producesBool = false;
        switch (op)
        {
            case BoundBinaryOperator.ShiftLeft or BoundBinaryOperator.ShiftRight:
                if (bothInteger)
                    return new BoundBinary(location, left, op, right, left.Type);
                break;
            case BoundBinaryOperator.LogicalAnd or BoundBinaryOperator.LogicalOr:
                operandType = bothBool ? VplType.Bool : null;
                break;
            case BoundBinaryOperator.BitwiseAnd or BoundBinaryOperator.BitwiseOr or BoundBinaryOperator.BitwiseXor:
                operandType = bothBool ? VplType.Bool : bothInteger ? CommonNumericType(left, right) : null;
                break;
            case BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual:
                operandType = bothBool ? VplType.Bool : bothNumeric ? CommonNumericType(left, right) : null;
                producesBool = true;
                break;
            case BoundBinaryOperator.Less or BoundBinaryOperator.LessOrEqual
                or BoundBinaryOperator.Greater or BoundBinaryOperator.GreaterOrEqual:
                operandType = bothNumeric ? CommonNumericType(left, right) : null;
                producesBool = true;
                break;
            default:
                operandType = bothNumeric ? CommonNumericType(left, right) : null;
                break;
        }

        if (operandType is null)
        {
            var hint = bothNumeric ? "; use an explicit cast to convert one side" : "";
            Error(location, $"operator '{SyntaxFacts.GetText(syntaxOperator)}' cannot be applied to '{left.Type}' and '{right.Type}'{hint}");
            return new BoundErrorExpression(location);
        }
        var resultType = producesBool ? VplType.Bool : operandType;
        return new BoundBinary(location, Convert(left, operandType), op, Convert(right, operandType), resultType);
    }

    VplType? CommonNumericType(BoundExpression left, BoundExpression right)
    {
        if (left.Type == right.Type)
            return left.Type;
        if (CanConvert(left, right.Type))
            return right.Type;
        if (CanConvert(right, left.Type))
            return left.Type;
        return null;
    }

    static bool CanConvert(BoundExpression expression, VplType to) =>
        Conversions.IsImplicit(expression.Type, to) || IsConvertibleConstant(expression, to);

    /// <summary>Integer literals convert implicitly to float or double when no precision is lost.</summary>
    static bool IsConvertibleConstant(BoundExpression expression, VplType to) =>
        to.IsFloatingPoint
        && expression is BoundLiteral { Value: int or long } literal
        && Conversions.IsExactlyRepresentable(System.Convert.ToInt64(literal.Value), to);

    BoundExpression Convert(BoundExpression expression, VplType to)
    {
        if (expression.Type == to || expression.Type == VplType.Error || to == VplType.Error)
            return expression;

        if (IsConvertibleConstant(expression, to))
        {
            long value = System.Convert.ToInt64(((BoundLiteral)expression).Value);
            return new BoundLiteral(expression.Location, to, to == VplType.Float ? (object)(float)value : (object)(double)value);
        }

        if (Conversions.IsImplicit(expression.Type, to))
            return new BoundConversion(expression.Location, to, expression);

        var hint = Conversions.IsExplicit(expression.Type, to) ? "; use an explicit cast" : "";
        Error(expression.Location, $"cannot implicitly convert '{expression.Type}' to '{to}'{hint}");
        return new BoundErrorExpression(expression.Location);
    }

    BoundExpression BindCall(CallExpr e)
    {
        var arguments = e.Arguments.Select(BindExpression).ToList();
        if (e.Callee is not NameExpr name)
        {
            Error(e.Callee.Location, "this expression cannot be called");
            return new BoundErrorExpression(e.Location);
        }
        if (!functions.TryGetValue(name.Name, out var overloads))
        {
            Error(name.Location, scope!.Lookup(name.Name) is not null
                ? $"'{name.Name}' is a variable, not a function"
                : $"function '{name.Name}' is not defined");
            return new BoundErrorExpression(e.Location);
        }
        if (arguments.Any(a => a.Type == VplType.Error))
            return new BoundErrorExpression(e.Location);

        var function = ResolveOverload(e.Location, name.Name, overloads, arguments);
        if (function is null)
            return new BoundErrorExpression(e.Location);

        var converted = function.Parameters.Zip(arguments, (p, a) => Convert(a, p.Type)).ToList();
        return new BoundCall(e.Location, function, converted);
    }

    FunctionSymbol? ResolveOverload(Location location, string name, List<FunctionSymbol> overloads, List<BoundExpression> arguments)
    {
        var sameArity = overloads.Where(f => f.Parameters.Count == arguments.Count).ToList();
        if (sameArity.Count == 0)
        {
            Error(location, $"no overload of '{name}' takes {arguments.Count} argument{(arguments.Count == 1 ? "" : "s")}");
            return null;
        }

        var applicable = sameArity
            .Where(f => f.Parameters.Zip(arguments).All(pair => CanConvert(pair.Second, pair.First.Type)))
            .ToList();
        if (applicable.Count == 0)
        {
            Error(location, $"no overload of '{name}' accepts ({string.Join(", ", arguments.Select(a => a.Type))})");
            return null;
        }

        var exact = applicable.FirstOrDefault(f => f.Parameters.Zip(arguments).All(pair => pair.First.Type == pair.Second.Type));
        if (exact is not null)
            return exact;

        // Java's rule: pick the overload whose parameters all convert to every other candidate's parameters.
        var mostSpecific = applicable.Where(f => applicable.All(g => g == f || IsMoreSpecific(f, g))).ToList();
        if (mostSpecific.Count == 1)
            return mostSpecific[0];

        Error(location, $"call to '{name}' is ambiguous between {string.Join(" and ", applicable.Select(f => $"'{f.Signature}'"))}");
        return null;
    }

    static bool IsMoreSpecific(FunctionSymbol f, FunctionSymbol g) =>
        f.Parameters.Zip(g.Parameters).All(pair => Conversions.IsImplicit(pair.First.Type, pair.Second.Type));

    BoundExpression BindCast(CastExpr e)
    {
        var operand = BindExpression(e.Operand);
        var type = BindType(e.Type, allowVoid: false);
        if (operand.Type == VplType.Error || type == VplType.Error)
            return new BoundErrorExpression(e.Location);
        if (operand.Type == type)
            return operand;
        if (!Conversions.IsExplicit(operand.Type, type))
        {
            Error(e.Location, $"cannot convert '{operand.Type}' to '{type}'");
            return new BoundErrorExpression(e.Location);
        }
        return new BoundConversion(e.Location, type, operand);
    }

    BoundExpression BindConditional(ConditionalExpr e)
    {
        var condition = BindCondition(e.Condition);
        var whenTrue = BindExpression(e.WhenTrue);
        var whenFalse = BindExpression(e.WhenFalse);
        if (whenTrue.Type == VplType.Error || whenFalse.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);

        VplType? type = whenTrue.Type == whenFalse.Type ? whenTrue.Type
            : CanConvert(whenTrue, whenFalse.Type) ? whenFalse.Type
            : CanConvert(whenFalse, whenTrue.Type) ? whenTrue.Type
            : null;
        if (type is null || type == VplType.Void)
        {
            Error(e.Location, $"the branches of '?:' have incompatible types '{whenTrue.Type}' and '{whenFalse.Type}'");
            return new BoundErrorExpression(e.Location);
        }
        return new BoundConditional(e.Location, condition, Convert(whenTrue, type), Convert(whenFalse, type), type);
    }
}
