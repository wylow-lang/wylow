# 0027. Design principles

- Status: accepted (2026-11-01)
- Spec: all; chapter 1 (`introduction`) states what they promise

## Decision

The language is designed by these principles:

1. **Safe by default.** Outside `unsafe` code no program has undefined behavior
   (`introduction.undefined`). No annotation, context, or flag turns a check off: an operation that
   skips one is a named unsafe function, such as `getUnchecked(i)`
   ([0025](0025-unsafe-permits.md)). The one check a build mode changes is integer overflow, which
   panics in debug builds and wraps in release builds (`introduction.build-modes`); both are
   defined.
2. **Failures are loud.** A failed run-time check panics: the program ends, and the implementation
   reports a message and the location of the panic (`introduction.panics`).
3. **Errors are values.** There are no exceptions. A failure the caller is expected to handle is a
   `T?` or a `Result<T, E>` ([0014](0014-panicking-and-non-panicking.md)).
4. **Simple ownership.** No borrow checker and no lifetime annotations. Safety comes from a small
   set of structural rules: second-class references, reference counting, and borrow counters
   (`memory`).
5. **Plain data first.** Structs, arrays, and spans are the fast path and the parallel path.
6. **Explicit over clever.** Boilerplate is acceptable if it makes behavior obvious.

Wylow is general-purpose. It has value types, inline arrays, control over layout, and no tracing
garbage collector. Specialized functionality lives in
libraries, and the language provides what a library needs to be as fast as a built-in would be:
value types, indexers, and layout control.

## Alternatives

None were recorded when the principles were first written down. Later records name the
alternatives for each decision these principles shaped.

## Why

They are the goals the other decisions are checked against. This record replaces
[0005](0005-design-principles.md), which was copied unchanged from the old README and contradicted
later decisions: its "every check can be turned off, but only explicitly" contradicts [0025](0025-unsafe-permits.md), under which nothing
turns a check off, and it promised a stack trace on a panic, where chapter 1 promises a message
and a location. Its comparison of the language's look and performance with two others named no
features and is cut.
