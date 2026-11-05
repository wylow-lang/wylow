using Wyl.Compiler.Syntax;
using Wyl.Compiler.Text;

namespace Wyl.Compiler.Diagnostics;

// Chapter 11, generics.
public static partial class Catalog
{
    [Error(195, "generics.arguments")]
    public static Diagnostic TypeArgumentsOnName(Location at) =>
        Make(at, "type arguments can only be used in a call or construction");

    [Error(196, "generics.arguments")]
    public static Diagnostic TypeArgumentsOnMember(Location at) =>
        Make(at, "type arguments can only be used in a call");

    [Error(191, "generics.parameters")]
    public static Diagnostic ConstructTypeParameter(Location at, string name) =>
        Make(at, $"cannot construct type parameter '{name}'");

    [Error(197, "generics.arguments")]
    public static Diagnostic VariantTypeArguments(Location at, string type, string variant) =>
        Make(at, "type arguments belong to the enum, not its variant").Help($"write '{type}<...>.{variant}(...)'");

    [Error(192, "generics.parameters")]
    public static Diagnostic DuplicateTypeParameter(Location at, string name) =>
        Make(at, $"type parameter '{name}' is already defined");

    [Error(193, "generics.parameters")]
    public static Diagnostic HidingTypeParameter(Location at, string name) =>
        Make(at, $"type parameter '{name}' hides the type parameter '{name}' of its type");

    [Error(207, "generics.bounds")]
    public static Diagnostic NonInterfaceBound(Location at, string bound) =>
        Make(at, $"'{bound}' cannot be a bound").Note("a type parameter can only be bounded by interfaces");

    [Error(194, "generics.parameters")]
    public static Diagnostic GenericVirtualMethod(Location at) =>
        Make(at, "generic methods cannot be 'virtual', 'override', or 'abstract'");

    [Error(199, "generics.arguments")]
    public static Diagnostic NotGeneric(Location at, string name) =>
        Make(at, $"type '{name}' takes no type arguments");

    [Error(200, "generics.arguments")]
    public static Diagnostic TypeArgumentCount(Location at, string name, int expected, int found) =>
        Make(at, $"type arguments of '{name}': expected {expected}, found {found}");

    [Error(208, "generics.bounds")]
    public static Diagnostic BoundNotSatisfied(Location at, string argument, string bound, string parameter) =>
        Make(at, $"'{argument}' does not implement '{bound}'").Note($"type parameter '{parameter}' requires it");

    [Error(201, "generics.arguments.inference")]
    public static Diagnostic CannotInferTypeArgument(Location at, string parameter, string function) =>
        Make(at, $"cannot infer type argument '{parameter}' of '{function}'").Help($"specify it, as in '{function}<...>(...)'");

    [Error(202, "generics.arguments.inference")]
    public static Diagnostic InferFromVoid(Location at, string parameter, string function) =>
        Make(at, $"cannot infer type argument '{parameter}' of '{function}' from a 'void' value")
            .Note("a call that returns nothing cannot be an argument");

    [Error(203, "generics.arguments.inference")]
    public static Diagnostic InferFromNull(Location at, string parameter, string function) =>
        Make(at, $"cannot infer type argument '{parameter}' of '{function}' from 'null'").Help($"specify it, as in '{function}<...>(...)'");

    [Error(204, "generics.arguments.inference")]
    public static Diagnostic ConflictingInference(Location at, string parameter, string function, IEnumerable<string> types) =>
        Make(at, $"cannot infer type argument '{parameter}' of '{function}'")
            .Note($"it could be {string.Join(" or ", types.Select(t => $"'{t}'"))}");

    [Error(205, "generics.arguments.inference")]
    public static Diagnostic InferFromVoidLambda(Location at, string parameter, string function) =>
        Make(at, $"cannot infer type argument '{parameter}' of '{function}' from a lambda that returns no value");

    [Error(206, "generics.arguments.inference")]
    public static Diagnostic AmbiguousLambdaInference(Location at, string parameter, string function) =>
        Make(at, $"cannot infer type argument '{parameter}' of '{function}' from a lambda")
            .Note("other overloads also take these arguments").Help($"specify it, as in '{function}<...>(...)'");

    [Error(453, "generics.bounds")]
    public static Diagnostic PropertyNotSatisfied(Location at, string argument, string property, string parameter) =>
        Make(at, $"'{argument}' is not '{property}'").Note($"type parameter '{parameter}' requires it");

    [Error(454, "generics.bounds")]
    public static Diagnostic WhereNotAllowed(Location at, string where, string? help = null) =>
        help is null ? Make(at, $"a 'where' clause cannot {where}") : Make(at, $"a 'where' clause cannot {where}").Help(help);

    [Error(455, "generics.bounds")]
    public static Diagnostic WhereParameterUnknown(Location at, string name, string owner, bool declaredHere) =>
        Make(at, $"'{name}' is not a type parameter of '{owner}'")
            .Help(declaredHere ? $"put the bound on '{name}' where it is declared" : "a member can only bound its type's type parameters");

    [Error(456, "generics.bounds")]
    public static Diagnostic MemberBoundNotSatisfied(Location at, string argument, string bound, string member, bool property) =>
        Make(at, property ? $"'{argument}' is not '{bound}'" : $"'{argument}' does not implement '{bound}'").Note($"'{member}' requires it");
}
