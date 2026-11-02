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


    const string NonEscapingMutMessage =
        "a non-escaping function cannot be 'mut', since it keeps the function it was given; declare it 'escaping' to reassign it";

    /// <summary>
    /// Only a parameter can hold a nullable non-escaping function: a variable would have to own the
    /// lambda it may hold, as a non-escaping variable does, while one that is null holds none.
    /// </summary>

    /// <summary>
    /// The type a <c>?:</c> or switch expression's options are bound with: a choice of nullable
    /// function type is escaping, since a non-escaping one would refer to a function made only for it.
    /// </summary>
    static WylType? ChoiceTarget(WylType? type) => type is not null && type.IsNullableNonEscapingFunction ? EscapingForm(type) : type;

    const string NullableVariableMessage =
        "a variable of nullable function type must be declared 'escaping'; only a parameter can hold a nullable non-escaping function";

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
            Error(syntax.Location, NullableVariableMessage);
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
            Error(s.Location, NullableVariableMessage);
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
        Error(at, $"'escaping' can only be declared on a {what} of function type, not '{type}'");
        return false;
    }

    /// <summary>Reports an annotation used where it doesn't apply, or unknown altogether.</summary>
    void ReportMisplacedAnnotation(AnnotationSyntax annotation) => Error(annotation.Location, annotation.Name switch
    {
        "CallerLocation" => "'@CallerLocation' can only be used on functions, methods, and constructors",
        "Acyclic" => "'@Acyclic' can only be used on classes",
        "Repr" => "'@Repr' can only be used on enums",
        "Pure" => "'@Pure' can only be used on extern functions of the standard library",
        _ => $"unknown annotation '@{annotation.Name}'",
    });

    void CheckNoArguments(AnnotationSyntax annotation)
    {
        if (annotation.Arguments.Count > 0)
            Error(annotation.Location, $"'@{annotation.Name}' takes no arguments");
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
        Error(culprit.Location, $"'{local.Name}' is a non-escaping {(local.IsParameter ? "parameter" : "variable")}, so it cannot be stored or returned; "
            + (local.IsParameter && local.Owner?.IsLambda == true
                ? "declare the parameter 'escaping' in the lambda's function type, as in 'fn(escaping fn() -> void) -> void'"
                : "declare it 'escaping'"));
    }

    /// <summary>
    /// For a duplicate overload, a hint when it differs from <paramref name="existing"/> only in
    /// which parameters are 'escaping', since the two forms of a function type print alike.
    /// </summary>
    static string OverloadEscapingHint(FunctionSymbol function, FunctionSymbol existing) =>
        function.Parameters.Zip(existing.Parameters).Any(p => p.First.Type != p.Second.Type
            && p.First.Type.NonNull is FunctionType first && p.Second.Type.NonNull is FunctionType second
            && first.NonEscaping == second.NonEscaping)
            ? $" as '{existing.Signature}'; overloads cannot differ only in which parameters are 'escaping'"
            : "";

    /// <summary>
    /// For signatures that must match, a hint naming a parameter that is 'escaping' in only one of
    /// them, since the two forms of a function type print alike.
    /// </summary>
    static string EscapingDifference(MethodReference method, MethodReference other, string otherName)
    {
        for (int i = 0; i < method.Symbol.Parameters.Count; i++)
        {
            var (mine, theirs) = (method.ParameterType(i), other.ParameterType(i));
            if (mine != theirs && mine.NonNull is FunctionType myFunction && theirs.NonNull is FunctionType theirFunction
                && mine.IsNullable == theirs.IsNullable && myFunction.NonEscaping == theirFunction.NonEscaping)
            {
                var name = method.Symbol.Parameters[i].Name;
                return mine.NonNull.IsEscaping
                    ? $"; parameter '{name}' is 'escaping' here but not in {otherName}"
                    : $"; parameter '{name}' is 'escaping' in {otherName} but not here";
            }
        }
        return "";
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
                if (CaptureProblem(use.Local) is { } problem && reported.Add(use.Local))
                    Error(use.Location, $"an escaping lambda cannot capture '{use.Local.Name}', {problem}");
            }
            if (self is { Type.IsReference: false } && !reportedThis)
            {
                reportedThis = true;
                Error(self.Location, "an escaping lambda cannot capture 'this' in a struct's method or constructor, since it is a reference to the struct; copy what the lambda needs to a local first");
            }
        }
    }

    static string? CaptureProblem(LocalSymbol local)
    {
        var what = local.IsParameter ? "parameter" : "variable";
        if (local.RefKind != RefKind.None)
            return $"which is {WithArticle(FunctionSymbol.RefPrefix(local.RefKind).Trim())} {what}; copy its value to a local first";
        if (local.Type.IsRefStruct)
            return $"a '{local.Type}' that may not outlive what it views";
        if (local.Type.NonNull.IsNonEscapingFunction)
            return $"which is a non-escaping {what}; declare it 'escaping'";
        if (local.IsMutable)
            return "which is 'mut', and the lambda would have only a copy; copy it to a variable that isn't 'mut'";
        return null;
    }
}
