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
            return value.ReferenceKind == lambda.ReferenceKind && value.ReferencedType is { } referenced && lambda.ReferencedType is { } other && MayBe(referenced, other);
        if (value.IsFunction || lambda.IsFunction)
        {
            return value.IsFunction && lambda.IsFunction
                && value.FunctionParameters.Count == lambda.FunctionParameters.Count
                && value.FunctionParameters.Zip(lambda.FunctionParameters).All(p => p.First.RefKind == p.Second.RefKind && MayBe(p.First.Type, p.Second.Type))
                && MayBe(value.FunctionReturnType!, lambda.FunctionReturnType!);
        }
        if (value.ElementType is { } element)
            return lambda.ElementType is { } other && value.IsPointer == lambda.IsPointer && value.FixedLength == lambda.FixedLength && MayBe(element, other);
        if (value.Symbol is { } symbol && value.TypeArguments.Count > 0)
            return lambda.Symbol == symbol && value.TypeArguments.Zip(lambda.TypeArguments).All(a => MayBe(a.First, a.Second));
        return value == lambda;
    }
}
