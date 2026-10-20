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
    IReadOnlyList<TypeParameterSymbol> typeParameters = [];
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

    List<TypeParameterSymbol> DeclareTypeParameters(IReadOnlyList<TypeParameterSyntax> syntax, IReadOnlyList<TypeParameterSymbol> outer)
    {
        var result = new List<TypeParameterSymbol>();
        foreach (var parameter in syntax)
        {
            if (result.Any(p => p.Name == parameter.Name))
                Error(parameter.Location, $"duplicate type parameter '{parameter.Name}'");
            else if (outer.Any(p => p.Name == parameter.Name))
                Error(parameter.Location, $"type parameter '{parameter.Name}' hides the struct's type parameter of the same name");
            result.Add(new TypeParameterSymbol(parameter.Name));
        }
        return result;
    }

    StructSymbol? DeclareStruct(StructDecl decl)
    {
        if (structs.ContainsKey(decl.Name) || BuiltinType(decl.Name) is not null)
        {
            Error(decl.NameLocation, $"'{decl.Name}' is already defined");
            return null;
        }
        var symbol = new StructSymbol(decl, DeclareTypeParameters(decl.TypeParameters, []));
        structs[decl.Name] = symbol;
        return symbol;
    }

    FunctionSymbol DeclareFunction(FunctionDecl decl)
    {
        var ownTypeParameters = DeclareTypeParameters(decl.TypeParameters, []);
        typeParameters = ownTypeParameters;
        var function = new FunctionSymbol(decl.Name, BindParameters(decl.Parameters), BindType(decl.ReturnType!, allowVoid: true))
        {
            Declaration = decl,
            TypeParameters = ownTypeParameters,
        };
        typeParameters = [];

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

    /// <summary>Signatures match when parameters match, with each function's own type parameters compared by position.</summary>
    static string SignatureKey(FunctionSymbol function)
    {
        string Key(VplType type) =>
            type.TypeParameter is { } p && function.TypeParameters.Contains(p) ? $"#{function.TypeParameters.ToList().IndexOf(p)}"
            : type.Struct is { } s && type.TypeArguments.Count > 0 ? $"{s.Name}<{string.Join(",", type.TypeArguments.Select(Key))}>"
            : type.Name;
        return $"{function.TypeParameters.Count}|{string.Join(",", function.Parameters.Select(p => p.RefKind + Key(p.Type)))}";
    }

    static FunctionSymbol? FindSameSignature(IEnumerable<FunctionSymbol>? candidates, FunctionSymbol function) =>
        candidates?.FirstOrDefault(f => SignatureKey(f) == SignatureKey(function));

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
        typeParameters = s.TypeParameters;
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
            var ownTypeParameters = DeclareTypeParameters(decl.TypeParameters, s.TypeParameters);
            typeParameters = [.. s.TypeParameters, .. ownTypeParameters];
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
                TypeParameters = ownTypeParameters,
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
        typeParameters = [];
    }

    /// <summary>
    /// A struct may not contain itself by value, directly or through type arguments
    /// (<c>struct Rec { Wrap&lt;Rec&gt; w; }</c>), and its field types may not grow forever.
    /// </summary>
    void CheckStructCycles(List<StructSymbol> all)
    {
        const int maxDepth = 32;
        var reported = new HashSet<StructSymbol>();
        var acyclic = new HashSet<VplType>();

        string? FindCycle(VplType type, List<VplType> path)
        {
            if (acyclic.Contains(type))
                return null;
            int index = path.IndexOf(type);
            if (index >= 0)
            {
                var cycle = path.Skip(index).Append(type).ToList();
                foreach (var member in cycle)
                    reported.Add(member.Struct!);
                return $"contains itself ({string.Join(" -> ", cycle)}); a struct cannot hold itself by value";
            }
            if (path.Count >= maxDepth)
            {
                foreach (var member in path)
                    reported.Add(member.Struct!);
                return "has field types that nest without end";
            }

            path.Add(type);
            var map = TypeMap.For(type);
            foreach (var field in type.Struct!.Fields)
            {
                var fieldType = map.Apply(field.Type);
                if (fieldType.IsStruct && FindCycle(fieldType, path) is { } problem)
                    return problem;
            }
            path.RemoveAt(path.Count - 1);
            acyclic.Add(type);
            return null;
        }

        foreach (var s in all)
        {
            if (!reported.Contains(s) && FindCycle(s.Type, []) is { } problem)
                Error(s.Declaration.NameLocation, $"struct '{s.Name}' {problem}");
        }
    }

    void BindFieldInitializers(StructSymbol s)
    {
        foreach (var field in s.Fields)
        {
            if (field.Declaration.Initializer is not { } initializer)
                continue;
            currentStruct = s;
            typeParameters = s.TypeParameters;
            inFieldInitializer = true;
            scope = new Scope(null);
            field.Initializer = Convert(BindExpression(initializer), field.Type);
            currentStruct = null;
            typeParameters = [];
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
            if (candidate.Parameters.Count != 0 || candidate.IsGeneric)
                Error(candidate.Declaration!.NameLocation, "'main' must not take parameters or type parameters");
            else if (candidate.ReturnType != VplType.Void && candidate.ReturnType != VplType.Int)
                Error(candidate.Declaration!.NameLocation, "'main' must return 'void' or 'int'");
            else
                main = candidate;
        }
        return main;
    }

    // ---- Types ----

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

    TypeParameterSymbol? LookupTypeParameter(string name) => typeParameters.LastOrDefault(p => p.Name == name);

    VplType BindType(TypeSyntax syntax, bool allowVoid)
    {
        var type = BindTypeName(syntax.Name, syntax.TypeArguments, syntax.Location);
        if (type == VplType.Void && !allowVoid)
        {
            Error(syntax.Location, "'void' cannot be used here");
            return VplType.Error;
        }
        return type;
    }

    VplType BindTypeName(string name, IReadOnlyList<TypeSyntax> typeArgumentSyntax, Location location)
    {
        var arguments = typeArgumentSyntax.Select(a => BindType(a, allowVoid: false)).ToList();
        if (arguments.Contains(VplType.Error))
            return VplType.Error;

        if (LookupTypeParameter(name) is { } parameter)
            return arguments.Count == 0 ? parameter.Type : NotGeneric(name, location);
        if (BuiltinType(name) is { } builtin)
            return arguments.Count == 0 ? builtin : NotGeneric(name, location);
        if (structs.TryGetValue(name, out var s))
            return Construct(s, arguments, location);

        Error(location, $"unknown type '{name}'");
        return VplType.Error;
    }

    VplType NotGeneric(string name, Location location)
    {
        Error(location, $"'{name}' is not generic");
        return VplType.Error;
    }

    VplType Construct(StructSymbol s, IReadOnlyList<VplType> arguments, Location location)
    {
        if (arguments.Count == s.TypeParameters.Count)
            return s.Construct(arguments);
        if (!s.IsGeneric)
            return NotGeneric(s.Name, location);
        int count = s.TypeParameters.Count;
        Error(location, $"'{s.Name}' requires {count} type argument{(count == 1 ? "" : "s")}");
        return VplType.Error;
    }

    IReadOnlyList<VplType>? BindTypeArguments(IReadOnlyList<TypeSyntax> syntax) =>
        syntax.Count == 0 ? null : [.. syntax.Select(a => BindType(a, allowVoid: false))];

    // ---- Bodies and statements ----

    BoundBlock BindBody(FunctionSymbol function)
    {
        currentFunction = function;
        currentStruct = function.ContainingStruct;
        typeParameters = [.. currentStruct?.TypeParameters ?? [], .. function.TypeParameters];
        hasThis = currentStruct is not null && !function.IsStatic;
        scope = new Scope(null);
        foreach (var parameter in function.Parameters)
            scope.Add(parameter);

        var body = BindBlock(function.Declaration!.Body);

        currentFunction = null;
        currentStruct = null;
        typeParameters = [];
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

    bool IsValueName(string name) => scope?.Lookup(name) is not null || currentStruct?.FindField(name) is not null;

    /// <summary>
    /// The struct type named by an expression like <c>Vec3</c> or <c>Pair&lt;int, float&gt;</c>, or null
    /// when the expression is not a type name. A local or field hides a struct of the same name.
    /// </summary>
    VplType? AsTypeName(Expr expression)
    {
        if (expression is not NameExpr name || IsValueName(name.Name) || !structs.TryGetValue(name.Name, out var s))
            return null;
        return BindTypeName(s.Name, name.TypeArguments, name.Location);
    }

    BoundExpression BindName(NameExpr e)
    {
        if (e.TypeArguments.Count > 0)
            return ErrorExpression(e.Location, "type arguments can only be used in a call or construction");

        if (scope!.Lookup(e.Name) is { } local)
            return new BoundLocalAccess(e.Location, local);

        if (currentStruct?.FindField(e.Name) is { } field)
        {
            if (!hasThis)
                return ErrorExpression(e.Location, $"'{e.Name}' is an instance field; {NoInstanceReason}");
            return new BoundFieldAccess(e.Location, new BoundThis(e.Location, currentStruct.Type), field, field.Type);
        }

        if (structs.ContainsKey(e.Name) || LookupTypeParameter(e.Name) is not null)
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
        if (e.TypeArguments.Count > 0)
            return ErrorExpression(e.NameLocation, "type arguments can only be used in a call");

        if (AsTypeName(e.Target) is { } typeName)
        {
            if (typeName == VplType.Error)
                return new BoundErrorExpression(e.Location);
            var s = typeName.Struct!;
            return ErrorExpression(e.NameLocation, s.Methods.ContainsKey(e.Name)
                ? $"'{s.Name}.{e.Name}' is a method; call it with '{e.Name}(...)'"
                : $"'{s.Name}' has no static member '{e.Name}'");
        }

        var target = BindExpression(e.Target);
        if (target.Type == VplType.Error)
            return new BoundErrorExpression(e.Location);
        if (target.Type.Struct is not { } owner)
            return ErrorExpression(e.NameLocation, $"'{target.Type}' has no member '{e.Name}'");

        if (owner.FindField(e.Name) is not { } field)
        {
            return ErrorExpression(e.NameLocation, owner.Methods.ContainsKey(e.Name)
                ? $"'{owner.Name}.{e.Name}' is a method; call it with '{e.Name}(...)'"
                : $"'{owner.Name}' has no field '{e.Name}'");
        }
        CheckAccessible(e.NameLocation, owner, field.IsPrivate, $"field '{owner.Name}.{e.Name}'");
        return new BoundFieldAccess(e.Location, target, field, TypeMap.For(target.Type).Apply(field.Type));
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
            if (Conversions.FindNonEquatableField(left.Type) is { } field)
            {
                return ErrorExpression(location,
                    $"operator '{symbol}' cannot be applied to '{left.Type}' because its field '{field.Name}' of type '{TypeMap.For(left.Type).Apply(field.Type)}' does not support '=='");
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
        if (IsValueName(name.Name))
            return ErrorExpression(name.Location, $"'{name.Name}' is a variable, not a function");
        if (LookupTypeParameter(name.Name) is not null)
            return ErrorExpression(name.Location, $"'{name.Name}' is a type parameter and cannot be constructed");

        var typeArguments = BindTypeArguments(name.TypeArguments);
        if (arguments.Any(a => a.Expression.Type == VplType.Error) || typeArguments?.Contains(VplType.Error) == true)
            return new BoundErrorExpression(e.Location);

        if (currentStruct is { } s && s.Methods.TryGetValue(name.Name, out var methods))
        {
            var method = Resolve(e.Location, $"overload of '{name.Name}'", methods, s.Type, typeArguments, arguments);
            if (method is null)
                return new BoundErrorExpression(e.Location);
            if (method.Symbol.IsStatic)
                return MakeCall(e.Location, method, null, arguments);
            if (!hasThis)
                return ErrorExpression(name.Location, $"'{name.Name}' is an instance method; {NoInstanceReason}");
            return MakeCall(e.Location, method, new BoundThis(name.Location, s.Type), arguments);
        }

        if (structs.TryGetValue(name.Name, out var type))
        {
            var constructed = Construct(type, typeArguments ?? [], name.Location);
            return constructed == VplType.Error
                ? new BoundErrorExpression(e.Location)
                : BindConstruction(e.Location, constructed, arguments, []);
        }

        if (!functions.TryGetValue(name.Name, out var overloads))
            return ErrorExpression(name.Location, $"function '{name.Name}' is not defined");
        var function = Resolve(e.Location, $"overload of '{name.Name}'", overloads, null, typeArguments, arguments);
        return function is null ? new BoundErrorExpression(e.Location) : MakeCall(e.Location, function, null, arguments);
    }

    BoundExpression BindMemberCall(CallExpr e, MemberAccessExpr member, List<Argument> arguments)
    {
        var typeArguments = BindTypeArguments(member.TypeArguments);
        if (typeArguments?.Contains(VplType.Error) == true)
            return new BoundErrorExpression(e.Location);

        if (AsTypeName(member.Target) is { } type)
        {
            if (type == VplType.Error || arguments.Any(a => a.Expression.Type == VplType.Error))
                return new BoundErrorExpression(e.Location);
            var s = type.Struct!;
            if (!s.Methods.TryGetValue(member.Name, out var staticCandidates))
                return ErrorExpression(member.NameLocation, $"'{s.Name}' has no method '{member.Name}'");
            var staticMethod = Resolve(e.Location, $"overload of '{s.Name}.{member.Name}'", staticCandidates, type, typeArguments, arguments);
            if (staticMethod is null)
                return new BoundErrorExpression(e.Location);
            if (!staticMethod.Symbol.IsStatic)
            {
                return ErrorExpression(member.NameLocation,
                    $"'{s.Name}.{member.Name}' is an instance method; call it on a value of type '{type}'");
            }
            CheckAccessible(member.NameLocation, s, staticMethod.Symbol.IsPrivate, $"method '{s.Name}.{member.Name}'");
            return MakeCall(e.Location, staticMethod, null, arguments);
        }

        var receiver = BindExpression(member.Target);
        if (receiver.Type == VplType.Error || arguments.Any(a => a.Expression.Type == VplType.Error))
            return new BoundErrorExpression(e.Location);
        if (receiver.Type.Struct is not { } owner)
            return ErrorExpression(member.NameLocation, $"'{receiver.Type}' has no method '{member.Name}'");
        if (!owner.Methods.TryGetValue(member.Name, out var candidates))
        {
            return ErrorExpression(member.NameLocation, owner.FindField(member.Name) is not null
                ? $"'{owner.Name}.{member.Name}' is a field, not a method"
                : $"'{owner.Name}' has no method '{member.Name}'");
        }

        var method = Resolve(e.Location, $"overload of '{owner.Name}.{member.Name}'", candidates, receiver.Type, typeArguments, arguments);
        if (method is null)
            return new BoundErrorExpression(e.Location);
        if (method.Symbol.IsStatic)
        {
            return ErrorExpression(member.NameLocation,
                $"static method '{member.Name}' must be called through the type: '{owner.Name}.{member.Name}(...)'");
        }
        CheckAccessible(member.NameLocation, owner, method.Symbol.IsPrivate, $"method '{owner.Name}.{member.Name}'");
        return MakeCall(e.Location, method, receiver, arguments);
    }

    BoundExpression MakeCall(Location location, MethodReference method, BoundExpression? receiver, List<Argument> arguments)
    {
        if (receiver is not null && !method.Symbol.IsReadOnly)
        {
            var (kind, reason) = ClassifyPlace(receiver);
            if (kind == PlaceKind.ReadOnly)
            {
                var what = receiver is BoundThis ? "'this'" : $"'{TextOf(receiver.Location)}'";
                return ErrorExpression(location,
                    $"cannot call '{method.Symbol.Name}' on {what} because {reason}; only 'readonly' methods can be called on it");
            }
        }
        return new BoundCall(location, method, receiver, ConvertArguments(method, arguments));
    }

    List<BoundExpression> ConvertArguments(MethodReference method, List<Argument> arguments)
    {
        var result = new List<BoundExpression>();
        for (int i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (method.ParameterRefKind(i) != RefKind.Ref)
            {
                result.Add(Convert(argument.Expression, method.ParameterType(i)));
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

    /// <summary>
    /// Picks the overload to call. Type arguments are inferred from the arguments unless given explicitly.
    /// Among equally good candidates a non-generic one wins.
    /// </summary>
    MethodReference? Resolve(
        Location location,
        string description,
        IReadOnlyList<FunctionSymbol> candidates,
        VplType? owner,
        IReadOnlyList<VplType>? typeArguments,
        List<Argument> arguments)
    {
        var sameArity = candidates
            .Where(f => f.Parameters.Count == arguments.Count && (typeArguments is null || f.TypeParameters.Count == typeArguments.Count))
            .ToList();
        if (sameArity.Count == 0)
        {
            if (typeArguments is not null && candidates.Any(f => f.Parameters.Count == arguments.Count))
                Error(location, $"no {description} takes {typeArguments.Count} type argument{(typeArguments.Count == 1 ? "" : "s")}");
            else
                Error(location, $"no {description} takes {arguments.Count} argument{(arguments.Count == 1 ? "" : "s")}");
            return null;
        }

        var instantiated = new List<MethodReference>();
        string? inferenceError = null;
        foreach (var candidate in sameArity)
        {
            var inferred = candidate.IsGeneric ? typeArguments ?? InferTypeArguments(candidate, owner, arguments, out inferenceError) : [];
            if (inferred is not null)
                instantiated.Add(new MethodReference(candidate, owner, inferred));
        }
        if (instantiated.Count == 0)
        {
            Error(location, inferenceError!);
            return null;
        }

        if (sameArity.Count == 1 && ExplainRefMismatch(instantiated[0], arguments) is { } mismatch)
        {
            Error(mismatch.Location, mismatch.Message);
            return null;
        }

        var applicable = instantiated.Where(m => IsApplicable(m, arguments)).ToList();
        if (applicable.Count == 0)
        {
            var types = string.Join(", ", arguments.Select(a => (a.IsRef ? "ref " : "") + a.Expression.Type));
            Error(location, $"no {description} accepts ({types})");
            return null;
        }

        var exact = applicable
            .Where(m => Enumerable.Range(0, arguments.Count).All(i => m.ParameterType(i) == arguments[i].Expression.Type))
            .ToList();
        if (exact.Count > 0)
            return exact.FirstOrDefault(m => !m.Symbol.IsGeneric) ?? exact[0];

        // Java's rule: pick the overload whose parameters all convert to every other candidate's parameters.
        var mostSpecific = applicable.Where(m => applicable.All(n => n == m || IsMoreSpecific(m, n))).ToList();
        if (mostSpecific.Count > 1 && mostSpecific.Count(m => !m.Symbol.IsGeneric) == 1)
            mostSpecific = [.. mostSpecific.Where(m => !m.Symbol.IsGeneric)];
        if (mostSpecific.Count == 1)
            return mostSpecific[0];

        Error(location, $"call is ambiguous between {string.Join(" and ", applicable.Select(m => $"'{m}'"))}");
        return null;
    }

    /// <summary>
    /// Infers each type parameter from the arguments in its positions. When positions disagree, the
    /// type that every argument converts to wins, so <c>max(1, 2L)</c> infers <c>long</c>.
    /// </summary>
    IReadOnlyList<VplType>? InferTypeArguments(FunctionSymbol function, VplType? owner, List<Argument> arguments, out string? error)
    {
        var ownerMap = owner is null ? TypeMap.Empty : TypeMap.For(owner);
        var bindings = function.TypeParameters.ToDictionary(p => p, _ => new List<(BoundExpression? Expression, VplType Type)>());

        void Unify(VplType parameter, BoundExpression? argument, VplType argumentType)
        {
            if (parameter.TypeParameter is { } p && bindings.TryGetValue(p, out var found))
            {
                found.Add((argument, argumentType));
            }
            else if (parameter.Struct is not null && parameter.Struct == argumentType.Struct)
            {
                // Inside a type argument there are no conversions, so these must match exactly.
                for (int i = 0; i < parameter.TypeArguments.Count; i++)
                    Unify(parameter.TypeArguments[i], null, argumentType.TypeArguments[i]);
            }
        }

        for (int i = 0; i < arguments.Count; i++)
            Unify(ownerMap.Apply(function.Parameters[i].Type), arguments[i].Expression, arguments[i].Expression.Type);

        var result = new List<VplType>();
        foreach (var parameter in function.TypeParameters)
        {
            var found = bindings[parameter];
            if (found.Count == 0)
            {
                error = $"cannot infer type argument '{parameter.Name}' of '{function.Name}'; specify it explicitly, as in '{function.Name}<...>(...)'";
                return null;
            }
            var chosen = found.Select(b => b.Type).Distinct().FirstOrDefault(candidate =>
                found.All(b => b.Expression is not null ? CanConvert(b.Expression, candidate) : b.Type == candidate));
            if (chosen is null)
            {
                error = $"cannot infer type argument '{parameter.Name}' of '{function.Name}': it could be {string.Join(" or ", found.Select(b => $"'{b.Type}'").Distinct())}";
                return null;
            }
            result.Add(chosen);
        }
        error = null;
        return result;
    }

    static bool IsApplicable(MethodReference method, List<Argument> arguments) =>
        Enumerable.Range(0, arguments.Count).All(i => method.ParameterRefKind(i) == RefKind.Ref
            ? arguments[i].IsRef && arguments[i].Expression.Type == method.ParameterType(i)
            : !arguments[i].IsRef && CanConvert(arguments[i].Expression, method.ParameterType(i)));

    static (Location Location, string Message)? ExplainRefMismatch(MethodReference method, List<Argument> arguments)
    {
        for (int i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            bool isRefParameter = method.ParameterRefKind(i) == RefKind.Ref;
            if (isRefParameter && !argument.IsRef)
                return (argument.Location, $"argument {i + 1} must be passed with 'ref'");
            if (!isRefParameter && argument.IsRef)
                return (argument.Location, $"argument {i + 1} must not be passed with 'ref'");
            if (isRefParameter && argument.Expression.Type != method.ParameterType(i))
                return (argument.Location, $"a 'ref' argument must have exactly type '{method.ParameterType(i)}', not '{argument.Expression.Type}'");
        }
        return null;
    }

    static bool IsMoreSpecific(MethodReference m, MethodReference n) =>
        Enumerable.Range(0, m.Symbol.Parameters.Count).All(i => Conversions.IsImplicit(m.ParameterType(i), n.ParameterType(i)));

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
        if (type == VplType.Error)
            return new BoundErrorExpression(e.Location);
        return BindConstruction(e.Location, type, arguments, e.Fields);
    }

    BoundExpression BindConstruction(Location location, VplType type, List<Argument> arguments, IReadOnlyList<FieldInit> fieldInits)
    {
        if (arguments.Any(a => a.Expression.Type == VplType.Error))
            return new BoundErrorExpression(location);

        var s = type.Struct!;
        var constructor = Resolve(location, $"constructor of '{type}'", [.. s.Constructors, s.DefaultConstructor], type, null, arguments);
        if (constructor is null)
            return new BoundErrorExpression(location);
        CheckAccessible(location, s, constructor.Symbol.IsPrivate, $"constructor '{constructor}'");

        var map = TypeMap.For(type);
        var initializers = new List<BoundFieldInitializer>();
        foreach (var init in fieldInits)
        {
            var value = BindExpression(init.Value);
            if (s.FindField(init.Name) is not { } field)
            {
                Error(init.Location, $"'{s.Name}' has no field '{init.Name}'");
                continue;
            }
            if (initializers.Any(i => i.Field == field))
            {
                Error(init.Location, $"field '{init.Name}' is initialized more than once");
                continue;
            }
            CheckAccessible(init.Location, s, field.IsPrivate, $"field '{s.Name}.{init.Name}'");
            initializers.Add(new BoundFieldInitializer(field, Convert(value, map.Apply(field.Type))));
        }

        if (constructor.Symbol.IsImplicit)
        {
            var missing = s.Fields.FirstOrDefault(f =>
                f.Declaration.Initializer is null
                && initializers.All(i => i.Field != f)
                && !Conversions.IsDefaultable(map.Apply(f.Type)));
            if (missing is not null)
            {
                return ErrorExpression(location,
                    $"'{type}' has no default value because field '{missing.Name}' has type '{map.Apply(missing.Type)}'; initialize it explicitly, as in '{s.Name}{{ {missing.Name} = ... }}'");
            }
        }

        return new BoundConstruction(location, type, constructor, ConvertArguments(constructor, arguments), initializers);
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
