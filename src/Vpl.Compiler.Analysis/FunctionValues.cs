using Vpl.Compiler.Semantics;

namespace Vpl.Compiler.Analysis;

/// <summary>
/// Which lambdas a function value may be, for whole-program analyses: every function value is a
/// lambda of the program (a function reference is one too), converted only from escaping to not,
/// so a value can only be a lambda of its own type.
/// </summary>
public static class FunctionValues
{
    /// <summary>
    /// Whether a value of type <paramref name="value"/> may be a lambda of type <paramref name="lambda"/>.
    /// Escaping is ignored, since it converts, and so is the nullability of references, which C++
    /// doesn't see. A type parameter on either side may be anything, since generic code may be
    /// instantiated with any type.
    /// </summary>
    public static bool MayBe(VplType value, VplType lambda)
    {
        value = value.WithoutReferenceNullability;
        lambda = lambda.WithoutReferenceNullability;
        if (value.NonNull.IsTypeParameter || lambda.NonNull.IsTypeParameter)
            return true;
        if (value.IsNullable || lambda.IsNullable)
            return value.IsNullable && lambda.IsNullable && MayBe(value.NonNull, lambda.NonNull);
        if (value.IsNullableReference || lambda.IsNullableReference)
            return value is NullableReferenceType reference && lambda is NullableReferenceType other && reference.Kind == other.Kind && MayBe(reference.Referenced, other.Referenced);
        if (value.IsFunction || lambda.IsFunction)
        {
            return value is FunctionType function && lambda is FunctionType other
                && function.Parameters.Count == other.Parameters.Count
                && function.Parameters.Zip(other.Parameters).All(p => p.First.RefKind == p.Second.RefKind && MayBe(p.First.Type, p.Second.Type))
                && MayBe(function.ReturnType, other.ReturnType);
        }
        if (value is PointerType pointer)
            return lambda is PointerType other && MayBe(pointer.Element, other.Element);
        if (value is FixedArrayType array)
            return lambda is FixedArrayType other && array.Length == other.Length && MayBe(array.Element, other.Element);
        if (value.Symbol is { } symbol && value.TypeArguments.Count > 0)
            return lambda.Symbol == symbol && value.TypeArguments.Zip(lambda.TypeArguments).All(a => MayBe(a.First, a.Second));
        return value == lambda;
    }
}
