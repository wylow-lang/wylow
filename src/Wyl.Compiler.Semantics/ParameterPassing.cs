using Wyl.Compiler.Syntax;

namespace Wyl.Compiler.Semantics;

/// <summary>
/// Which by-value parameters are passed borrowed: the callee refers to the value the caller passes
/// instead of taking a copy of its own (docs/design/decisions/0111). No program can tell the
/// difference, because the caller holds every by-value argument as a temporary until its statement
/// completes (spec <c>memory.release.temporaries</c>), so the callee's copy would never be the last.
/// </summary>
public static class ParameterPassing
{
    /// <summary>
    /// Whether <paramref name="parameter"/> of <paramref name="function"/> is passed borrowed. Its type
    /// must be copyable, may own an object, and must neither be nor contain a type parameter, so that
    /// one C++ form serves every instantiation; a borrow of any other type saves nothing. An extern
    /// function takes its arguments as its C++ declaration does. A lambda and a function value take
    /// theirs by value, because a function type has one C++ form wherever it is written, including
    /// where its parameter types are type parameters. A <c>mut</c> parameter is passed borrowed too,
    /// and the callee copies it into a variable of its own.
    /// </summary>
    public static bool Borrows(FunctionSymbol function, LocalSymbol parameter) =>
        !function.IsBuiltin && !function.IsLambda && !function.IsFunctionValue
        && parameter.RefKind == RefKind.None
        && parameter.Type is not NullableReferenceType
        && !parameter.Type.NonNull.IsNonEscapingFunction
        && !parameter.Type.ContainsTypeParameters
        && parameter.Type.IsCopyableIn(null)
        && parameter.Type.MayOwnObjects;
}
