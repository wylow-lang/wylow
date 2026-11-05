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
        hasThis = currentType is not null && !function.IsStatic && function.ReceiverParameter is null;
        scope = new Scope(null);
        if (function.ReceiverParameter is { } receiver)
        {
            receiver.Owner = function;
            scope.Add(receiver);
        }
        foreach (var parameter in function.Parameters)
        {
            parameter.Owner = function;
            scope.Add(parameter);
        }

        var undo = ApplyMemberBounds(function);
        var body = function.IsConstructor
            ? BindConstructorBody(function)
            : BindBlock(function.Body!);
        undo();
        CheckEscapingCaptures(body);

        currentFunction = null;
        currentType = null;
        typeParameters = [];
        hasThis = false;
        scope = null;
        return body;
    }

    void Declare(LocalSymbol local, Location location)
    {
        // A local may not shadow another local or parameter (it may shadow a field).
        local.Owner ??= currentFunction;
        if (scope!.Lookup(local.Name) is not null)
        {
            Error(Catalog.DuplicateLocal(location, local.Name));
            return;
        }
        TakesLanguageName(local.Name, location);
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
        DiscardLocalStmt s => BindDiscardLocal(s),
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
        var declared = s.Type is null ? null : BindVariableType(s.Type, s.Escaping, s.Annotations, isParameter: false);
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
            Error(Catalog.VarWithoutInitializer(s.Location));
            type = WylType.Error;
        }
        else if (initializer.Type == WylType.Void)
        {
            Error(Catalog.VoidInitializer(initializer.Location));
            type = WylType.Error;
        }
        else if (initializer.Type == WylType.Null)
        {
            Error(Catalog.VarFromNull(initializer.Location));
            type = WylType.Error;
        }
        else if (initializer.Type is NullableReferenceType reference)
        {
            var keyword = FunctionSymbol.RefPrefix(reference.Kind).Trim();
            Error(Catalog.VarFromNullableReference(initializer.Location, TextOf(initializer.Location), keyword));
            type = WylType.Error;
        }
        else
        {
            (type, initializer) = InferVariableType(s, initializer);
        }

        if (type.IsSecondClass && initializer is null)
            Error(Catalog.SecondClassUninitialized(s.Location, type.ToString()));
        else if (s.IsMutable && type.NonNull.IsNonEscapingFunction)
            Error(Catalog.NonEscapingMut(s.NameLocation));
        else if (type.IsRefStruct && initializer is not null && ViewsTemporary(initializer))
            Error(Catalog.ViewOfTemporary(initializer.Location, type.ToString()));
        else if (type.IsRefStruct && initializer is not null && PlacePath.ViewedNullableValue(initializer) is { } root)
            Error(Catalog.ViewInsideNullable(initializer.Location, type.ToString(), root.Name));
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
            ReportMisplacedAnnotation(annotation);
        if (s.Escaping is { } escaping)
            Error(Catalog.ReferenceEscaping(escaping, keyword));
        var typeSyntax = mayBeNull ? s.Type!.Element : s.Type;
        var declared = typeSyntax is null ? null : BindType(typeSyntax, allowVoid: false);
        if (mayBeNull && declared is not null && declared != WylType.Error && !CheckReferencedType(declared, keyword, typeSyntax!.Location))
            declared = WylType.Error;
        var place = s.Initializer is null ? null : BindExpression(s.Initializer, declared);
        if (place is not null && !mayBeNull)
            place = NarrowReference(place);
        // A local that can't be assigned again keeps its value, so a reference may be kept into its
        // narrowed value (spec flow.narrowing); flow analysis checks the null test.
        if (place is BoundLocalAccess { AssumesNonNull: false, Type.IsNullableValue: true } narrowed && declared == narrowed.Type.Narrowed && PlacePath.MayReferInto(narrowed.Local))
            place = new BoundLocalAccess(narrowed.Location, narrowed.Local, AssumesNonNull: true);
        var type = declared ?? place?.Type.Referred ?? WylType.Error;
        BoundExpression? initializer = null;
        var what = $"'{keyword}' variable";
        if (place is null)
            Error(Catalog.ReferenceUninitialized(s.Location, what));
        else if (place.Type == WylType.Null && declared is null)
        {
            Error(Catalog.ReferenceTypeFromNull(place.Location, what));
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
            Error(Catalog.ReferenceTypeMismatch(value.Location, what, type.Referenced.ToString(), reference.Referenced.ToString()));
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
        if (PlacePath.NullableValueRoot(place) is { } root)
            Error(Catalog.ReferenceIntoNullable(place.Location, what, root.Name));
        else if (kind == PlaceKind.Temporary)
            Error(Catalog.ReferenceToTemporary(place.Location, what, Elementless(place)));
        else if (kind == PlaceKind.ReadOnly && refKind == RefKind.Ref)
            Error(Catalog.RefToReadOnly(place.Location, $"ref{suffix}", TextOf(place.Location), throughReference, reason, suffix));
        else
            return true;
        return false;
    }

    /// <summary>A reference can't refer to a class object or a function, which are never copied anyway.</summary>
    bool CheckReferencedType(WylType type, string keyword, Location location)
    {
        var target = type.Referred.NonNull;
        if (!target.IsFunction)
            return true;
        Error(Catalog.ReferenceToPassedWithoutCopying(location, keyword, target.ToString()));
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
            Error(MayReferToNothing(place.Location, place));
            return false;
        }
        if (place.Type != type)
        {
            Error(Catalog.ReferenceTypeMismatch(place.Location, what, type.ToString(), place.Type.ToString()));
            return false;
        }
        // Through such a reference the variable could become null after a null check narrowed it.
        if (refKind == RefKind.Ref && type.IsNullableValue && place is BoundLocalAccess { Local.RefKind: RefKind.None } local)
        {
            Error(Catalog.ReferenceToNullableVariable(place.Location, what, local.Local.Name));
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
                Error(Catalog.MissingReturnValue(s.Location, function.IsLambda ? null : function.Name, function.ReturnType.ToString()));
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
            Error(Catalog.ReturnFromConstructor(s.Value.Location, function.IsConstructor));
            return new BoundReturn(s.Location, null);
        }
        if (function.ReturnType == WylType.Void)
        {
            Error(Catalog.ReturnFromVoid(s.Value.Location, function.IsLambda ? null : function.Name));
            return new BoundReturn(s.Location, null);
        }
        var converted = Convert(value, function.ReturnType);
        // A by-value receiver cannot return a view, which is reported with the method.
        if (function.ReturnType.IsRefStruct && function.ReceiverParameter is null && converted.Type != WylType.Error && !IsThisDerived(converted))
        {
            var holderText = converted is BoundCall { Receiver: { } holder } && holder.Type.SharesStorage && IsThisRooted(holder)
                ? TextOf(holder.Location)
                : null;
            Error(Catalog.ReturnedViewNotOfReceiver(s.Value.Location, function.ReturnType.ToString(), holderText));
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
            Error(Catalog.ReturnRefWithoutReference(s.Location));
        else if (!s.IsRef && returnType.IsNullableReference && place.Type == WylType.Null)
            return new BoundReturn(s.Location, new BoundConversion(place.Location, returnType, place));
        else if (!s.IsRef)
            Error(Catalog.ReturnReferenceWithoutRef(s.Location, function.Name, returnType.IsNullableReference));
        // A return type that cannot be a reference was reported with the method.
        else if (referenced.NonNull.IsFunction || referenced == WylType.Void)
            return new BoundReturn(s.Location, null);
        else if (place.Type != WylType.Error)
        {
            var what = $"returned '{keyword}'";
            var reference = returnType is NullableReferenceType nullableReturn ? ConvertToNullableReference(place, nullableReturn, what)
                : CheckReference(place, function.ReturnRefKind, returnType, what) ? place
                : null;
            if (reference is not null && IsThisRooted(place))
                return new BoundReturn(s.Location, reference);
            // A by-value receiver cannot return a reference, which is reported with the method.
            if (reference is not null && function.ReceiverParameter is null)
                Error(Catalog.ReturnedReferenceNotOfReceiver(s.Value!.Location, what));
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
            Error(Catalog.JumpOutsideLoop(location, keyword));
        return jump;
    }

    BoundStatement BindExpressionStatement(ExprStmt s)
    {
        var expression = BindValue(s.Expression, null);
        if (expression is not (BoundCall or BoundInvocation or BoundNew or BoundTry or BoundConditionalAccess { WhenNotNull: BoundCall }) && expression.Type != WylType.Error)
            Error(Catalog.NotAStatement(s.Location));
        else if (expression is BoundCall or BoundInvocation && IsResult(expression.Type))
        {
            var name = expression is BoundCall call ? call.Method.Symbol.Name : TextOf(((BoundInvocation)expression).Callee.Location);
            Error(Catalog.UnusedResult(s.Location, name));
        }
        return new BoundExpressionStatement(s.Location, expression);
    }

    BoundStatement BindDiscard(DiscardStmt s)
    {
        var value = BindValue(s.Value, null);
        if (value.Type == WylType.Void)
            Error(Catalog.DiscardVoid(s.Value.Location));
        return new BoundExpressionStatement(s.Location, value, IsDiscard: true);
    }

    /// <summary>
    /// <c>discard name;</c>, which ends a by-value parameter's lifetime without its destructor's
    /// body. Only a parameter of a unique struct that declares a destructor may be discarded, and
    /// only from a function declared in the type's file (spec <c>structs.unique.destructors</c>).
    /// </summary>
    BoundStatement BindDiscardLocal(DiscardLocalStmt s)
    {
        if (scope!.Lookup(s.Name) is not { IsParameter: true } local || local.Owner != currentFunction)
        {
            Error(Catalog.DiscardNotParameter(s.NameLocation, s.Name));
            return ErrorStatement(s.Location);
        }
        if (local.RefKind != RefKind.None || local.Type.NonNull.Symbol is not { Kind: TypeKind.Struct, Destructor: not null } type)
        {
            Error(Catalog.DiscardNotParameter(s.NameLocation, s.Name));
            return ErrorStatement(s.Location);
        }
        var typeFile = type.Declaration.Location.Source;
        if (currentFunction!.Declaration?.Location.Source is not { } functionFile || !ReferenceEquals(functionFile, typeFile))
        {
            Error(Catalog.DiscardOutsideTypeFile(s.NameLocation, s.Name, type.DisplayName, typeFile.Name));
            return ErrorStatement(s.Location);
        }
        return new BoundDiscardLocal(s.Location, local);
    }

    BoundStatement BindAssignment(AssignStmt s)
    {
        var target = BindAssignmentTarget(s.Target);
        var value = s.Operator == TokenKind.Assign ? BindValue(s.Value, target.Type) : BindExpression(s.Value);
        if (target.Type == WylType.Error || !CheckAssignable(target))
            return ErrorStatement(s.Location);

        if (s.Operator != TokenKind.Assign)
            value = BindBinaryOperation(s.OperatorLocation, target, SyntaxFacts.CompoundToBinary(s.Operator), value);
        return new BoundAssignment(s.Location, target, Convert(value, target.Type));
    }

    /// <summary>Assigning through a reference assigns the place it refers to; an element may be assigned through <c>operator []=</c>.</summary>
    BoundExpression BindAssignmentTarget(Expr target) =>
        target is IndexExpr index ? NarrowReference(BindIndex(index, assigned: true)) : NarrowReference(BindExpression(target));

    BoundStatement BindIncDec(IncDecStmt s)
    {
        var target = BindAssignmentTarget(s.Target);
        if (target.Type == WylType.Error || !CheckAssignable(target))
            return ErrorStatement(s.Location);
        var operand = target.Type.IsNullableValue ? AssumeNonNull(target) : target;
        if (!operand.Type.IsNumeric)
        {
            Error(Catalog.IncDecNonNumeric(s.Location, s.IsIncrement ? "++" : "--", NullableOperands(operand)));
            return ErrorStatement(s.Location);
        }

        var value = BindBinaryOperation(
            s.Location, operand, s.IsIncrement ? TokenKind.Plus : TokenKind.Minus, BoundLiteral.Number(s.Location, operand.Type, 1));
        return new BoundAssignment(s.Location, target, Convert(value, target.Type));
    }

    (PlaceKind Kind, ReadOnlyReason Reason) ClassifyPlace(BoundExpression expression) => ClassifyPlace(PlacePath.Of(expression));

    (PlaceKind Kind, ReadOnlyReason Reason) ClassifyPlace(PlacePath path)
    {
        (PlaceKind Kind, ReadOnlyReason Reason) place = path switch
        {
            { Base: PlaceBase.Local, Root: BoundLocalAccess { Local: var local } } => ClassifyLocal(local),
            { Root: BoundThis { Type.IsReference: true } } => (PlaceKind.Temporary, default),
            { Base: PlaceBase.This } when currentFunction is { IsReadOnly: true } => (PlaceKind.ReadOnly, new(ReadOnlyKind.ReadOnlyThis)),
            { Base: PlaceBase.This } => (PlaceKind.Mutable, default),
            { Base: PlaceBase.Returned, Root: BoundCall call } => ClassifyReturnedReference(call, PlaceKind.Temporary),
            _ => (PlaceKind.Temporary, default),
        };
        foreach (var step in path.Steps)
        {
            place = step switch
            {
                // Fields of an object live on the heap, however the reference to it was obtained.
                BoundFieldAccess { Receiver.Type.IsReference: true } field when IsAssignableField(field) => (PlaceKind.Mutable, default),
                BoundFieldAccess { Receiver.Type.IsReference: true } field => (PlaceKind.ReadOnly, new(ReadOnlyKind.ImmutableField, field.Field.Name)),
                BoundIndex { Target.Type.IsPointer: true } => (PlaceKind.Mutable, default),
                // The value in a nullable value is a temporary, so `a!.x = 1` and `a!.m()` are errors.
                BoundNonNull { Operand.Type.IsNullableValue: true } => (PlaceKind.Temporary, default),
                BoundCall { Method.Symbol.ReturnRefKind: not RefKind.None } call => ClassifyReturnedReference(call, place.Kind),
                // A view is a value, whatever it views.
                BoundCall => (PlaceKind.Temporary, default),
                _ => place,
            };
        }
        return place;
    }

    (PlaceKind Kind, ReadOnlyReason Reason) ClassifyLocal(LocalSymbol local) => local switch
    {
        _ when IsCopiedByEscapingLambda(local) => (PlaceKind.ReadOnly, new(ReadOnlyKind.LambdaCopy, local.Name)),
        { RefKind: RefKind.In } => (PlaceKind.ReadOnly, new(local.IsParameter ? ReadOnlyKind.InParameter : ReadOnlyKind.InVariable, local.Name)),
        { IsWritable: false } => (PlaceKind.ReadOnly, new(local.IsParameter ? ReadOnlyKind.ImmutableParameter : ReadOnlyKind.ImmutableVariable, local.Name)),
        _ => (PlaceKind.Mutable, default),
    };

    /// <summary>A class field that isn't <c>mut</c> can only be changed by its own class's constructors, through <c>this</c>.</summary>
    bool IsAssignableField(BoundFieldAccess field) =>
        field.Field.IsMutable
        || (field.Receiver is BoundThis && currentFunction is { IsConstructor: true } constructor && constructor.ContainingType == field.Field.Owner);

    /// <summary>A returned reference points into the receiver, so a temporary struct receiver makes it temporary too.</summary>
    static (PlaceKind Kind, ReadOnlyReason Reason) ClassifyReturnedReference(BoundCall call, PlaceKind receiver)
    {
        if (call.Receiver is { Type.IsAggregate: true, Type.IsRefStruct: false } && receiver == PlaceKind.Temporary)
            return (PlaceKind.Temporary, default);
        if (call.Method.Symbol.ReturnRefKind == RefKind.In)
            return (PlaceKind.ReadOnly, new(ReadOnlyKind.ReturnsIn, call.Method.Symbol.Signature));
        return (PlaceKind.Mutable, default);
    }

    bool CheckAssignable(BoundExpression target)
    {
        if (target.Type.IsNullableReference)
        {
            Error(MayReferToNothing(target.Location, target));
            return false;
        }
        if (target.Type.IsRefStruct)
        {
            Error(Catalog.ReassignView(target.Location, TextOf(target.Location), target.Type.ToString()));
            return false;
        }
        // A lambda in it may use the function's locals by reference, so it must not outlive any of them.
        if (target is BoundLocalAccess function && function.Local.Type.NonNull.IsNonEscapingFunction)
        {
            Error(Catalog.ReassignNonEscaping(target.Location, function.Local.Name, function.Local.IsParameter));
            return false;
        }
        // Its first assignment initializes it; the flow analysis checks that it is the only one.
        if (target is BoundLocalAccess { Local: { IsDeferred: true, IsMutable: false } deferred })
        {
            if (deferred.Owner == currentFunction)
                return true;
            if (!IsCopiedByEscapingLambda(deferred))
            {
                Error(Catalog.AssignDeferredInLambda(target.Location, deferred.Name));
                return false;
            }
        }
        if (target is BoundIndexerElement)
            return true;
        var (kind, reason) = ClassifyPlace(target);
        switch (kind)
        {
            case PlaceKind.Temporary:
                Error(target switch { BoundFieldAccess => Catalog.FieldOfTemporary(target.Location), BoundIndex => Catalog.ElementOfTemporaryArray(target.Location), _ => Catalog.NotAssignable(target.Location) });
                return false;
            case PlaceKind.ReadOnly:
                Error(Catalog.AssignReadOnly(target.Location, TextOf(target.Location), reason));
                return false;
            default:
                return true;
        }
    }
}
