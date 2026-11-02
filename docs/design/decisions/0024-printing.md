# 0024. Printing: opt-in `toString`, no cultures

- Status: accepted (2026-10-23)
- Spec: `expressions.interpolation`

## Decision

A type is printable only if it implements `Printable`. Format specifiers are C#'s, without the ones
that depend on a culture (`N`, `C`).

## Alternatives

- **A default `toString()`** for every type (Java's `Object.toString`).
- **Culture-dependent formatting** (C#'s `N`, `C`).

## Why

A type opts in, so printing never silently shows something like Java's `Node@1b6d3586`. Wylow has no
cultures, so output doesn't change with the machine it runs on.
