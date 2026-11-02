# 0007. An unsuffixed floating-point literal is a `float`

- Status: accepted (2026-10-19)
- Spec: `lexical.literals.float`

## Decision

`1.5` is a `float`; `1.5d` is a `double`.

## Alternatives

- **`double` by default** (Java, C#, C++), with a suffix for `float` (`1.5f`).

## Why

When this was decided, most of the arithmetic the language was meant for (graphics and simulation)
was single precision, so the common case got the short form.
