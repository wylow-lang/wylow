# 0005. Design principles

- Status: superseded by [0027](0027-design-principles.md) (2026-11-01)
- Spec: all

## Decision

The language is designed by these principles:

1. **Safe by default, fast by opt-in.** Every check can be turned off, but only explicitly.
2. **Failures are loud.** A failed safety check panics, which ends the program with a message and a
   stack trace.
3. **Errors are values.** No exceptions. Recoverable failures are `Result<T, E>`.
4. **Simple ownership.** No borrow checker and no lifetime annotations. Safety comes from a small
   set of structural rules (second-class references, reference counting, borrow counters).
5. **Data-oriented friendly.** Plain structs, arrays, and spans are the fast path and the parallel
   path.
6. **Explicit over clever.** Boilerplate is acceptable if it makes behavior obvious.

Wylow looks like Java (naming, class syntax, packages, generics syntax) and performs like C++
(value types, inline arrays, explicit layout, no tracing garbage collector, generics without
indirection). Specialized functionality, such as vector math, lives in the standard library or
other libraries rather than the language, which provides what such a library needs to be as fast as
built-ins (value types, indexers, layout control).

## Alternatives

None were recorded when the language was first written down. Later records name the alternatives
for each decision these principles shaped.

## Why

They are the stated goals the other decisions are checked against: a language with Java's
readability and C++'s control, in which no safe program has undefined behavior.
