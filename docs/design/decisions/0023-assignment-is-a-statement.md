# 0023. Assignment is a statement

- Status: accepted (2026-10-19)
- Spec: `statements.assignment`

## Decision

`=`, compound assignments, `++`, and `--` are statements, not expressions.

## Alternatives

- **Assignment as an expression** (C, Java, C#): `a = b = c`, `f(x = 1)`, `arr[i++]`.

## Why

It removes a class of evaluation-order bugs.
