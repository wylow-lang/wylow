# 0009. Unique structs and enums, and moves

- Status: accepted (2026-11-01)
- Spec: `structs.unique`, `enums.unique`, `memory.moves`, `flow.moves`, `types.copyable`

## Decision

A `unique struct` or `unique enum` can't be copied, only moved; one mechanism serves both the need
for single ownership and the need to move data between threads. Only whole locals and by-value
parameters move; use after a move is a flow error; generic code copies a `T` only when `T extends
Copyable`. Unique structs have no destructors.

## Alternatives

- **Destructors on unique structs**, for resources such as file or device handles without a heap
  object. They need a runtime mark on a value in use, so that overwriting or destroying it while a
  method runs on it panics (a value has no reference count to keep it alive), exact rules for when a
  moved-from or partly moved value is destroyed, and a way to end a value without its destructor
  (Swift's `discard self`, [SE-0390](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0390-noncopyable-structs-and-enums.md)). Adding
  them later changes no existing program.
- **Moves of copyable values only as an optimization**, with no unique types: nothing could rely on
  a value having one owner.
- The parameter modes compare to Swift's
  ([SE-0377](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0377-parameter-ownership-modifiers.md)): by value is `consuming`, `in` is
  `borrowing`, `ref` is `inout`.

## Why

Uniqueness is the point: since no copy can exist, the language knows that nothing else refers to
what the value holds. That is what lets a value move to another thread, and it makes a big struct's
cost visible in the source: a copy is an error, not something the optimizer may or may not remove.
