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
    /// <summary>The non-escaping form of a function type or nullable function type; any other type itself.</summary>
    static VplType NonEscaping(VplType type) =>
        type.IsNullable && type.NonNull.IsFunction ? type.NonNull.NonEscaping.Nullable : type.IsFunction ? type.NonEscaping : type;

    /// <summary>The escaping form of a function type or nullable function type; any other type itself.</summary>
    static VplType EscapingForm(VplType type) =>
        type.IsNullable && type.NonNull.IsFunction ? type.NonNull.Escaping.Nullable : type.IsFunction ? type.Escaping : type;

    static bool IsNonEscapingFunction(VplType type) => type.NonNull.IsFunction && !type.NonNull.IsEscaping;

    /// <summary>
    /// Only a parameter can hold a nullable non-escaping function: a variable would have to own the
    /// lambda it may hold, as a non-escaping variable does, while one that is null holds none.
    /// </summary>
    static bool IsNullableNonEscaping(VplType type) => type.IsNullable && IsNonEscapingFunction(type);

    /// <summary>
    /// The type a <c>?:</c> or switch expression's options are bound with: a choice of nullable
    /// function type is escaping, since a non-escaping one would refer to a function made only for it.
    /// </summary>
    static VplType? ChoiceTarget(VplType? type) => type is not null && IsNullableNonEscaping(type) ? EscapingForm(type) : type;

    const string NullableVariableMessage =
        "a variable of nullable function type must be declared '@Escaping'; only a parameter can hold a nullable non-escaping function";

    /// <summary>
    /// The type of a parameter or local: a function type, nullable or not, is non-escaping unless
    /// the declaration is <c>@Escaping</c>; a nullable local must be.
    /// </summary>
    VplType BindVariableType(TypeSyntax syntax, IReadOnlyList<AnnotationSyntax> annotations, bool isParameter)
    {
        var type = BindType(syntax, allowVoid: false);
        if (IsDeclaredEscaping(annotations, type, isParameter ? "parameter" : "variable"))
            return type;
        if (!isParameter && type.IsNullable && type.NonNull.IsFunction)
        {
            Error(syntax.Location, NullableVariableMessage);
            return VplType.Error;
        }
        return NonEscaping(type);
    }

    /// <summary>
    /// The type of a <c>var</c> local: its initializer's, in the escaping form if it is declared
    /// <c>@Escaping</c>. A nullable function must be.
    /// </summary>
    (VplType Type, BoundExpression Initializer) InferVariableType(VarDeclStmt s, BoundExpression initializer)
    {
        var type = initializer.Type;
        if (IsDeclaredEscaping(s.Annotations, type, "variable"))
            type = EscapingForm(type);
        else if (IsNullableNonEscaping(type))
        {
            Error(s.Location, NullableVariableMessage);
            return (VplType.Error, initializer);
        }
        return (type, Convert(initializer, type));
    }

    /// <summary>Whether a parameter or local of the type is declared <c>@Escaping</c>; reports annotations that don't apply.</summary>
    bool IsDeclaredEscaping(IReadOnlyList<AnnotationSyntax> annotations, VplType type, string what)
    {
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
        return escaping;
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
    void ReportNotEscaping(BoundExpression function)
    {
        // A lambda option is non-escaping only because a variable option made the choice so.
        var culprit = BoundTree.FunctionOptions(function)
            .Select(o => o is BoundConversion conversion ? conversion.Operand : o)
            .Select(o => o is BoundNonNull nonNull ? nonNull.Operand : o)
            .Where(o => IsNonEscapingFunction(o.Type))
            .OrderBy(o => o is BoundLocalAccess ? 0 : 1)
            .FirstOrDefault() ?? function;
        Error(culprit.Location, culprit is BoundLocalAccess { Local: var local }
            ? $"'{local.Name}' is a non-escaping {(local.IsParameter ? "parameter" : "variable")}, so it cannot be stored or returned; "
                + (local.IsParameter && local.Owner?.IsLambda == true
                    ? "declare the parameter '@Escaping' in the lambda's function type, as in '(@Escaping () -> void) -> void'"
                    : "declare it '@Escaping'")
            : "a non-escaping function cannot be stored or returned; only '@Escaping' parameters and variables can");
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
        IsNonEscapingFunction(from) && to.NonNull == from.NonNull.Escaping && (to.IsNullable || !from.IsNullable);

    /// <summary>
    /// The first argument that keeps a candidate from applying only by being non-escaping, if one
    /// does: saying so explains more than that no overload accepts the arguments.
    /// </summary>
    Argument? NonEscapingArgument(IEnumerable<MethodReference> candidates, List<Argument> arguments)
    {
        foreach (var method in candidates)
        {
            var failing = Enumerable.Range(0, arguments.Count).Where(i => !IsApplicable(method, arguments, i)).ToList();
            if (failing.Count > 0 && failing.All(i => !arguments[i].IsRef && method.ParameterRefKind(i) != RefKind.Ref
                    && IsEscapingMismatch(arguments[i].Expression.Type, method.ParameterType(i))))
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
            return $"which is {(local.RefKind == RefKind.In ? "an" : "a")} '{FunctionSymbol.RefPrefix(local.RefKind).Trim()}' {what}; copy its value to a local first";
        if (local.Type.IsRefStruct)
            return $"a '{local.Type}' that may not outlive what it views";
        if (IsNonEscapingFunction(local.Type))
            return $"which is a non-escaping {what}; declare it '@Escaping'";
        if (local.IsModified)
            return "since it changes after its declaration and the lambda would have only a copy; copy it to a variable that doesn't change";
        return null;
    }
}
