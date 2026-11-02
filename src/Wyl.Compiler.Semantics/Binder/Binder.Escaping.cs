using Wyl.Compiler.Diagnostics;
using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Semantics;

/// <summary>
/// Escaping functions (§6.7): which function types are escaping, and what an escaping lambda may
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
        if (!isParameter && type.IsNullable && type.NonNull.IsFunction)
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
        foreach (var annotation in s.Annotations)
            ReportMisplacedAnnotation(annotation);
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

    /// <summary>Whether a parameter or local of the type is declared <c>escaping</c>, which only one of function type can be.</summary>
    bool IsDeclaredEscaping(Location? escaping, WylType type, string what)
    {
        if (escaping is not { } at)
            return false;
        if (type.NonNull.IsFunction || type == WylType.Error)
            return true;
        Error(Catalog.EscapingOnNonFunction(at, what, type.ToString()));
        return false;
    }

    /// <summary>Reports an annotation used where it doesn't apply, or unknown altogether.</summary>
    void ReportMisplacedAnnotation(AnnotationSyntax annotation) => Error(annotation.Name is "CallerLocation" or "Acyclic" or "Repr" or "Pure"
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
        // Only a variable is non-escaping by its type; a lambda option is only because a variable
        // option made the choice so.
        var culprit = BoundTree.FunctionOptions(function)
            .Select(o => o is BoundConversion conversion ? conversion.Operand : o)
            .Select(o => o is BoundNonNull nonNull ? nonNull.Operand : o)
            .OfType<BoundLocalAccess>()
            .FirstOrDefault(o => o.Type.NonNull.IsNonEscapingFunction)
            ?? throw new InvalidOperationException("a non-escaping function that no variable makes so");
        var local = culprit.Local;
        Error(Catalog.NonEscapingStored(culprit.Location, local.Name, local.IsParameter, local.IsParameter && local.Owner?.IsLambda == true));
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

    /// <summary>Checks what the escaping lambdas in a body capture (§6.7).</summary>
    void CheckEscapingCaptures(BoundBlock body)
    {
        var reported = new HashSet<LocalSymbol>();
        bool reportedThis = false;
        foreach (var lambda in BoundTree.AllExpressions(body).OfType<BoundLambda>().Where(l => l.Function.IsEscaping))
        {
            var (locals, self) = BoundTree.Captures(lambda);
            foreach (var use in locals)
            {
                if (FindCaptureProblem(use.Local) is var (problem, detail) && reported.Add(use.Local))
                    Error(Catalog.EscapingCapture(use.Location, use.Local.Name, problem, use.Local.IsParameter, detail));
            }
            if (self is { Type.IsReference: false } && !reportedThis)
            {
                reportedThis = true;
                Error(Catalog.EscapingCaptureOfStructThis(self.Location));
            }
        }
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
