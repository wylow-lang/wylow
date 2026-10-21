using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

public sealed partial class Binder
{
    BoundBlock BindBody(FunctionSymbol function)
    {
        currentFunction = function;
        currentType = function.ContainingType;
        typeParameters = [.. currentType?.TypeParameters ?? [], .. function.TypeParameters];
        hasThis = currentType is not null && !function.IsStatic;
        scope = new Scope(null);
        foreach (var parameter in function.Parameters)
            scope.Add(parameter);

        var body = function.IsConstructor && currentType!.Kind == TypeKind.Class
            ? BindClassConstructorBody(function)
            : BindBlock(function.Body!);

        currentFunction = null;
        currentType = null;
        typeParameters = [];
        hasThis = false;
        scope = null;
        return body;
    }

    /// <summary>Binds a class constructor, whose first statement is always the call to the base class constructor.</summary>
    BoundBlock BindClassConstructorBody(FunctionSymbol constructor)
    {
        var block = constructor.Body!;
        var statements = new List<BoundStatement>();
        var rest = block.Statements;
        if (block.Statements.FirstOrDefault() is ExprStmt { Expression: CallExpr { Callee: SuperExpr } call })
        {
            if (BindSuperCall(call) is { } super)
                statements.Add(super);
            rest = [.. block.Statements.Skip(1)];
        }
        else if (ImplicitSuperCall(currentType!, constructor.Declaration!.NameLocation) is { } super)
        {
            statements.Add(super);
        }

        scope = new Scope(scope);
        statements.AddRange(rest.Select(BindStatement));
        scope = scope.Parent;
        return new BoundBlock(block.Location, statements);
    }

    BoundSuperCall? BindSuperCall(CallExpr call)
    {
        hasThis = false;
        noThisContext = NoThisContext.SuperArguments;
        var arguments = BindArguments(call.Arguments, currentType!.Type.BaseType is { } bindingBase ? ConstructorsOf(bindingBase) : null);
        hasThis = true;
        noThisContext = NoThisContext.Static;

        if (currentType!.Type.BaseType is not { } baseType)
        {
            Error(call.Callee.Location, $"'{currentType.Name}' has no base class, so it cannot call 'super(...)'");
            return null;
        }
        if (arguments.Any(a => a.Expression.Type == VplType.Error))
            return null;
        var constructor = Resolve(call.Location, $"constructor of '{baseType}'", ConstructorsOf(baseType), null, arguments);
        if (constructor is null)
            return null;
        CheckAccessible(call.Location, baseType.Symbol!, constructor.Symbol.IsPrivate, $"constructor '{constructor}'");
        return new BoundSuperCall(call.Location, constructor, ConvertArguments(constructor, arguments));
    }

    /// <summary>The base constructor call of a constructor without <c>super(...)</c>: the base's parameterless constructor.</summary>
    BoundSuperCall? ImplicitSuperCall(TypeSymbol s, Location location)
    {
        if (s.Type.BaseType is not { } baseType)
            return null;
        var constructor = ConstructorsOf(baseType).FirstOrDefault(c => c.Symbol.Parameters.Count == 0);
        if (constructor is null)
        {
            Error(location, $"'{baseType}' has no parameterless constructor, so this constructor must start with 'super(...)'");
            return null;
        }
        var reference = new MethodReference(constructor.Symbol, baseType, []);
        CheckAccessible(location, baseType.Symbol!, reference.Symbol.IsPrivate, $"constructor '{reference}'");
        return new BoundSuperCall(location, reference, []);
    }

    static List<Candidate> ConstructorsOf(VplType type)
    {
        var s = type.Symbol!;
        if (s.Kind == TypeKind.Struct)
            return [.. s.Constructors.Append(s.DefaultConstructor).Select(c => new Candidate(c, type))];
        return [.. (s.Constructors.Count > 0 ? s.Constructors : [s.DefaultConstructor]).Select(c => new Candidate(c, type))];
    }

    BoundFunction BindImplicitConstructor(TypeSymbol s)
    {
        var location = s.Declaration.NameLocation;
        foreach (var field in s.Fields.Where(f => f.RequiresAssignment))
        {
            Error(field.Declaration.NameLocation,
                $"field '{field.Name}' of type '{field.Type}' has no default value; give it an initializer or assign it in a constructor");
        }
        currentType = s;
        var super = ImplicitSuperCall(s, location);
        currentType = null;
        return new BoundFunction(s.DefaultConstructor, new BoundBlock(location, super is null ? [] : [super]));
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
        UnsafeStmt s => BindUnsafe(s),
        VarDeclStmt s => BindVariableDeclaration(s),
        IfStmt s => BindIf(s),
        WhileStmt s => BindWhile(s),
        ForStmt s => BindFor(s),
        ForEachStmt s => BindForEach(s),
        ReturnStmt s => BindReturn(s),
        BreakStmt s => BindJump(s.Location, "break", new BoundBreak(s.Location)),
        ContinueStmt s => BindJump(s.Location, "continue", new BoundContinue(s.Location)),
        ExprStmt s => BindExpressionStatement(s),
        AssignStmt s => BindAssignment(s),
        IncDecStmt s => BindIncDec(s),
        DiscardStmt s => BindDiscard(s),
        SwitchStmt s => BindSwitch(s),
        _ => throw new InvalidOperationException($"unexpected statement {statement.GetType().Name}"),
    };

    static BoundStatement ErrorStatement(Location location) =>
        new BoundExpressionStatement(location, new BoundErrorExpression(location));

    BoundStatement BindUnsafe(UnsafeStmt s)
    {
        unsafeDepth++;
        var body = BindBlock(s.Body);
        unsafeDepth--;
        return body;
    }

    BoundBlock BindBlock(BlockStmt block)
    {
        scope = new Scope(scope);
        var statements = block.Statements.Select(BindStatement).ToList();
        scope = scope.Parent;
        return new BoundBlock(block.Location, statements);
    }

    // Names bound by `is` patterns in a condition are in scope for the whole statement.
    BoundStatement BindIf(IfStmt s)
    {
        scope = new Scope(scope);
        var result = new BoundIf(s.Location, BindCondition(s.Condition), BindBlock(s.Then), s.Else is null ? null : BindStatement(s.Else));
        scope = scope.Parent;
        return result;
    }

    BoundStatement BindVariableDeclaration(VarDeclStmt s)
    {
        if (s.RefKind != RefKind.None)
            return BindRefLocal(s);
        var declared = s.Type is null ? null : BindType(s.Type, allowVoid: false);
        var initializer = s.Initializer is null ? null : BindValue(s.Initializer, declared);
        VplType type;
        if (declared is not null)
        {
            type = declared;
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
        else if (initializer.Type == VplType.Null)
        {
            Error(initializer.Location, "cannot infer a type from 'null'; declare the variable with a nullable type instead of 'var'");
            type = VplType.Error;
        }
        else
        {
            type = initializer.Type;
        }

        if ((type.IsRefStruct || type.NonNull.IsFunction) && initializer is null)
            Error(s.Location, $"a '{type}' variable must be initialized where it is declared, since it cannot be reassigned");
        else if (type.IsRefStruct && initializer is not null && ViewsTemporary(initializer))
            Error(initializer.Location, $"this '{type}' would view a temporary value, which is gone after this statement; store the value in a variable first");
        else if (type.IsRefStruct && initializer is not null && BoundTree.ViewedNullableValue(initializer) is { } root)
            Error(initializer.Location, $"this '{type}' would view inside the nullable '{root.Name}', which assigning it null would destroy; copy the value to a variable first");
        var local = new LocalSymbol(s.Name, type, isParameter: false);
        Declare(local, s.NameLocation);
        return new BoundVariableDeclaration(s.Location, local, initializer);
    }

    /// <summary><c>ref T x = place;</c> or <c>in T x = place;</c>, which name a place for the rest of the scope.</summary>
    BoundStatement BindRefLocal(VarDeclStmt s)
    {
        var keyword = FunctionSymbol.RefPrefix(s.RefKind).Trim();
        var declared = s.Type is null ? null : BindType(s.Type, allowVoid: false);
        var place = s.Initializer is null ? null : BindExpression(s.Initializer, declared);
        var type = declared ?? place?.Type ?? VplType.Error;
        if (place is null)
            Error(s.Location, $"a '{keyword}' variable must be initialized with the variable, field, or element it refers to");
        else if (type != VplType.Error && place.Type != VplType.Error && CheckReference(place, s.RefKind, type, $"a '{keyword}' variable"))
            type = place.Type;
        else
            place = null;

        var local = new LocalSymbol(s.Name, type, isParameter: false, s.RefKind);
        Declare(local, s.NameLocation);
        // Declared even after an error, so later uses aren't reported as unassigned.
        return new BoundVariableDeclaration(s.Location, local, place ?? new BoundErrorExpression(s.Location));
    }

    /// <summary>
    /// Checks that <paramref name="place"/> can be referred to by a <c>ref</c> or <c>in</c> of type
    /// <paramref name="type"/>; <paramref name="what"/> names the reference in messages.
    /// </summary>
    bool CheckReference(BoundExpression place, RefKind refKind, VplType type, string what)
    {
        var keyword = FunctionSymbol.RefPrefix(refKind).Trim();
        if (type.IsReference)
        {
            Error(place.Location, $"'{keyword}' cannot be used with type '{type}'; class objects are already references");
            return false;
        }
        if (type.IsFunction)
        {
            Error(place.Location, $"'{keyword}' cannot be used with type '{type}'; functions are already passed without copying");
            return false;
        }
        if (place.Type != type)
        {
            Error(place.Location, $"{what} must have exactly the type of what it refers to, '{place.Type}', not '{type}'");
            return false;
        }
        if (BoundTree.NullableValueRoot(place) is { } root)
        {
            Error(place.Location, $"{what} cannot refer into the nullable '{root.Name}', since assigning it null would destroy its value; copy the value to a variable first");
            return false;
        }
        // Through such a reference the variable could become null after a null check narrowed it.
        if (refKind == RefKind.Ref && type.IsNullableValue && place is BoundLocalAccess { Local.RefKind: RefKind.None } local)
        {
            Error(place.Location, $"{what} cannot refer to the nullable variable '{local.Local.Name}'; pass it by 'ref' to a function instead");
            return false;
        }
        var (kind, reason) = ClassifyPlace(place);
        if (kind == PlaceKind.Temporary)
            Error(place.Location, $"{what} must refer to a variable, field, or element, not a temporary value");
        else if (kind == PlaceKind.ReadOnly && refKind == RefKind.Ref)
            Error(place.Location, $"cannot take a 'ref' to '{TextOf(place.Location)}' because {reason}; use 'in' instead");
        else
            return true;
        return false;
    }

    BoundStatement BindWhile(WhileStmt s)
    {
        scope = new Scope(scope);
        var condition = BindCondition(s.Condition);
        loopDepth++;
        var body = BindBlock(s.Body);
        loopDepth--;
        scope = scope.Parent;
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
        // The update runs inside the C++ `for` header, where an early return can't be placed.
        var updateValue = update switch
        {
            BoundAssignment assignment => assignment.Value,
            BoundExpressionStatement statement => statement.Expression,
            _ => null,
        };
        if (updateValue is not null && IsTry(updateValue))
            Error(s.Update!.Location, "'try' cannot be used in a 'for' update");
        scope = scope.Parent;
        return new BoundFor(s.Location, initializer, condition, update, body);
    }

    BoundStatement BindReturn(ReturnStmt s)
    {
        var function = currentFunction!;
        if (s.Value is null)
        {
            if (function.ReturnType != VplType.Void && function.ReturnType != VplType.Error)
                Error(s.Location, $"{Describe(function)} must return a value of type '{function.ReturnType}'");
            return new BoundReturn(s.Location, null);
        }

        if (function.ReturnRefKind != RefKind.None || s.IsRef)
            return BindReturnReference(s, function);
        if (inferring is (var lambda, var values) && lambda == function)
        {
            var returned = BindValue(s.Value, null);
            values.Add(returned);
            return new BoundReturn(s.Location, returned);
        }
        var value = BindValue(s.Value, function.IsConstructor || function.IsDestructor ? null : function.ReturnType);
        if (function.IsConstructor || function.IsDestructor)
        {
            Error(s.Value.Location, function.IsConstructor ? "constructors cannot return a value" : "destructors cannot return a value");
            return new BoundReturn(s.Location, null);
        }
        if (function.ReturnType == VplType.Void)
        {
            Error(s.Value.Location, $"{Describe(function)} returns 'void' and cannot return a value");
            return new BoundReturn(s.Location, null);
        }
        var converted = Convert(value, function.ReturnType);
        if (function.ReturnType.IsRefStruct && converted.Type != VplType.Error && !IsThisDerived(converted))
            Error(s.Value.Location, $"a returned '{function.ReturnType}' must view the method's receiver: fields or elements of 'this', or a span returned by its methods");
        return new BoundReturn(s.Location, converted);
    }

    /// <summary>
    /// <c>return ref place;</c>. The caller treats the reference as borrowing the receiver, so it
    /// must point into the receiver's own storage: <c>this</c>, its fields and elements, or, in
    /// unsafe code, a pointer's target.
    /// </summary>
    BoundStatement BindReturnReference(ReturnStmt s, FunctionSymbol function)
    {
        var keyword = FunctionSymbol.RefPrefix(function.ReturnRefKind).Trim();
        var place = BindExpression(s.Value!, function.ReturnType);
        if (function.ReturnRefKind == RefKind.None)
            Error(s.Location, "'return ref' can only be used in a method that returns 'ref' or 'in'");
        else if (!s.IsRef)
            Error(s.Location, $"'{function.Name}' returns a reference; return a place with 'return ref'");
        // A return type that cannot be a reference was reported with the method.
        else if (function.ReturnType.IsReference || function.ReturnType == VplType.Void)
            return new BoundReturn(s.Location, null);
        else if (place.Type != VplType.Error && CheckReference(place, function.ReturnRefKind, function.ReturnType, $"a returned '{keyword}'"))
        {
            if (IsThisRooted(place))
                return new BoundReturn(s.Location, place);
            Error(s.Value!.Location, $"a returned '{keyword}' must point into the method's receiver: a field or element of 'this'");
        }
        return new BoundReturn(s.Location, null);
    }

    /// <summary>Whether a place lies in <c>this</c> itself, not in another object it refers to.</summary>
    static bool IsThisRooted(BoundExpression place) => place switch
    {
        BoundThis => true,
        BoundFieldAccess field => field.Receiver is BoundThis || (!field.Receiver.Type.IsReference && IsThisRooted(field.Receiver)),
        BoundIndex { Target.Type.IsPointer: true } => true,
        BoundIndex index => IsThisRooted(index.Target),
        BoundCall { Method.Symbol.ReturnRefKind: not RefKind.None, Receiver: { } receiver } =>
            receiver is BoundThis || (!receiver.Type.IsReference && IsThisRooted(receiver)),
        _ => false,
    };

    BoundStatement BindJump(Location location, string keyword, BoundStatement jump)
    {
        if (loopDepth == 0)
            Error(location, $"'{keyword}' can only be used inside a loop");
        return jump;
    }

    BoundStatement BindExpressionStatement(ExprStmt s)
    {
        var expression = BindValue(s.Expression, null);
        if (expression is not (BoundCall or BoundInvocation or BoundNew or BoundTry or BoundConditionalAccess { WhenNotNull: BoundCall }) && expression.Type != VplType.Error)
            Error(s.Location, "only calls can be used as statements");
        else if (expression is BoundCall or BoundInvocation && IsResult(expression.Type))
        {
            var name = expression is BoundCall call ? call.Method.Symbol.Name : TextOf(((BoundInvocation)expression).Callee.Location);
            Error(s.Location, $"the 'Result' returned by '{name}' is not used; handle it, or discard it with '_ = ...'");
        }
        return new BoundExpressionStatement(s.Location, expression);
    }

    BoundStatement BindDiscard(DiscardStmt s)
    {
        var value = BindValue(s.Value, null);
        if (value.Type == VplType.Void)
            Error(s.Value.Location, "there is no value to discard; call it without '_ ='");
        return new BoundExpressionStatement(s.Location, value, IsDiscard: true);
    }

    BoundStatement BindAssignment(AssignStmt s)
    {
        var target = BindExpression(s.Target);
        var value = s.Operator == TokenKind.Assign ? BindValue(s.Value, target.Type) : BindExpression(s.Value);
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
        var operand = target.Type.IsNullableValue ? AssumeNonNull(target) : target;
        if (!operand.Type.IsNumeric)
        {
            Error(s.Location, $"'{(s.IsIncrement ? "++" : "--")}' requires a numeric variable{NullableOperandHint(operand)}");
            return ErrorStatement(s.Location);
        }

        object one = operand.Type == VplType.Int ? (object)1
            : operand.Type == VplType.Long ? (object)1L
            : operand.Type == VplType.Float ? (object)1f
            : (object)1d;
        var value = BindBinaryOperation(
            s.Location, operand, s.IsIncrement ? TokenKind.Plus : TokenKind.Minus, new BoundLiteral(s.Location, operand.Type, one));
        return new BoundAssignment(s.Location, target, Convert(value, target.Type));
    }

    (PlaceKind Kind, string Reason) ClassifyPlace(BoundExpression expression) => expression switch
    {
        BoundLocalAccess { Local.RefKind: RefKind.In } access =>
            (PlaceKind.ReadOnly, $"'{access.Local.Name}' is an 'in' {(access.Local.IsParameter ? "parameter" : "variable")}"),
        BoundLocalAccess => (PlaceKind.Mutable, ""),
        BoundThis { Type.IsReference: true } => (PlaceKind.Temporary, ""),
        BoundThis when currentFunction is { IsReadOnly: true } => (PlaceKind.ReadOnly, "'this' is read-only in a readonly method"),
        BoundThis => (PlaceKind.Mutable, ""),
        // Fields of an object live on the heap, however the reference to it was obtained.
        BoundFieldAccess { Receiver.Type.IsReference: true } => (PlaceKind.Mutable, ""),
        BoundFieldAccess field => ClassifyPlace(field.Receiver),
        BoundIndex { Target.Type.IsPointer: true } => (PlaceKind.Mutable, ""),
        BoundIndex index => ClassifyPlace(index.Target),
        BoundCall { Method.Symbol.ReturnRefKind: not RefKind.None } call => ClassifyReturnedReference(call),
        _ => (PlaceKind.Temporary, ""),
    };

    /// <summary>A returned reference points into the receiver, so a temporary struct receiver makes it temporary too.</summary>
    (PlaceKind Kind, string Reason) ClassifyReturnedReference(BoundCall call)
    {
        if (call.Receiver is { Type.IsAggregate: true, Type.IsRefStruct: false } receiver && ClassifyPlace(receiver).Kind == PlaceKind.Temporary)
            return (PlaceKind.Temporary, "");
        if (call.Method.Symbol.ReturnRefKind == RefKind.In)
            return (PlaceKind.ReadOnly, $"'{call.Method.Symbol.Signature}' returns 'in'");
        return (PlaceKind.Mutable, "");
    }

    bool CheckAssignable(BoundExpression target)
    {
        if (target.Type.IsRefStruct)
        {
            Error(target.Location, $"'{TextOf(target.Location)}' cannot be reassigned: a '{target.Type}' views what it was initialized with for its whole scope; declare a new variable instead");
            return false;
        }
        // A lambda in it may use the function's locals by reference, so it must not outlive any of them.
        if (target is BoundLocalAccess { Local.Type.NonNull.IsFunction: true } function)
        {
            Error(target.Location, $"'{function.Local.Name}' cannot be reassigned: a function-typed {(function.Local.IsParameter ? "parameter" : "variable")} keeps the function it was given; declare a new variable instead");
            return false;
        }
        var (kind, reason) = ClassifyPlace(target);
        switch (kind)
        {
            case PlaceKind.Temporary:
                Error(target.Location, target switch
                {
                    BoundFieldAccess => "cannot assign to a field of a temporary value",
                    BoundIndex => "cannot assign to an element of a temporary array",
                    BoundCall { Method.Symbol.IsProperty: true } call => $"cannot assign to property '{call.Method.Symbol.Name}', which has no 'set'",
                    _ => "cannot assign to this expression",
                });
                return false;
            case PlaceKind.ReadOnly:
                Error(target.Location, $"cannot assign to '{TextOf(target.Location)}' because {reason}");
                return false;
            default:
                return true;
        }
    }
}
