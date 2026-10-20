using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

public sealed class Binder(DiagnosticBag diagnostics)
{
    readonly Dictionary<string, List<FunctionSymbol>> functions = new();
    readonly Dictionary<string, StructSymbol> structs = new();

    FunctionSymbol? currentFunction;
    StructSymbol? currentStruct;
    bool hasThis;
    bool inFieldInitializer;
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

    sealed record Argument(BoundExpression Expression, bool IsRef, Location Location);

    enum PlaceKind { Temporary, Mutable, ReadOnly }

    public BoundProgram BindProgram(CompilationUnit unit)
    {
        foreach (var builtin in Builtins.All)
            AddFunction(builtin);

        var structList = new List<StructSymbol>();
        foreach (var decl in unit.Declarations.OfType<StructDecl>())
        {
            if (DeclareStruct(decl) is { } symbol)
                structList.Add(symbol);
        }

        var functionList = unit.Declarations.OfType<FunctionDecl>().Select(DeclareFunction).ToList();
        foreach (var s in structList)
            DeclareMembers(s);
        CheckStructCycles(structList);
        foreach (var s in structList)
            BindFieldInitializers(s);

        var bodies = new List<BoundFunction>();
        foreach (var function in structList.SelectMany(s => s.Members).Concat(functionList))
            bodies.Add(new BoundFunction(function, BindBody(function)));

        return new BoundProgram(unit.Source, structList, bodies, FindMain(unit.Source));
    }

    void Error(Location location, string message) => diagnostics.Error(location, message);

    static string TextOf(Location location) => location.Source.Text.Substring(location.Span.Start, location.Span.Length);

    // ---- Declarations ----

    void AddFunction(FunctionSymbol function)
    {
        if (!functions.TryGetValue(function.Name, out var overloads))
            functions[function.Name] = overloads = [];
        overloads.Add(function);
    }

    StructSymbol? DeclareStruct(StructDecl decl)
    {
        if (structs.ContainsKey(decl.Name) || BuiltinType(decl.Name) is not null)
        {
            Error(decl.NameLocation, $"'{decl.Name}' is already defined");
            return null;
        }
        var symbol = new StructSymbol(decl);
        structs[decl.Name] = symbol;
        return symbol;
    }

    FunctionSymbol DeclareFunction(FunctionDecl decl)
    {
        var function = new FunctionSymbol(decl.Name, BindParameters(decl.Parameters), BindType(decl.ReturnType!, allowVoid: true))
        {
            Declaration = decl,
        };

        if (structs.ContainsKey(decl.Name))
        {
            Error(decl.NameLocation, $"'{decl.Name}' is already defined as a struct");
            return function;
        }

        var existing = FindSameSignature(functions.GetValueOrDefault(decl.Name), function);
        if (existing is null)
            AddFunction(function);
        else if (existing.IsBuiltin)
            Error(decl.NameLocation, $"'{function.Signature}' conflicts with a built-in function");
        else
            Error(decl.NameLocation, $"function '{function.Signature}' is already defined");
        return function;
    }

    static FunctionSymbol? FindSameSignature(IEnumerable<FunctionSymbol>? candidates, FunctionSymbol function) =>
        candidates?.FirstOrDefault(f => f.Parameters.Select(p => (p.Type, p.RefKind))
            .SequenceEqual(function.Parameters.Select(p => (p.Type, p.RefKind))));

    List<LocalSymbol> BindParameters(IReadOnlyList<ParameterSyntax> parameters)
    {
        var result = new List<LocalSymbol>();
        foreach (var parameter in parameters)
        {
            if (result.Any(p => p.Name == parameter.Name))
                Error(parameter.Location, $"duplicate parameter '{parameter.Name}'");
            result.Add(new LocalSymbol(parameter.Name, BindType(parameter.Type, allowVoid: false), isParameter: true, parameter.RefKind));
        }
        return result;
    }

    void DeclareMembers(StructSymbol s)
    {
        foreach (var decl in s.Declaration.Fields)
        {
            var field = new FieldSymbol(decl.Name, BindType(decl.Type, allowVoid: false), s, decl);
            if (s.FindField(decl.Name) is not null)
                Error(decl.NameLocation, $"field '{decl.Name}' is already defined");
            else
                s.Fields.Add(field);
        }

        foreach (var decl in s.Declaration.Methods)
        {
            var parameters = BindParameters(decl.Parameters);
            bool isPrivate = (decl.Modifiers & Modifiers.Private) != 0;
            if (decl.IsConstructor)
            {
                if (parameters.Count == 0)
                {
                    Error(decl.NameLocation, "structs cannot declare a parameterless constructor; use field initializers instead");
                    continue;
                }
                var constructor = new FunctionSymbol(s.Name, parameters, VplType.Void)
                {
                    Declaration = decl,
                    ContainingStruct = s,
                    IsConstructor = true,
                    IsPrivate = isPrivate,
                };
                if (FindSameSignature(s.Constructors, constructor) is not null)
                {
                    Error(decl.NameLocation, $"constructor '{constructor.Signature}' is already defined");
                    continue;
                }
                s.Constructors.Add(constructor);
                s.Members.Add(constructor);
                continue;
            }

            var method = new FunctionSymbol(decl.Name, parameters, BindType(decl.ReturnType!, allowVoid: true))
            {
                Declaration = decl,
                ContainingStruct = s,
                IsStatic = (decl.Modifiers & Modifiers.Static) != 0,
                IsReadOnly = (decl.Modifiers & Modifiers.ReadOnly) != 0,
                IsPrivate = isPrivate,
            };
            if (s.FindField(decl.Name) is not null)
            {
                Error(decl.NameLocation, $"'{decl.Name}' is already defined as a field");
                continue;
            }
            if (!s.Methods.TryGetValue(decl.Name, out var overloads))
                s.Methods[decl.Name] = overloads = [];
            if (FindSameSignature(overloads, method) is not null)
            {
                Error(decl.NameLocation, $"method '{method.Signature}' is already defined");
                continue;
            }
            overloads.Add(method);
            s.Members.Add(method);
        }
    }

    void CheckStructCycles(List<StructSymbol> all)
    {
        var done = new HashSet<StructSymbol>();
        var path = new List<StructSymbol>();

        void Visit(StructSymbol s)
        {
            if (done.Contains(s))
                return;
            int index = path.IndexOf(s);
            if (index >= 0)
            {
                var cycle = path.Skip(index).Append(s).Select(x => x.Name);
                Error(s.Declaration.NameLocation, $"struct '{s.Name}' contains itself ({string.Join(" -> ", cycle)}); a struct cannot hold itself by value");
                return;
            }
            path.Add(s);
            foreach (var field in s.Fields)
            {
                if (field.Type.Struct is { } nested)
                    Visit(nested);
            }
            path.RemoveAt(path.Count - 1);
            done.Add(s);
        }

        foreach (var s in all)
            Visit(s);
    }

    void BindFieldInitializers(StructSymbol s)
    {
        foreach (var field in s.Fields)
        {
            if (field.Declaration.Initializer is not { } initializer)
                continue;
            currentStruct = s;
            inFieldInitializer = true;
            scope = new Scope(null);
            field.Initializer = Convert(BindExpression(initializer), field.Type);
            currentStruct = null;
            inFieldInitializer = false;
            scope = null;
        }
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

    static VplType? BuiltinType(string name) => name switch
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

    VplType BindType(TypeSyntax syntax, bool allowVoid)
    {
        var type = BuiltinType(syntax.Name) ?? structs.GetValueOrDefault(syntax.Name)?.Type;
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

    // ---- Bodies and statements ----

    BoundBlock BindBody(FunctionSymbol function)
    {
        currentFunction = function;
        currentStruct = function.ContainingStruct;
        hasThis = currentStruct is not null && !function.IsStatic;
        scope = new Scope(null);
        foreach (var parameter in function.Parameters)
            scope.Add(parameter);

        var body = BindBlock(function.Declaration!.Body);

        currentFunction = null;
        currentStruct = null;
        hasThis = false;
        scope = null;
        return body;
    }

    void Declare(LocalSymbol local, Location location)
    {
        // Like Java, a local may not shadow another local or parameter (it may shadow a field).
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

    static BoundStatement ErrorStatement(Location location) =>
        new BoundExpressionStatement(location, new BoundErrorExpression(location));

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
        if (function.IsConstructor)
        {
            Error(s.Value.Location, "constructors cannot return a value");
            return new BoundReturn(s.Location, null);
        }
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
        var target = BindExpression(s.Target);
        var value = BindExpression(s.Value);
        if (target.Type == VplType.Error || !CheckAssignable(target))
            return ErrorStatement(s.Location);

        if (s.Operator != TokenKind.Assign)
            value = BindBinaryOperation(s.OperatorLocation, target, SyntaxFacts.CompoundToBinary(s.Operator), value);
        return new BoundAssignment(s.Location, target, Convert(value, target.Type));
    }

    BoundStatement BindIncDec(IncDecStmt s)
    {
        var target = BindExpression(s.Target);
        if (target.Type == VplType.Error || !CheckAssignable(target))
            return ErrorStatement(s.Location);
        if (!target.Type.IsNumeric)
        {
            Error(s.Location, $"'{(s.IsIncrement ? "++" : "--")}' requires a numeric variable");
            return ErrorStatement(s.Location);
        }

        object one = target.Type == VplType.Int ? (object)1
            : target.Type == VplType.Long ? (object)1L
            : target.Type == VplType.Float ? (object)1f
            : (object)1d;
        var value = BindBinaryOperation(
            s.Location, target, s.IsIncrement ? TokenKind.Plus : TokenKind.Minus, new BoundLiteral(s.Location, target.Type, one));
        return new BoundAssignment(s.Location, target, value);
    }

    // ---- Places: what can be assigned or passed by ref ----

    (PlaceKind Kind, string Reason) ClassifyPlace(BoundExpression expression) => expression switch
    {
        BoundLocalAccess { Local.RefKind: RefKind.In } access => (PlaceKind.ReadOnly, $"'{access.Local.Name}' is an 'in' parameter"),
        BoundLocalAccess => (PlaceKind.Mutable, ""),
        BoundThis when currentFunction is { IsReadOnly: true } => (PlaceKind.ReadOnly, "'this' is read-only in a readonly method"),
        BoundThis => (PlaceKind.Mutable, ""),
        BoundFieldAccess field => ClassifyPlace(field.Receiver),
        _ => (PlaceKind.Temporary, ""),
    };

    bool CheckAssignable(BoundExpression target)
    {
        var (kind, reason) = ClassifyPlace(target);
        switch (kind)
        {
            case PlaceKind.Temporary:
                Error(target.Location, target is BoundFieldAccess
                    ? "cannot assign to a field of a temporary value"
                    : "cannot assign to this expression");
                return false;
            case PlaceKind.ReadOnly:
                Error(target.Location, $"cannot assign to '{TextOf(target.Location)}' because {reason}");
                return false;
            default:
                return true;
        }
    }

    // ---- Expressions ----

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
        ThisExpr e => BindThis(e),
        MemberAccessExpr e => BindMemberAccess(e),
        UnaryExpr e => BindUnary(e),
        BinaryExpr e => BindBinaryOperation(e.OperatorLocation, BindExpression(e.Left), e.Operator, BindExpression(e.Right)),
        CallExpr e => BindCall(e),
        InitializerExpr e => BindInitializer(e),
        CastExpr e => BindCast(e),
        ConditionalExpr e => BindConditional(e),
        RefArgExpr e => ErrorExpression(e.Location, "'ref' can only be used on a call argument"),
        ErrorExpr e => new BoundErrorExpression(e.Location),
        _ => throw new InvalidOperationException($"unexpected expression {expression.GetType().Name}"),
    };

    BoundErrorExpression ErrorExpression(Location location, string message)
    {
        Error(location, message);
        return new BoundErrorExpression(location);
    }

    string NoInstanceReason => inFieldInitializer
        ? "field initializers cannot use 'this', fields, or instance methods"
        : "static methods cannot use 'this', fields, or instance methods";

    BoundExpression BindIntegerLiteral(IntLiteralExpr e, Location location, bool negated)
    {
        // Negation is folded into the literal so that int.MinValue and long.MinValue can be written.
        ulong limit = (e.IsLong ? (ulong)long.MaxValue : int.MaxValue) + (negated ? 1UL : 0UL);
        if (e.Value > limit)
        {
            return ErrorExpression(location, e.IsLong
                ? "integer literal is too large for 'long'"
                : "integer literal is too large for 'int'; add an 'L' suffix to make it a 'long'");
        }

        if (e.IsLong)
            return new BoundLiteral(location, VplType.Long, negated ? unchecked((long)(0UL - e.Value)) : (long)e.Value);
        return new BoundLiteral(location, VplType.Int, negated ? unchecked((int)(0U - (uint)e.Value)) : (int)e.Value);
    }

    /// <summary>A name refers to a struct type only if no local or field hides it.</summary>
    StructSymbol? AsTypeName(Expr expression) =>
        expression is NameExpr name
        && scope?.Lookup(name.Name) is null
        && currentStruct?.FindField(name.Name) is null
            ? structs.GetValueOrDefault(name.Name)
            : null;

    BoundExpression BindName(NameExpr e)
    {
        if (scope!.Lookup(e.Name) is { } local)
            return new BoundLocalAccess(e.Location, local);

        if (currentStruct?.FindField(e.Name) is { } field)
        {
            if (!hasThis)
                return ErrorExpression(e.Location, $"'{e.Name}' is an instance field; {NoInstanceReason}");
            return new BoundFieldAccess(e.Location, new BoundThis(e.Location, currentStruct.Type), field);
        }

        if (structs.ContainsKey(e.Name))
            return ErrorExpression(e.Location, $"'{e.Name}' is a type, not a value");
        if (functions.ContainsKey(e.Name) || currentStruct?.Methods.ContainsKey(e.Name) == true)
            return ErrorExpression(e.Location, $"'{e.Name}' is a function; call it with '{e.Name}(...)'");
        return ErrorExpression(e.Location, $"'{e.Name}' is not defined");
    }

    BoundExpression BindThis(ThisExpr e)
    {
        if (!hasThis)
        {
            return ErrorExpression(e.Location, currentStruct is null
                ? "'this' can only be used inside a struct"
                : $"'this' cannot be used here; {NoInstanceReason}");
        }
        return new BoundThis(e.Location, currentStruct!.Type);
    }

    BoundExpression BindMemberAccess(MemberAccessExpr e)
    {
        if (AsTypeName(e.Target) is { } typeName)
        {
            return ErrorExpression(e.NameLocation, typeName.Methods.ContainsKey(e.Name)
                ? $"'{typeName.Name}.{e.Name}' is a method; call it with '{e.Name}(...)'"
                : $"'{typeName.Name}' has no static member '{e.Name}'");
        }

        var target = BindExpression(e.Target);
        if (target.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);
        if (target.Type.Struct is not { } s)
            return ErrorExpression(e.NameLocation, $"'{target.Type}' has no member '{e.Name}'");

        if (s.FindField(e.Name) is not { } field)
        {
            return ErrorExpression(e.NameLocation, s.Methods.ContainsKey(e.Name)
                ? $"'{s.Name}.{e.Name}' is a method; call it with '{e.Name}(...)'"
                : $"'{s.Name}' has no field '{e.Name}'");
        }
        CheckAccessible(e.NameLocation, s, field.IsPrivate, $"field '{s.Name}.{e.Name}'");
        return new BoundFieldAccess(e.Location, target, field);
    }

    void CheckAccessible(Location location, StructSymbol owner, bool isPrivate, string description)
    {
        if (isPrivate && currentStruct != owner)
            Error(location, $"{description} is private");
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
            return ErrorExpression(e.Location, $"operator '{SyntaxFacts.GetText(e.Operator)}' cannot be applied to '{operand.Type}'");
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
        var symbol = SyntaxFacts.GetText(syntaxOperator);

        if (op is BoundBinaryOperator.Equal or BoundBinaryOperator.NotEqual && (left.Type.IsStruct || right.Type.IsStruct))
        {
            if (left.Type != right.Type)
                return ErrorExpression(location, $"operator '{symbol}' cannot be applied to '{left.Type}' and '{right.Type}'");
            if (left.Type.Struct!.FindNonEquatableField() is { } field)
            {
                return ErrorExpression(location,
                    $"operator '{symbol}' cannot be applied to '{left.Type}' because its field '{field.Name}' of type '{field.Type}' does not support '=='");
            }
            return new BoundBinary(location, left, op, right, VplType.Bool);
        }

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
            return ErrorExpression(location, $"operator '{symbol}' cannot be applied to '{left.Type}' and '{right.Type}'{hint}");
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
        return ErrorExpression(expression.Location, $"cannot implicitly convert '{expression.Type}' to '{to}'{hint}");
    }

    // ---- Calls and construction ----

    List<Argument> BindArguments(IEnumerable<Expr> arguments) =>
    [
        .. arguments.Select(a => a is RefArgExpr r
            ? new Argument(BindExpression(r.Operand), true, r.Location)
            : new Argument(BindExpression(a), false, a.Location)),
    ];

    BoundExpression BindCall(CallExpr e)
    {
        var arguments = BindArguments(e.Arguments);
        return e.Callee switch
        {
            NameExpr name => BindNameCall(e, name, arguments),
            MemberAccessExpr member => BindMemberCall(e, member, arguments),
            _ => ErrorExpression(e.Callee.Location, "this expression cannot be called"),
        };
    }

    BoundExpression BindNameCall(CallExpr e, NameExpr name, List<Argument> arguments)
    {
        if (scope!.Lookup(name.Name) is not null || currentStruct?.FindField(name.Name) is not null)
            return ErrorExpression(name.Location, $"'{name.Name}' is a variable, not a function");
        if (arguments.Any(a => a.Expression.Type == VplType.Error))
            return new BoundErrorExpression(e.Location);

        if (currentStruct is { } s && s.Methods.TryGetValue(name.Name, out var methods))
        {
            var method = Resolve(e.Location, $"overload of '{name.Name}'", methods, arguments);
            if (method is null)
                return new BoundErrorExpression(e.Location);
            if (method.IsStatic)
                return MakeCall(e.Location, method, null, arguments);
            if (!hasThis)
                return ErrorExpression(name.Location, $"'{name.Name}' is an instance method; {NoInstanceReason}");
            return MakeCall(e.Location, method, new BoundThis(name.Location, s.Type), arguments);
        }

        if (structs.TryGetValue(name.Name, out var type))
            return BindConstruction(e.Location, type, arguments, []);

        if (!functions.TryGetValue(name.Name, out var overloads))
            return ErrorExpression(name.Location, $"function '{name.Name}' is not defined");
        var function = Resolve(e.Location, $"overload of '{name.Name}'", overloads, arguments);
        return function is null ? new BoundErrorExpression(e.Location) : MakeCall(e.Location, function, null, arguments);
    }

    BoundExpression BindMemberCall(CallExpr e, MemberAccessExpr member, List<Argument> arguments)
    {
        if (AsTypeName(member.Target) is { } type)
        {
            if (!type.Methods.TryGetValue(member.Name, out var staticCandidates))
                return ErrorExpression(member.NameLocation, $"'{type.Name}' has no method '{member.Name}'");
            if (arguments.Any(a => a.Expression.Type == VplType.Error))
                return new BoundErrorExpression(e.Location);
            var staticMethod = Resolve(e.Location, $"overload of '{type.Name}.{member.Name}'", staticCandidates, arguments);
            if (staticMethod is null)
                return new BoundErrorExpression(e.Location);
            if (!staticMethod.IsStatic)
            {
                return ErrorExpression(member.NameLocation,
                    $"'{type.Name}.{member.Name}' is an instance method; call it on a value of type '{type.Name}'");
            }
            CheckAccessible(member.NameLocation, type, staticMethod.IsPrivate, $"method '{type.Name}.{member.Name}'");
            return MakeCall(e.Location, staticMethod, null, arguments);
        }

        var receiver = BindExpression(member.Target);
        if (receiver.Type == VplType.Error || arguments.Any(a => a.Expression.Type == VplType.Error))
            return new BoundErrorExpression(e.Location);
        if (receiver.Type.Struct is not { } s)
            return ErrorExpression(member.NameLocation, $"'{receiver.Type}' has no method '{member.Name}'");
        if (!s.Methods.TryGetValue(member.Name, out var candidates))
        {
            return ErrorExpression(member.NameLocation, s.FindField(member.Name) is not null
                ? $"'{s.Name}.{member.Name}' is a field, not a method"
                : $"'{s.Name}' has no method '{member.Name}'");
        }

        var method = Resolve(e.Location, $"overload of '{s.Name}.{member.Name}'", candidates, arguments);
        if (method is null)
            return new BoundErrorExpression(e.Location);
        if (method.IsStatic)
            return ErrorExpression(member.NameLocation, $"static method '{member.Name}' must be called through the type: '{s.Name}.{member.Name}(...)'");
        CheckAccessible(member.NameLocation, s, method.IsPrivate, $"method '{s.Name}.{member.Name}'");
        return MakeCall(e.Location, method, receiver, arguments);
    }

    BoundExpression MakeCall(Location location, FunctionSymbol function, BoundExpression? receiver, List<Argument> arguments)
    {
        if (receiver is not null && !function.IsReadOnly)
        {
            var (kind, reason) = ClassifyPlace(receiver);
            if (kind == PlaceKind.ReadOnly)
            {
                var what = receiver is BoundThis ? "'this'" : $"'{TextOf(receiver.Location)}'";
                return ErrorExpression(location,
                    $"cannot call '{function.Name}' on {what} because {reason}; only 'readonly' methods can be called on it");
            }
        }
        return new BoundCall(location, function, receiver, ConvertArguments(function, arguments));
    }

    List<BoundExpression> ConvertArguments(FunctionSymbol function, List<Argument> arguments)
    {
        var result = new List<BoundExpression>();
        foreach (var (parameter, argument) in function.Parameters.Zip(arguments))
        {
            if (parameter.RefKind != RefKind.Ref)
            {
                result.Add(Convert(argument.Expression, parameter.Type));
                continue;
            }

            var (kind, reason) = ClassifyPlace(argument.Expression);
            if (kind == PlaceKind.Temporary)
                Error(argument.Location, "a 'ref' argument must be a variable or field");
            else if (kind == PlaceKind.ReadOnly)
                Error(argument.Location, $"cannot pass '{TextOf(argument.Expression.Location)}' by 'ref' because {reason}");
            result.Add(argument.Expression);
        }
        return result;
    }

    FunctionSymbol? Resolve(Location location, string description, IReadOnlyList<FunctionSymbol> candidates, List<Argument> arguments)
    {
        var sameArity = candidates.Where(f => f.Parameters.Count == arguments.Count).ToList();
        if (sameArity.Count == 0)
        {
            Error(location, $"no {description} takes {arguments.Count} argument{(arguments.Count == 1 ? "" : "s")}");
            return null;
        }

        if (sameArity.Count == 1 && ExplainRefMismatch(sameArity[0], arguments) is { } mismatch)
        {
            Error(mismatch.Location, mismatch.Message);
            return null;
        }

        var applicable = sameArity.Where(f => IsApplicable(f, arguments)).ToList();
        if (applicable.Count == 0)
        {
            var types = string.Join(", ", arguments.Select(a => (a.IsRef ? "ref " : "") + a.Expression.Type));
            Error(location, $"no {description} accepts ({types})");
            return null;
        }

        var exact = applicable.FirstOrDefault(f => f.Parameters.Zip(arguments).All(pair => pair.First.Type == pair.Second.Expression.Type));
        if (exact is not null)
            return exact;

        // Java's rule: pick the overload whose parameters all convert to every other candidate's parameters.
        var mostSpecific = applicable.Where(f => applicable.All(g => g == f || IsMoreSpecific(f, g))).ToList();
        if (mostSpecific.Count == 1)
            return mostSpecific[0];

        Error(location, $"call is ambiguous between {string.Join(" and ", applicable.Select(f => $"'{f.Signature}'"))}");
        return null;
    }

    static bool IsApplicable(FunctionSymbol function, List<Argument> arguments) =>
        function.Parameters.Zip(arguments).All(pair => pair.First.RefKind == RefKind.Ref
            ? pair.Second.IsRef && pair.Second.Expression.Type == pair.First.Type
            : !pair.Second.IsRef && CanConvert(pair.Second.Expression, pair.First.Type));

    static (Location Location, string Message)? ExplainRefMismatch(FunctionSymbol function, List<Argument> arguments)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            var parameter = function.Parameters[i];
            var argument = arguments[i];
            if (parameter.RefKind == RefKind.Ref && !argument.IsRef)
                return (argument.Location, $"argument {i + 1} must be passed with 'ref'");
            if (parameter.RefKind != RefKind.Ref && argument.IsRef)
                return (argument.Location, $"argument {i + 1} must not be passed with 'ref'");
            if (parameter.RefKind == RefKind.Ref && argument.Expression.Type != parameter.Type)
                return (argument.Location, $"a 'ref' argument must have exactly type '{parameter.Type}', not '{argument.Expression.Type}'");
        }
        return null;
    }

    static bool IsMoreSpecific(FunctionSymbol f, FunctionSymbol g) =>
        f.Parameters.Zip(g.Parameters).All(pair => Conversions.IsImplicit(pair.First.Type, pair.Second.Type));

    BoundExpression BindInitializer(InitializerExpr e)
    {
        var (typeName, arguments) = e.Target switch
        {
            NameExpr n => (n, new List<Argument>()),
            CallExpr { Callee: NameExpr n } call => (n, BindArguments(call.Arguments)),
            _ => throw new InvalidOperationException("the parser only produces initializers on names and calls"),
        };
        if (AsTypeName(typeName) is not { } type)
            return ErrorExpression(typeName.Location, $"'{typeName.Name}' is not a struct; '{{ ... }}' initializers can only follow a struct name");
        if (arguments.Any(a => a.Expression.Type == VplType.Error))
            return new BoundErrorExpression(e.Location);
        return BindConstruction(e.Location, type, arguments, e.Fields);
    }

    BoundExpression BindConstruction(Location location, StructSymbol type, List<Argument> arguments, IReadOnlyList<FieldInit> fieldInits)
    {
        if (arguments.Any(a => a.Expression.Type == VplType.Error))
            return new BoundErrorExpression(location);

        var constructor = Resolve(location, $"constructor of '{type.Name}'", [.. type.Constructors, type.DefaultConstructor], arguments);
        if (constructor is null)
            return new BoundErrorExpression(location);
        CheckAccessible(location, type, constructor.IsPrivate, $"constructor '{constructor.Signature}'");

        var initializers = new List<BoundFieldInitializer>();
        foreach (var init in fieldInits)
        {
            var value = BindExpression(init.Value);
            if (type.FindField(init.Name) is not { } field)
            {
                Error(init.Location, $"'{type.Name}' has no field '{init.Name}'");
                continue;
            }
            if (initializers.Any(i => i.Field == field))
            {
                Error(init.Location, $"field '{init.Name}' is initialized more than once");
                continue;
            }
            CheckAccessible(init.Location, type, field.IsPrivate, $"field '{type.Name}.{init.Name}'");
            initializers.Add(new BoundFieldInitializer(field, Convert(value, field.Type)));
        }

        return new BoundConstruction(location, type.Type, constructor, ConvertArguments(constructor, arguments), initializers);
    }

    BoundExpression BindCast(CastExpr e)
    {
        var operand = BindExpression(e.Operand);
        var type = BindType(e.Type, allowVoid: false);
        if (operand.Type == VplType.Error || type == VplType.Error)
            return new BoundErrorExpression(e.Location);
        if (operand.Type == type)
            return operand;
        if (!Conversions.IsExplicit(operand.Type, type))
            return ErrorExpression(e.Location, $"cannot convert '{operand.Type}' to '{type}'");
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
            return ErrorExpression(e.Location, $"the branches of '?:' have incompatible types '{whenTrue.Type}' and '{whenFalse.Type}'");
        return new BoundConditional(e.Location, condition, Convert(whenTrue, type), Convert(whenFalse, type), type);
    }
}
