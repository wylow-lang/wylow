# 0017. Isolated object graphs

- Status: accepted (2026-11-01)
- Spec: `concurrency.isolated`

## Decision

`Isolated<T>` is a unique struct holding an object graph nothing outside refers to. It is made by a
non-escaping `shared fn`, opened with a `shared fn(ref T) -> R` whose result is sendable, and ended
with `take`. It has its own cycle buffer.

## Alternatives

- **Swift's region-based isolation**
  ([SE-0414](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0414-region-based-isolation.md)), which proves by flow analysis that an ordinary local's graph is
  disconnected from everything else, so a list filled with ordinary code could be sent without
  `Isolated`. It needs a new analysis with an exactly specified rule for when regions merge, and
  code that mixes the thread's objects with the graph's makes the cycle buffer impossible to split
  cheaply. It could be added later as a conversion from such a local to `Isolated<T>`.

## Why

It follows the reasoning of Pony's
[`recover`](https://tutorial.ponylang.io/reference-capabilities/recovering-capabilities.html)
expression, which "only has access to sendable values from the enclosing lexical scope", so what it
makes can't be referred to from outside. In Wylow that needs nothing new: without `mut` statics, and
with shared functions limited to sendable captures, a function can only reach what it was given.
