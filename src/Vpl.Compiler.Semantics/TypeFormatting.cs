namespace Vpl.Compiler.Semantics;

/// <summary>Facts about how a value of a type is written as text.</summary>
public static class TypeFormatting
{
    extension(VplType type)
    {
        /// <summary>
        /// Whether the runtime writes a value of the type as text itself, for <c>print</c>,
        /// interpolation, and <c>toString</c>, instead of calling the type's own method: the
        /// primitives and <c>String</c>, and their nullables (spec 06-syntax.md, String interpolation).
        /// </summary>
        public bool IsFormatted => Builtins.FormattedValues.Contains(type.NonNull);
    }
}
