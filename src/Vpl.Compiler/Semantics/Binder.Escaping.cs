using Vpl.Compiler.Diagnostics;
using Vpl.Compiler.Syntax;
using Vpl.Compiler.Text;

namespace Vpl.Compiler.Semantics;

/// <summary>
/// Escaping functions (§6.7): which function types are escaping, and what an escaping lambda may
/// capture. A non-escaping function may use its creator's locals by reference, so it can't be
/// stored; an escaping one copies what it captures, so what it captures must not change.
/// </summary>
public sealed partial class Binder
{
    static VplType NonEscaping(VplType type) => type.IsFunction ? type.NonEscaping : type;

    /// <summary>
    /// The type of a parameter or local, <paramref name="what"/>: a function type is non-escaping
    /// unless the declaration is <c>@Escaping</c>, and a nullable one must be.
    /// </summary>
    VplType BindVariableType(TypeSyntax syntax, IReadOnlyList<AnnotationSyntax> annotations, string what)
    {
        var type = BindType(syntax, allowVoid: false);
        bool escaping = false;
        foreach (var annotation in annotations)
        {
            if (annotation.Name != "Escaping")
                ReportMisplacedAnnotation(annotation);
            else if (type.NonNull.IsFunction || type == VplType.Error)
                escaping = true;
            else
                Error(annotation.Location, $"'@Escaping' can only be used on a {what} of function type, not '{type}'");
        }
        if (type.IsNullable && type.NonNull.IsFunction && !escaping)
        {
            Error(syntax.Location, $"a nullable function is always escaping, so this {what} must be declared '@Escaping'");
            return VplType.Error;
        }
        return escaping ? type : NonEscaping(type);
    }

    /// <summary>A <c>var</c> local declared <c>@Escaping</c> takes the escaping form of its initializer's function type.</summary>
    (VplType Type, BoundExpression Initializer) AnnotateInferred(IReadOnlyList<AnnotationSyntax> annotations, BoundExpression initializer)
    {
        var type = initializer.Type;
        foreach (var annotation in annotations)
        {
            if (annotation.Name != "Escaping")
                ReportMisplacedAnnotation(annotation);
            else if (type.IsFunction)
                type = type.Escaping;
            else if (!type.NonNull.IsFunction && type != VplType.Error)
                Error(annotation.Location, $"'@Escaping' can only be used on a variable of function type, not '{type}'");
        }
        return (type, Convert(initializer, type));
    }

    /// <summary>Reports an annotation used where it doesn't apply, or unknown altogether.</summary>
    void ReportMisplacedAnnotation(AnnotationSyntax annotation) => Error(annotation.Location, annotation.Name switch
    {
        "Escaping" => "'@Escaping' can only be used on parameters and local variables of function type; other functions are always escaping",
        "CallerLocation" => "'@CallerLocation' can only be used on functions, methods, and constructors",
        "Acyclic" => "'@Acyclic' can only be used on classes",
        _ => $"unknown annotation '@{annotation.Name}'",
    });

    /// <summary>
    /// Reports why a non-escaping function can't be converted to the escaping form, at the variable
    /// that makes it non-escaping where there is one, such as an option of a choice.
    /// </summary>
    void ReportNotEscaping(BoundExpression function, VplType to)
    {
        var fate = to.IsNullable ? "made nullable, since a nullable function is always escaping" : "stored or returned";
        // A lambda option is non-escaping only because a variable option made the choice so.
        var culprit = BoundTree.FunctionOptions(function)
            .Select(o => o is BoundConversion conversion ? conversion.Operand : o)
            .Where(o => o.Type.NonNull.IsFunction && !o.Type.NonNull.IsEscaping)
            .OrderBy(o => o is BoundLocalAccess ? 0 : 1)
            .FirstOrDefault() ?? function;
        Error(culprit.Location, culprit is BoundLocalAccess { Local: var local }
            ? $"'{local.Name}' is a non-escaping {(local.IsParameter ? "parameter" : "variable")}, so it cannot be {fate}; declare it '@Escaping'"
            : $"a non-escaping function cannot be {fate}; only '@Escaping' parameters and variables can");
    }

    /// <summary>
    /// For a duplicate overload, a hint when it differs from <paramref name="existing"/> only in
    /// which parameters are '@Escaping', since the two forms of a function type print alike.
    /// </summary>
    static string OverloadEscapingHint(FunctionSymbol function, FunctionSymbol existing) =>
        function.Parameters.Zip(existing.Parameters).Any(p => p.First.Type != p.Second.Type
            && p.First.Type.NonNull.IsFunction && p.Second.Type.NonNull.IsFunction
            && p.First.Type.NonNull.NonEscaping == p.Second.Type.NonNull.NonEscaping)
            ? $" as '{existing.Signature}'; overloads cannot differ only in which parameters are '@Escaping'"
            : "";

    /// <summary>
    /// For signatures that must match, a hint naming a parameter that is '@Escaping' in only one of
    /// them, since the two forms of a function type print alike.
    /// </summary>
    static string EscapingDifference(MethodReference method, MethodReference other, string otherName)
    {
        for (int i = 0; i < method.Symbol.Parameters.Count; i++)
        {
            var (mine, theirs) = (method.ParameterType(i), other.ParameterType(i));
            if (mine != theirs && mine.NonNull.IsFunction && theirs.NonNull.IsFunction
                && mine.IsNullable == theirs.IsNullable && mine.NonNull.NonEscaping == theirs.NonNull.NonEscaping)
            {
                var name = method.Symbol.Parameters[i].Name;
                return mine.NonNull.IsEscaping
                    ? $"; parameter '{name}' is '@Escaping' here but not in {otherName}"
                    : $"; parameter '{name}' is '@Escaping' in {otherName} but not here";
            }
        }
        return "";
    }

    /// <summary>Whether a value of type <paramref name="from"/> fails to convert only because it is non-escaping.</summary>
    static bool IsEscapingMismatch(VplType from, VplType to) =>
        from.IsFunction && !from.IsEscaping && to.NonNull == from.Escaping;

    /// <summary>
    /// The first argument that keeps a candidate from applying only by being non-escaping, if one
    /// does: saying so explains more than that no overload accepts the arguments.
    /// </summary>
    (Argument Argument, VplType Parameter)? NonEscapingArgument(IEnumerable<MethodReference> candidates, List<Argument> arguments)
    {
        foreach (var method in candidates)
        {
            var failing = Enumerable.Range(0, arguments.Count).Where(i => !IsApplicable(method, arguments, i)).ToList();
            if (failing.Count > 0 && failing.All(i => !arguments[i].IsRef && method.ParameterRefKind(i) != RefKind.Ref
                    && IsEscapingMismatch(arguments[i].Expression.Type, method.ParameterType(i))))
                return (arguments[failing[0]], method.ParameterType(failing[0]));
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

    /// <summary>Marks the local that a place being changed lies in, if any; see <see cref="LocalSymbol.IsModified"/>.</summary>
    static void NoteModified(BoundExpression place)
    {
        switch (place)
        {
            case BoundLocalAccess access:
                access.Local.IsModified = true;
                break;
            case BoundFieldAccess { Receiver.Type.IsReference: false } field:
                NoteModified(field.Receiver);
                break;
            case BoundIndex { Target.Type.IsFixedArray: true } index:
                NoteModified(index.Target);
                break;
            case BoundCall { Method.Symbol.ReturnRefKind: RefKind.Ref, Receiver: { Type.IsReference: false } receiver }:
                NoteModified(receiver);
                break;
        }
    }

    /// <summary>
    /// Checks what the escaping lambdas in a body capture, once the whole body is bound and so every
    /// change to a local is known (§6.7).
    /// </summary>
    void CheckEscapingCaptures(BoundBlock body)
    {
        var reported = new HashSet<object>();
        foreach (var lambda in BoundTree.AllExpressions(body).OfType<BoundLambda>().Where(l => l.Function.IsEscaping))
        {
            var (locals, self) = BoundTree.Captures(lambda);
            foreach (var use in locals)
            {
                if (CaptureProblem(use.Local) is { } problem && reported.Add(use.Local))
                    Error(use.Location, $"an escaping lambda cannot capture '{use.Local.Name}', {problem}");
            }
            if (self is { Type.IsReference: false } && reported.Add(self.Type))
                Error(self.Location, "an escaping lambda cannot capture 'this' in a struct's method or constructor, since it is a reference to the struct; copy what the lambda needs to a local first");
        }
    }

    static string? CaptureProblem(LocalSymbol local)
    {
        var what = local.IsParameter ? "parameter" : "variable";
        if (local.RefKind != RefKind.None)
            return $"which is {(local.RefKind == RefKind.In ? "an" : "a")} '{FunctionSymbol.RefPrefix(local.RefKind).Trim()}' {what}; copy its value to a local first";
        if (local.Type.IsRefStruct)
            return $"a '{local.Type}' that may not outlive what it views";
        if (local.Type.IsFunction && !local.Type.IsEscaping)
            return $"which is a non-escaping {what}; declare it '@Escaping'";
        if (local.IsModified)
            return "since it changes after its declaration and the lambda would have only a copy; copy it to a variable that doesn't change";
        return null;
    }
}
