using Wyl.Compiler.Semantics;

namespace Wyl.Generate;

/// <summary>
/// One generated expression: the source text, the exact type it has, and, for an integer, a range
/// every run stays in. A null range means any value of the type, which no constructor narrows.
/// </summary>
sealed record GenExpr(string Text, WylType Type, IntegerRange? Range = null, ExactFloat? Exact = null);
