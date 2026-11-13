using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

/// <summary>
/// Escaping functions (spec functions.lambdas.escaping): which function types are escaping, and what an escaping lambda may
/// capture. A non-escaping function may use its creator's locals by reference, so it can't be
/// stored; an escaping one copies what it captures, so what it captures must not change.
/// </summary>
public sealed partial class Binder
{
    /// <summary>The non-escaping form of a function type or nullable function type; any other type itself.</summary>
    static WylType NonEscaping(WylType type) =>
        type.NonNull is FunctionType function ? (type.IsNullable ? function.NonEscaping.Nullable : function.NonEscaping) : type;

    /// <summary>The escaping form of a function type or nullable function type; any other type itself.</summary>
    static WylType EscapingForm(WylType type) =>
        type.NonNull is FunctionType function ? (type.IsNullable ? function.Escaping.Nullable : function.Escaping) : type;


    /// <summary>
    /// Only a parameter can hold a nullable non-escaping function: a variable would have to own the
    /// lambda it may hold, as a non-escaping variable does, while one that is null holds none.
    /// </summary>

    /// <summary>
    /// The type a <c>?:</c> or switch expression's options are bound with: a choice of nullable
    /// function type is escaping, since a non-escaping one would refer to a function made only for it.
    /// </summary>
    static WylType? ChoiceTarget(WylType? type) => type is not null && type.IsNullableNonEscapingFunction ? EscapingForm(type) : type;

    /// <summary>
    /// The type of a parameter or local: a function type, nullable or not, is non-escaping unless
    /// the declaration is <c>escaping</c>; a nullable local must be. No annotation applies to one.
    /// </summary>
    WylType BindVariableType(TypeSyntax syntax, Location? escaping, IReadOnlyList<AnnotationSyntax> annotations, bool isParameter)
    {
        foreach (var annotation in annotations)
            ReportMisplacedAnnotation(annotation);
        var type = BindType(syntax, allowVoid: false);
        if (IsDeclaredEscaping(escaping, type, isParameter ? "parameter" : "variable"))
            return type;
        if (!isParameter && type.IsNullable && type.NonNull.IsFunction && !type.NonNull.IsExternFunction)
        {
            Error(Catalog.NullableNonEscapingVariable(syntax.Location));
            return WylType.Error;
        }
        return NonEscaping(type);
    }

    /// <summary>
    /// The type of a <c>var</c> local: its initializer's, in the escaping form if it is declared
    /// <c>escaping</c>. A nullable function must be.
    /// </summary>
    (WylType Type, BoundExpression Initializer) InferVariableType(VarDeclStmt s, BoundExpression initializer)
    {
        var type = initializer.Type;
        if (IsDeclaredEscaping(s.Escaping, type, "variable"))
            type = EscapingForm(type);
        else if (type.IsNullableNonEscapingFunction)
        {
            Error(Catalog.NullableNonEscapingVariable(s.Location));
            return (WylType.Error, initializer);
        }
        return (type, Convert(initializer, type));
    }

    /// <summary>
    /// The type of a field: a function type of a <c>ref struct</c>'s field is non-escaping unless the
    /// field is declared <c>escaping</c>, and every other function type is escaping (spec structs.ref).
    /// A non-escaping field has no initializer.
    /// </summary>
    WylType FieldType(TypeSymbol owner, FieldDecl decl, WylType type)
    {
        if (!owner.IsRefStruct || IsDeclaredEscaping(decl.Escaping, type, "field") || type.IsNullable || type is not FunctionType { IsExtern: false })
            return type;
        if (decl.Initializer is not null)
            Error(Catalog.NonEscapingFieldInitializer(decl.NameLocation, decl.Name));
        return NonEscaping(type);
    }

    /// <summary>
    /// Reports a non-escaping function argument of a <c>ref struct</c>'s constructor that may be gone
    /// before the struct is: one that is not a variable, or a variable that may be assigned another
    /// function (spec structs.ref). The constructor may keep the argument in a field.
    /// </summary>
    void CheckRefStructArguments(MethodReference constructor, IReadOnlyList<BoundExpression> arguments)
    {
        if (constructor.Symbol.ContainingType is not { IsRefStruct: true } owner)
            return;
        for (int i = 0; i < arguments.Count; i++)
        {
            if (!constructor.ParameterType(i).IsNonEscapingFunction)
                continue;
            var argument = arguments[i] is BoundConversion conversion ? conversion.Operand : arguments[i];
            if (argument is not BoundLocalAccess { Local.IsWritable: false })
                Error(Catalog.RefStructFunctionArgument(arguments[i].Location, owner.Type.ToString()));
        }
    }

    /// <summary>
    /// Reports an assignment of a non-escaping field from anything but a parameter of the constructor
    /// that assigns it, which the constructor's caller keeps alive (spec structs.ref).
    /// </summary>
    void CheckNonEscapingFieldSource(BoundExpression target, BoundExpression value)
    {
        if (target is not BoundFieldAccess { Field: { Type.IsNonEscapingFunction: true } field })
            return;
        var source = value is BoundConversion conversion ? conversion.Operand : value;
        if (source is not BoundLocalAccess { Local: { IsParameter: true } parameter } || parameter.Owner != currentFunction)
            Error(Catalog.NonEscapingFieldSource(value.Location, field.Name));
    }

    /// <summary>Whether a parameter or local of the type is declared <c>escaping</c>, which only one of function type can be.</summary>
    bool IsDeclaredEscaping(Location? escaping, WylType type, string what)
    {
        if (escaping is not { } at)
            return false;
        if (type.NonNull.IsExternFunction)
        {
            Error(Catalog.EscapingExternFunction(at, type.NonNull.ToString()));
            return false;
        }
        if (type.NonNull.IsFunction || type == WylType.Error)
            return true;
        Error(Catalog.EscapingOnNonFunction(at, what, type.ToString()));
        return false;
    }

    /// <summary>Reports an annotation used where it doesn't apply, or unknown altogether.</summary>
    void ReportMisplacedAnnotation(AnnotationSyntax annotation) => Error(annotation.Name is "CallerLocation" or "Acyclic" or "Repr" or "Pure" or "Safe" or CCallbackName or Include or CppNamespaceName or CppNameName or SuppressWarnings
        ? Catalog.MisplacedAnnotation(annotation.Location, annotation.Name)
        : Catalog.UnknownAnnotation(annotation.Location, annotation.Name));

    void CheckNoArguments(AnnotationSyntax annotation)
    {
        if (annotation.Arguments.Count > 0)
            Error(Catalog.AnnotationArguments(annotation.Location, annotation.Name));
    }

    /// <summary>
    /// Reports why a non-escaping function can't be converted to the escaping form, at the variable
    /// that makes it non-escaping, such as an option of a choice.
    /// </summary>
    void ReportNotEscaping(BoundExpression function)
    {
        // Only a variable or a field of a ref struct is non-escaping by its type; a lambda option is
        // only because such an option made the choice so.
        var culprit = BoundTree.FunctionOptions(function)
            .Select(o => o is BoundConversion conversion ? conversion.Operand : o)
            .Select(o => o is BoundNonNull nonNull ? nonNull.Operand : o)
            .FirstOrDefault(o => o is BoundLocalAccess or BoundFieldAccess && o.Type.NonNull.IsNonEscapingFunction)
            ?? throw new InvalidOperationException("a non-escaping function that no variable or field makes so");
        Error(culprit is BoundLocalAccess { Local: var local }
            ? Catalog.NonEscapingStored(culprit.Location, local.IsParameter ? "parameter" : "variable", local.Name, local.IsParameter && local.Owner?.IsLambda == true)
            : Catalog.NonEscapingStored(culprit.Location, "field", ((BoundFieldAccess)culprit).Field.Name, ofLambda: false));
    }

    /// <summary>
    /// For a duplicate overload, whether it differs from <paramref name="existing"/> only in which
    /// parameters are 'escaping', which a message says, since the two forms of a function type print alike.
    /// </summary>
    static bool DiffersOnlyInEscaping(FunctionSymbol function, FunctionSymbol existing) =>
        function.Parameters.Zip(existing.Parameters).Any(p => p.First.Type != p.Second.Type
            && p.First.Type.NonNull is FunctionType first && p.Second.Type.NonNull is FunctionType second
            && first.NonEscaping == second.NonEscaping);

    /// <summary>
    /// For signatures that must match, a parameter that is 'escaping' in only one of them, and
    /// whether in <paramref name="method"/>, which a message names, since the two forms of a function
    /// type print alike.
    /// </summary>
    static EscapingDifference? FindEscapingDifference(MethodReference method, MethodReference other)
    {
        for (int i = 0; i < method.Symbol.Parameters.Count; i++)
        {
            var (mine, theirs) = (method.ParameterType(i), other.ParameterType(i));
            if (mine != theirs && mine.NonNull is FunctionType myFunction && theirs.NonNull is FunctionType theirFunction
                && mine.IsNullable == theirs.IsNullable && myFunction.NonEscaping == theirFunction.NonEscaping)
            {
                return new EscapingDifference(method.Symbol.Parameters[i].Name, mine.NonNull.IsEscaping);
            }
        }
        return null;
    }

    /// <summary>Whether a value of type <paramref name="from"/> fails to convert only because it is non-escaping.</summary>
    static bool IsEscapingMismatch(WylType from, WylType to) =>
        from.NonNull is FunctionType { IsEscaping: false } function && to.NonNull == function.Escaping && (to.IsNullable || !from.IsNullable);

    /// <summary>
    /// The first argument that keeps a candidate from applying only by being non-escaping, if one
    /// does: saying so explains more than that no overload accepts the arguments.
    /// </summary>
    Argument? NonEscapingArgument(IEnumerable<(MethodReference Method, IReadOnlyList<Argument?> Arguments)> candidates)
    {
        foreach (var (method, arguments) in candidates)
        {
            // Applicability fails only on arguments, never on parameters left to their defaults.
            var failing = Enumerable.Range(0, arguments.Count).Where(i => !IsApplicable(method, arguments, i)).ToList();
            if (failing.Count > 0 && failing.All(i => !arguments[i]!.IsRef && method.ParameterRefKind(i) != RefKind.Ref
                    && IsEscapingMismatch(arguments[i]!.Expression.Type, method.ParameterType(i))))
                return arguments[failing[0]];
        }
        return null;
    }

    /// <summary>
    /// Whether the code being bound uses <paramref name="local"/> from inside an escaping lambda
    /// that it is declared outside of, which has only a copy of it.
    /// </summary>
    bool IsCopiedByEscapingLambda(LocalSymbol local)
    {
        if (local.Owner is null)
            return false;
        for (var function = currentFunction; function is not null && function != local.Owner; function = function.Enclosing)
        {
            if (function.IsEscaping)
                return true;
        }
        return false;
    }

    /// <summary>Checks what the escaping and shared lambdas in a body capture (spec functions.lambdas.captures, concurrency.shared-functions).</summary>
    void CheckCaptures(BoundBlock body)
    {
        var reported = new HashSet<LocalSymbol>();
        bool reportedStructThis = false;
        bool reportedSharedThis = false;
        foreach (var lambda in BoundTree.AllExpressions(body).OfType<BoundLambda>())
        {
            var function = lambda.Function;
            var (locals, self) = BoundTree.Captures(lambda);
            if (function.IsEscaping)
            {
                foreach (var use in locals)
                {
                    if (FindCaptureProblem(use.Local) is var (problem, detail) && reported.Add(use.Local))
                        Error(Catalog.EscapingCapture(use.Location, use.Local.Name, problem, use.Local.IsParameter, detail));
                }
                if (!function.IsShared && self is { Type.IsReference: false } && !reportedStructThis)
                {
                    reportedStructThis = true;
                    Error(Catalog.EscapingCaptureOfStructThis(self.Location));
                }
            }
            if (!function.IsShared)
                continue;
            foreach (var use in locals)
            {
                if (reported.Contains(use.Local))
                    continue;
                if (FindSharedCaptureProblem(function, use.Local) is { } problem)
                {
                    reported.Add(use.Local);
                    Error(SharedCaptureError(use.Location, use.Local, problem));
                }
            }
            if (self is { } captured && !reportedSharedThis && !InSharedClassMethod(function))
            {
                reportedSharedThis = true;
                Error(Catalog.SharedCaptureOfThis(captured.Location));
            }
        }
    }

    (SharedCaptureProblem Kind, string Detail)? FindSharedCaptureProblem(FunctionSymbol lambda, LocalSymbol local)
    {
        if (!lambda.IsEscaping)
        {
            if (local.IsMutable)
                return (SharedCaptureProblem.Mutable, "");
            if (local.RefKind == RefKind.Ref)
                return (SharedCaptureProblem.Reference, "ref");
            if (local.Type.IsRefStruct && local.Type.Symbol != stdlib.ReadOnlySpan)
                return (SharedCaptureProblem.View, local.Type.ToString());
        }
        var type = local.Type.IsRefStruct && local.Type.Symbol == stdlib.ReadOnlySpan ? local.Type.TypeArguments[0] : local.Type;
        if (!type.IsSendableIn(local.Owner))
            return (SharedCaptureProblem.NotSendable, type.ToString());
        if (!type.IsCopyableIn(local.Owner))
            return (SharedCaptureProblem.NotCopyable, type.ToString());
        return null;
    }

    static Diagnostic SharedCaptureError(Location at, LocalSymbol local, (SharedCaptureProblem Kind, string Detail) problem) => problem.Kind switch
    {
        SharedCaptureProblem.NotSendable => Catalog.SharedCaptureNotSendable(at, local.Name, problem.Detail, local.IsParameter),
        SharedCaptureProblem.NotCopyable => Catalog.SharedCaptureNotCopyable(at, local.Name, problem.Detail, local.IsParameter),
        SharedCaptureProblem.Mutable => Catalog.SharedCaptureMutable(at, local.Name, local.IsParameter),
        SharedCaptureProblem.Reference => Catalog.SharedCaptureReference(at, local.Name, problem.Detail, local.IsParameter),
        _ => Catalog.SharedCaptureView(at, local.Name, problem.Detail),
    };

    static bool InSharedClassMethod(FunctionSymbol lambda)
    {
        var enclosing = lambda.Enclosing;
        while (enclosing is { IsLambda: true })
            enclosing = enclosing.Enclosing;
        return enclosing?.ContainingType is { IsShared: true };
    }

    bool IsCapturedBySharedLambda(LocalSymbol local)
    {
        if (local.Owner is null)
            return false;
        for (var function = currentFunction; function is not null && function != local.Owner; function = function.Enclosing)
        {
            if (function.IsShared)
                return true;
        }
        return false;
    }

    /// <summary>Why an escaping lambda cannot capture a local, with the keyword or type that makes it so.</summary>
    static (CaptureProblem Problem, string Detail)? FindCaptureProblem(LocalSymbol local)
    {
        if (local.RefKind != RefKind.None)
            return (CaptureProblem.Reference, FunctionSymbol.RefPrefix(local.RefKind).Trim());
        if (local.Type.IsRefStruct)
            return (CaptureProblem.View, local.Type.ToString());
        if (local.Type.NonNull.IsNonEscapingFunction)
            return (CaptureProblem.NonEscaping, "");
        if (local.IsMutable)
            return (CaptureProblem.Mutable, "");
        return null;
    }
}
