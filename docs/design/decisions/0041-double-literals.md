# 0041. An unsuffixed floating-point literal is a `double`

- Status: accepted (2026-11-03)
- Spec: `lexical.literals.float`

## Decision

`1.5` is a `double`; `1.5f` is a `float`. The `d` suffix stays, and is redundant. This replaces [0007](0007-float-literals.md).

## Alternatives

- **`float` by default** (0007): the common case was to be single precision.
- **Remove the `d` suffix**, for one way to write a `double`. Java keeps it ([JLS 3.10.2](https://docs.oracle.com/javase/specs/jls/se27/html/jls-3.html#jls-3.10.2)), and removing it gains little.

## Why

Java ([JLS 3.10.2](https://docs.oracle.com/javase/specs/jls/se27/html/jls-3.html#jls-3.10.2)), C# ([§6.4.5.4](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/lexical-structure#6454-real-literals)), and C++ ([\[lex.fcon\]](https://eel.is/c++draft/lex.fcon)) all make it `double`, and Java's conventions win toss-ups. 0007's reason was the arithmetic of graphics and simulation, and no design is justified by one kind of program. `double` does not convert to `float`, so `float f = 1.5;` is an error.
