# 0023. Assignment is a statement

- Status: accepted (2026-10-19)
- Spec: `statements.assignment`

## Decision

`=`, compound assignments, `++`, and `--` are statements, not expressions.

## Alternatives

- **Assignment as an expression**, as in Java ([JLS 15.26](https://docs.oracle.com/javase/specs/jls/se27/html/jls-15.html#jls-15.26))
  and C# ([§12.24](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/expressions#1224-assignment-operators)): `a = b = c`,
  `f(x = 1)`, `arr[i++]`.

## Why

It removes a class of evaluation-order bugs.
