using Wyl.Compiler.Semantics;

namespace Wyl.Generate;

/// <summary>
/// One generated expression: the source text, the exact type it has, and, for an integer or a
/// <c>char</c>'s code point, a range every run stays in. A null range means any value of the type,
/// which no constructor narrows.
/// </summary>
/// <param name="Constant">
/// Whether the binder may read the text as an unsuffixed integer constant, which takes the type of
/// the operand beside it (docs/spec/13-expressions.md#expressions.arithmetic.operands);
/// over-approximated, since it only refuses.
/// </param>
sealed record GenExpr(string Text, WylType Type, IntegerRange? Range = null, ExactFloat? Exact = null, bool Constant = false);
