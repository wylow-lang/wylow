# 0024. Printing: opt-in `toString`, no cultures

- Status: accepted (2026-10-23)
- Spec: `expressions.interpolation`

## Decision

A type is printable only if it implements `Printable`. Format specifiers are .NET's
[standard numeric format strings](https://learn.microsoft.com/en-us/dotnet/standard/base-types/standard-numeric-format-strings),
without the ones whose result depends on the current culture (`N`, `C`).

## Alternatives

- **A default `toString()`** for every type, like Java's
  [`Object.toString`](https://docs.oracle.com/en/java/javase/25/docs/api/java.base/java/lang/Object.html#toString()).
- **Culture-dependent formatting** (.NET's `N`, `C`).

## Why

A type opts in, so printing never silently shows something like Java's `Node@1b6d3586`. Wylow has no
cultures, so output doesn't change with the machine it runs on.
