# 0008. Arithmetic keeps its operands' type

- Status: accepted (2026-10-22)
- Spec: `expressions.arithmetic.operands`

## Decision

An arithmetic operator on two values of the same type produces that type: `byte + byte` is a
`byte`. Small types are not promoted to `int`.

## Alternatives

- **Promotion to `int`** (C, Java, C#): `byte + byte` is an `int`.

## Why

Promotion would make `byte c = a + b;` an error, and the cast everyone would write to fix it,
`(byte)(a + b)`, wraps silently, which would hide the overflow that debug builds otherwise catch.
Keeping the type keeps every integer operation checked, and generic code, compound assignment, and
`++` need no special cases.

The cost is that an intermediate result can overflow even where the final one would fit:
`(a + b) / 2` with two `byte`s overflows when `a + b` exceeds 255, also when the result is stored in
an `int`. Widening one operand first, `((int)a + b) / 2`, avoids it, and debug builds panic rather
than produce a wrong value.
