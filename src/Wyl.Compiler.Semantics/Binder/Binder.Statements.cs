using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

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
        {
            parameter.Owner = function;
            scope.Add(parameter);
        }

        var body = function.IsConstructor && currentType!.Kind == TypeKind.Class
            ? BindClassConstructorBody(function)
            : BindBlock(function.Body!);
        CheckEscapingCaptures(body);

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
        if (arguments.Any(a => a.Expression.Type == WylType.Error))
            return null;
        var constructor = Resolve(call.Location, $"constructor of '{baseType}'", ConstructorsOf(baseType), null, arguments);
        if (constructor is null)
            return null;
        CheckAccessible(call.Location, baseType.Symbol!, constructor.Symbol.IsPrivate, $"constructor '{constructor}'");
        var converted = ConvertArguments(constructor, arguments);
        for (int i = 0; i < converted.Count; i++)
        {
            var parameter = constructor.ParameterType(i);
            if ((constructor.ParameterRefKind(i) != RefKind.None || parameter.IsNullableReference || parameter.IsRefStruct) && !IsHeldByCaller(converted[i]))
                Error(converted[i].Location, "an argument to 'super(...)' for a reference or view parameter must be a parameter, a field of one, or null, for now");
        }
        return new BoundSuperCall(call.Location, constructor, converted);
    }

    /// <summary>
    /// Whether a reference or view argument points into storage the constructor's caller keeps
    /// alive, so that passing it needs nothing held and nothing computed before the base constructor.
    /// </summary>
    static bool IsHeldByCaller(BoundExpression argument)
    {
        if (argument is BoundConversion { Operand.Type: var from } && from == WylType.Null)
            return true;
        var path = PlacePath.Of(argument);
        return path is { Base: PlaceBase.Local, Root: BoundLocalAccess { Local.IsParameter: true, AssumesNonNull: false } }
            && path.Steps.All(s => s is BoundFieldAccess or BoundConversion);
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

    static List<Candidate> ConstructorsOf(WylType type)
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
        local.Owner ??= currentFunction;
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
        var declared = s.Type is null ? null : BindVariableType(s.Type, s.Annotations, isParameter: false);
        var initializer = s.Initializer is null ? null : BindValue(s.Initializer, declared);
        WylType type;
        if (declared is not null)
        {
            type = declared;
            if (initializer is not null)
                initializer = Convert(initializer, type);
        }
        else if (initializer is null)
        {
            Error(s.Location, "'var' declarations require an initializer");
            type = WylType.Error;
        }
        else if (initializer.Type == WylType.Void)
        {
            Error(initializer.Location, "cannot assign a 'void' value to a variable");
            type = WylType.Error;
        }
        else if (initializer.Type == WylType.Null)
        {
            Error(initializer.Location, "cannot infer a type from 'null'; declare the variable with a nullable type instead of 'var'");
            type = WylType.Error;
        }
        else if (initializer.Type is NullableReferenceType reference)
        {
            var keyword = FunctionSymbol.RefPrefix(reference.Kind).Trim();
            Error(initializer.Location, $"'{TextOf(initializer.Location)}' is a reference that may refer to nothing; refer to it with '{keyword}? var', or copy what it refers to with '!'");
            type = WylType.Error;
        }
        else
        {
            (type, initializer) = InferVariableType(s, initializer);
        }

        if ((type.IsRefStruct || type.IsNonEscapingFunction) && initializer is null)
            Error(s.Location, $"a '{type}' variable must be initialized where it is declared, since it cannot be reassigned");
        else if (s.IsMutable && type.NonNull.IsNonEscapingFunction)
            Error(s.NameLocation, NonEscapingMutMessage);
        else if (type.IsRefStruct && initializer is not null && ViewsTemporary(initializer))
            Error(initializer.Location, $"this '{type}' would view a temporary value, which is gone after this statement; store the value in a variable first");
        else if (type.IsRefStruct && initializer is not null && PlacePath.ViewedNullableValue(initializer) is { } root)
            Error(initializer.Location, $"this '{type}' would view inside {DestroyedByNull(root)}");
        var local = new LocalSymbol(s.Name, type, isParameter: false) { IsMutable = s.IsMutable, IsDeferred = s.Initializer is null };
        Declare(local, s.NameLocation);
        return new BoundVariableDeclaration(s.Location, local, initializer);
    }

    /// <summary>
    /// <c>ref T x = place;</c> or <c>in T x = place;</c>, which name a place for the rest of the
    /// scope, or <c>ref? T x = ...;</c>, which may name none.
    /// </summary>
    BoundStatement BindRefLocal(VarDeclStmt s)
    {
        bool mayBeNull = s.Type is { Shape: TypeShape.NullableReference };
        var keyword = FunctionSymbol.RefPrefix(s.RefKind).Trim() + (mayBeNull ? "?" : "");
        foreach (var annotation in s.Annotations)
        {
            if (annotation.Name == "Escaping")
                Error(annotation.Location, $"'@Escaping' cannot be used on a '{keyword}' variable, which refers to a place rather than holding a function");
            else
                ReportMisplacedAnnotation(annotation);
        }
        var typeSyntax = mayBeNull ? s.Type!.Element : s.Type;
        var declared = typeSyntax is null ? null : BindType(typeSyntax, allowVoid: false);
        if (mayBeNull && declared is not null && declared != WylType.Error && !CheckReferencedType(declared, keyword, typeSyntax!.Location))
            declared = WylType.Error;
        var place = s.Initializer is null ? null : BindExpression(s.Initializer, declared);
        if (place is not null && !mayBeNull)
            place = NarrowReference(place);
        // A local that can't be assigned again keeps its value, so a reference may be kept into its
        // narrowed value (02-types.md §2.5); flow analysis checks the null test.
        if (place is BoundLocalAccess { AssumesNonNull: false, Type.IsNullableValue: true } narrowed && declared == narrowed.Type.Narrowed && PlacePath.MayReferInto(narrowed.Local))
            place = new BoundLocalAccess(narrowed.Location, narrowed.Local, AssumesNonNull: true);
        var type = declared ?? place?.Type.Referred ?? WylType.Error;
        BoundExpression? initializer = null;
        var what = $"a '{keyword}' variable";
        if (place is null)
            Error(s.Location, $"{what} must be initialized with the variable, field, or element it refers to");
        else if (place.Type == WylType.Null && declared is null)
        {
            Error(place.Location, $"cannot infer a type from 'null'; declare the type {what} refers to instead of 'var'");
            type = WylType.Error;
        }
        else if (type != WylType.Error && place.Type != WylType.Error)
        {
            initializer = mayBeNull ? ConvertToNullableReference(place, type.NullableReference(s.RefKind), what)
                : CheckReference(place, s.RefKind, type, what) ? place
                : null;
        }
        var local = new LocalSymbol(s.Name, mayBeNull && type != WylType.Error ? type.NullableReference(s.RefKind) : type, isParameter: false, s.RefKind);
        Declare(local, s.NameLocation);
        // Declared even after an error, so later uses aren't reported as unassigned.
        return new BoundVariableDeclaration(s.Location, local, initializer ?? new BoundErrorExpression(s.Location));
    }

    /// <summary>
    /// The reference a <c>ref?</c> or <c>in?</c> of type <paramref name="type"/> takes from
    /// <paramref name="value"/>: <c>null</c>, another reference that may refer to nothing, or a place,
    /// as a <c>ref</c> or <c>in</c> would take it. Null after reporting why it can't;
    /// <paramref name="what"/> names the reference in messages.
    /// </summary>
    BoundExpression? ConvertToNullableReference(BoundExpression value, NullableReferenceType type, string what)
    {
        if (value.Type == WylType.Error)
            return null;
        if (value.Type == WylType.Null)
            return new BoundConversion(value.Location, type, value);
        if (value.Type is not NullableReferenceType reference)
            return CheckReference(value, type.Kind, type.Referenced, what) ? new BoundConversion(value.Location, type, value) : null;

        if (reference.Referenced != type.Referenced)
            Error(value.Location, $"{what} must have exactly the type of what it refers to, '{reference.Referenced}', not '{type.Referenced}'");
        else if (CheckReferredPlace(value, type.Kind, what, mayBeNull: true, throughReference: true))
            return value.Type == type ? value : new BoundConversion(value.Location, type, value);
        return null;
    }

    /// <summary>
    /// Checks that a reference can be kept to a place: <paramref name="place"/> itself, or with
    /// <paramref name="throughReference"/>, what the <c>ref?</c> or <c>in?</c> it is refers to.
    /// </summary>
    bool CheckReferredPlace(BoundExpression place, RefKind refKind, string what, bool mayBeNull, bool throughReference)
    {
        var (kind, reason) = ClassifyPlace(place);
        var suffix = mayBeNull ? "?" : "";
        var referred = throughReference ? $"what '{TextOf(place.Location)}' refers to," : $"'{TextOf(place.Location)}'";
        if (PlacePath.NullableValueRoot(place) is { } root)
            Error(place.Location, $"{what} cannot refer into {DestroyedByNull(root)}");
        else if (kind == PlaceKind.Temporary)
            Error(place.Location, $"{what} must refer to a variable, field, or element, not a temporary value");
        else if (kind == PlaceKind.ReadOnly && refKind == RefKind.Ref)
            Error(place.Location, $"cannot take a 'ref{suffix}' to {referred} because {reason}; use 'in{suffix}' instead");
        else
            return true;
        return false;
    }

    /// <summary>Why nothing may keep referring into a nullable value, which assigning its variable null destroys.</summary>
    static string DestroyedByNull(LocalSymbol root) =>
        $"the nullable '{root.Name}', which assigning it null would destroy; copy the value to a variable first";

    /// <summary>A reference can't refer to a class object or a function, which are never copied anyway.</summary>
    bool CheckReferencedType(WylType type, string keyword, Location location)
    {
        var target = type.Referred.NonNull;
        if (!type.PassedWithoutCopying)
            return true;
        Error(location, target.IsFunction
            ? $"'{keyword}' cannot be used with type '{target}'; functions are already passed without copying"
            : $"'{keyword}' cannot be used with type '{target}'; class objects are already references");
        return false;
    }

    /// <summary>
    /// Checks that <paramref name="place"/> can be referred to by a <c>ref</c> or <c>in</c> of type
    /// <paramref name="type"/>; <paramref name="what"/> names the reference in messages.
    /// </summary>
    bool CheckReference(BoundExpression place, RefKind refKind, WylType type, string what)
    {
        if (!CheckReferencedType(type, FunctionSymbol.RefPrefix(refKind).Trim(), place.Location))
            return false;
        if (place.Type is NullableReferenceType reference && reference.Referenced == type)
        {
            Error(place.Location, MayReferToNothingMessage(place));
            return false;
        }
        if (place.Type != type)
        {
            Error(place.Location, $"{what} must have exactly the type of what it refers to, '{place.Type}', not '{type}'");
            return false;
        }
        // Through such a reference the variable could become null after a null check narrowed it.
        if (refKind == RefKind.Ref && type.IsNullableValue && place is BoundLocalAccess { Local.RefKind: RefKind.None } local)
        {
            Error(place.Location, $"{what} cannot refer to the nullable variable '{local.Local.Name}'; pass it by 'ref' to a function instead");
            return false;
        }
        return CheckReferredPlace(place, refKind, what, mayBeNull: false, throughReference: false);
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
        scope = scope.Parent;
        return new BoundFor(s.Location, initializer, condition, update, body);
    }

    BoundStatement BindReturn(ReturnStmt s)
    {
        var function = currentFunction!;
        if (s.Value is null)
        {
            if (function.ReturnType != WylType.Void && function.ReturnType != WylType.Error)
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
        if (function.ReturnType == WylType.Void)
        {
            Error(s.Value.Location, $"{Describe(function)} returns 'void' and cannot return a value");
            return new BoundReturn(s.Location, null);
        }
        var converted = Convert(value, function.ReturnType);
        if (function.ReturnType.IsRefStruct && converted.Type != WylType.Error && !IsThisDerived(converted))
        {
            var hint = converted is BoundCall { Receiver: { } holder } && holder.Type.SharesStorage && IsThisRooted(holder)
                ? $"; '{TextOf(holder.Location)}' holds its contents itself, and could be replaced while the caller uses them"
                : "";
            Error(s.Value.Location, $"a returned '{function.ReturnType}' must view the method's receiver: fields or elements of 'this', or a span returned by its methods{hint}");
        }
        return new BoundReturn(s.Location, converted);
    }

    /// <summary>
    /// <c>return ref place;</c>, or <c>return null;</c> from a method returning <c>ref?</c>. The
    /// caller treats the reference as borrowing the receiver, so it must point into the receiver's
    /// own storage: <c>this</c>, its fields and elements, or, in unsafe code, a pointer's target.
    /// </summary>
    BoundStatement BindReturnReference(ReturnStmt s, FunctionSymbol function)
    {
        var returnType = function.ReturnType;
        var keyword = FunctionSymbol.RefPrefix(function.ReturnRefKind).Trim() + (returnType.IsNullableReference ? "?" : "");
        var referenced = returnType.Referred;
        var place = BindExpression(s.Value!, referenced);
        if (!returnType.IsNullableReference)
            place = NarrowReference(place);
        if (function.ReturnRefKind == RefKind.None)
            Error(s.Location, "'return ref' can only be used in a method that returns 'ref' or 'in'");
        else if (!s.IsRef && returnType.IsNullableReference && place.Type == WylType.Null)
            return new BoundReturn(s.Location, new BoundConversion(place.Location, returnType, place));
        else if (!s.IsRef)
            Error(s.Location, $"'{function.Name}' returns a reference; return a place with 'return ref'{(returnType.IsNullableReference ? ", or 'return null'" : "")}");
        // A return type that cannot be a reference was reported with the method.
        else if (referenced.PassedWithoutCopying || referenced == WylType.Void)
            return new BoundReturn(s.Location, null);
        else if (place.Type != WylType.Error)
        {
            var what = $"a returned '{keyword}'";
            var reference = returnType is NullableReferenceType nullableReturn ? ConvertToNullableReference(place, nullableReturn, what)
                : CheckReference(place, function.ReturnRefKind, returnType, what) ? place
                : null;
            if (reference is not null && IsThisRooted(place))
                return new BoundReturn(s.Location, reference);
            if (reference is not null)
                Error(s.Value!.Location, $"{what} must point into the method's receiver: a field or element of 'this'");
        }
        return new BoundReturn(s.Location, null);
    }

    /// <summary>Whether a place lies in <c>this</c> itself, not in another object it refers to.</summary>
    static bool IsThisRooted(BoundExpression place) => IsThisRooted(PlacePath.Of(place));

    static bool IsThisRooted(PlacePath path) => path.Base switch
    {
        PlaceBase.This or PlaceBase.Pointer => true,
        PlaceBase.Object => path.Root is BoundThis,
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
        if (expression is not (BoundCall or BoundInvocation or BoundNew or BoundTry or BoundConditionalAccess { WhenNotNull: BoundCall }) && expression.Type != WylType.Error)
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
        if (value.Type == WylType.Void)
            Error(s.Value.Location, "there is no value to discard; call it without '_ ='");
        return new BoundExpressionStatement(s.Location, value, IsDiscard: true);
    }

    BoundStatement BindAssignment(AssignStmt s)
    {
        // Assigning through a reference assigns the place it refers to.
        var target = NarrowReference(BindExpression(s.Target));
        var value = s.Operator == TokenKind.Assign ? BindValue(s.Value, target.Type) : BindExpression(s.Value);
        if (target.Type == WylType.Error || !CheckAssignable(target))
            return ErrorStatement(s.Location);

        if (s.Operator != TokenKind.Assign)
            value = BindBinaryOperation(s.OperatorLocation, target, SyntaxFacts.CompoundToBinary(s.Operator), value);
        return new BoundAssignment(s.Location, target, Convert(value, target.Type));
    }

    BoundStatement BindIncDec(IncDecStmt s)
    {
        var target = NarrowReference(BindExpression(s.Target));
        if (target.Type == WylType.Error || !CheckAssignable(target))
            return ErrorStatement(s.Location);
        var operand = target.Type.IsNullableValue ? AssumeNonNull(target) : target;
        if (!operand.Type.IsNumeric)
        {
            Error(s.Location, $"'{(s.IsIncrement ? "++" : "--")}' requires a numeric variable{NullableOperandHint(operand)}");
            return ErrorStatement(s.Location);
        }

        var value = BindBinaryOperation(
            s.Location, operand, s.IsIncrement ? TokenKind.Plus : TokenKind.Minus, BoundLiteral.Number(s.Location, operand.Type, 1));
        return new BoundAssignment(s.Location, target, Convert(value, target.Type));
    }

    (PlaceKind Kind, string Reason) ClassifyPlace(BoundExpression expression) => ClassifyPlace(PlacePath.Of(expression));

    (PlaceKind Kind, string Reason) ClassifyPlace(PlacePath path)
    {
        (PlaceKind Kind, string Reason) place = path switch
        {
            { Base: PlaceBase.Local, Root: BoundLocalAccess { Local: var local } } => ClassifyLocal(local),
            { Root: BoundThis { Type.IsReference: true } } => (PlaceKind.Temporary, ""),
            { Base: PlaceBase.This } when currentFunction is { IsReadOnly: true } => (PlaceKind.ReadOnly, "'this' is read-only in a method that isn't 'mut'"),
            { Base: PlaceBase.This } => (PlaceKind.Mutable, ""),
            { Base: PlaceBase.Returned, Root: BoundCall call } => ClassifyReturnedReference(call, PlaceKind.Temporary),
            _ => (PlaceKind.Temporary, ""),
        };
        foreach (var step in path.Steps)
        {
            place = step switch
            {
                // Fields of an object live on the heap, however the reference to it was obtained.
                BoundFieldAccess { Receiver.Type.IsReference: true } field when IsAssignableField(field) => (PlaceKind.Mutable, ""),
                BoundFieldAccess { Receiver.Type.IsReference: true } field => (PlaceKind.ReadOnly, $"field '{field.Field.Name}' is not 'mut'"),
                BoundIndex { Target.Type.IsPointer: true } => (PlaceKind.Mutable, ""),
                BoundCall { Method.Symbol.ReturnRefKind: not RefKind.None } call => ClassifyReturnedReference(call, place.Kind),
                // A view is a value, whatever it views.
                BoundCall or BoundConversion { Type.IsRefStruct: true } => (PlaceKind.Temporary, ""),
                _ => place,
            };
        }
        return place;
    }

    (PlaceKind Kind, string Reason) ClassifyLocal(LocalSymbol local) => local switch
    {
        _ when IsCopiedByEscapingLambda(local) => (PlaceKind.ReadOnly, $"an escaping lambda only has a copy of '{local.Name}'"),
        { RefKind: RefKind.In } => (PlaceKind.ReadOnly, $"'{local.Name}' is an 'in' {(local.IsParameter ? "parameter" : "variable")}"),
        { IsWritable: false } => (PlaceKind.ReadOnly, $"{(local.IsParameter ? "parameter" : "variable")} '{local.Name}' is not 'mut'"),
        _ => (PlaceKind.Mutable, ""),
    };

    /// <summary>A class field that isn't <c>mut</c> can only be changed by its own class's constructors, through <c>this</c>.</summary>
    bool IsAssignableField(BoundFieldAccess field) =>
        field.Field.IsMutable
        || (field.Receiver is BoundThis && currentFunction is { IsConstructor: true } constructor && constructor.ContainingType == field.Field.Owner);

    /// <summary>A returned reference points into the receiver, so a temporary struct receiver makes it temporary too.</summary>
    static (PlaceKind Kind, string Reason) ClassifyReturnedReference(BoundCall call, PlaceKind receiver)
    {
        if (call.Receiver is { Type.IsAggregate: true, Type.IsRefStruct: false } && receiver == PlaceKind.Temporary)
            return (PlaceKind.Temporary, "");
        if (call.Method.Symbol.ReturnRefKind == RefKind.In)
            return (PlaceKind.ReadOnly, $"'{call.Method.Symbol.Signature}' returns 'in'");
        return (PlaceKind.Mutable, "");
    }

    bool CheckAssignable(BoundExpression target)
    {
        if (target.Type.IsNullableReference)
        {
            Error(target.Location, MayReferToNothingMessage(target));
            return false;
        }
        if (target.Type.IsRefStruct)
        {
            Error(target.Location, $"'{TextOf(target.Location)}' cannot be reassigned: a '{target.Type}' views what it was initialized with for its whole scope; declare a new variable instead");
            return false;
        }
        // A lambda in it may use the function's locals by reference, so it must not outlive any of them.
        if (target is BoundLocalAccess function && function.Local.Type.NonNull.IsNonEscapingFunction)
        {
            Error(target.Location, $"'{function.Local.Name}' cannot be reassigned: a non-escaping {(function.Local.IsParameter ? "parameter" : "variable")} keeps the function it was given; declare a new variable instead, or declare this one '@Escaping'");
            return false;
        }
        // Its first assignment initializes it; the flow analysis checks that it is the only one.
        if (target is BoundLocalAccess { Local: { IsDeferred: true, IsMutable: false } deferred })
        {
            if (deferred.Owner == currentFunction)
                return true;
            if (!IsCopiedByEscapingLambda(deferred))
            {
                Error(target.Location, $"cannot assign to '{deferred.Name}' in a lambda, which may run more than once, because variable '{deferred.Name}' is not 'mut'");
                return false;
            }
        }
        var (kind, reason) = ClassifyPlace(target);
        switch (kind)
        {
            case PlaceKind.Temporary:
                Error(target.Location, target switch
                {
                    BoundFieldAccess => "cannot assign to a field of a temporary value",
                    BoundIndex => "cannot assign to an element of a temporary array",
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
