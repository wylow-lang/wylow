# 0007. An unsuffixed floating-point literal is a `float`

- Status: accepted (2026-10-19)
- Spec: `lexical.literals.float`

## Decision

`1.5` is a `float`; `1.5d` is a `double`.

## Alternatives

- **`double` by default**, with a suffix for `float` (`1.5f`): Java
  ([JLS 3.10.2](https://docs.oracle.com/javase/specs/jls/se27/html/jls-3.html#jls-3.10.2)), C#
  ([§6.4.5.4](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/lexical-structure#6454-real-literals)), and C++
  ([\[lex.fcon\]](https://eel.is/c++draft/lex.fcon)).

## Why

When this was decided, most of the arithmetic the language was meant for (graphics and simulation)
was single precision, so the common case got the short form.
