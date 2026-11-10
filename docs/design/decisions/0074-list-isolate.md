# 0074. A list moves to another thread through `isolate`, and `List<T>` stays the only growable array

- Status: accepted (2026-11-09)
- Spec: `concurrency.confinement`, `concurrency.sendable`, `concurrency.isolated`, `library.isolated`

## Decision

`List<T>` stays a class and stays the standard library's only growable array. No unique `Vec<T>` is
added. `list.isolate()`, where `T` is sendable, moves the list's elements into a new list inside an
`Isolated<List<T>>` without copying them. The list is left with no elements and no capacity, as
`freeze` leaves it. `isolate` panics if the list is borrowed.

The spec no longer reserves a place for a unique struct of the standard library that owns class
objects. The row for such a struct is gone from the sendable table, and an object changes threads
only inside an isolated graph.

## Alternatives

- **A unique `Vec<T>` that replaces `List<T>`.** Rust's
  [`Vec<T>`](https://doc.rust-lang.org/std/vec/struct.Vec.html) is a value with one owner and is
  `Send` when `T: Send`. Swift's [`Array`](https://developer.apple.com/documentation/swift/array) is
  a value type and is `Sendable` when its `Element` is. In Wylow, a `Vec<T>` would need two new
  language rules. First, a struct method cannot return a reference into its storage object today,
  because a place in an object that a field refers to is not in the receiver
  (`functions.ref-returns`). Second, a borrow of a `Vec` would have to reach its storage object, and
  a struct cannot be `Borrowable` (`memory.borrows.borrowable`). The check that a value moved to
  another thread owns nothing borrowed would become reachable, and it reads in-use bits, not borrow
  counters. Replacing `List` would also change 130 test files, `freeze`, and the cycle collector's
  container elements. A program that shares one list between objects would need a class around each
  `Vec`.
- **A `Vec<T>` beside `List<T>`.** It has every cost of the first alternative and gives the library
  two growable arrays, so it breaks the principle of one way to do things.
- **Swift's region-based isolation**
  ([SE-0414](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0414-region-based-isolation.md)).
  Decision 0017 already declined it: it needs a new flow analysis, and the cycle buffer of code that
  mixes the thread's objects with the graph's cannot be split cheaply.
- **Making `List<T>` sendable when its elements are.** A `List` is a class, so another reference to
  the same list could stay on the sending thread, and both threads would reach the list.

## Why

A unique `Vec<T>` was proposed so that a thread could fill a growable array with ordinary code and
then send it or put it in a mutex. Before this decision no program could do that. A `List` is not
sendable, and the lambda that builds an `Isolated<T>` captures only sendable, copyable values, so it
cannot take a list filled outside it. `freeze` only accepts copyable elements, and its result never
changes.

`isolate` fills that gap with a mechanism the library already has. `freeze` already moves a list's
buffer into another object and leaves the list empty, whatever other references to it exist, so no
reference to the old list can reach the moved elements. The elements are sendable, so they refer to
no normal object of the sending thread. The new list therefore starts a graph that nothing outside
refers to, which is what `Isolated<T>` requires. Unique elements are moved with the buffer, so they
need not be copyable.

The new list is made while the box's cycle buffer is the thread's, so that a release of it during
the call buffers it in the box's buffer, never in the sending thread's. A `std`-only `Isolated`
constructor runs a function that is not shared for that purpose, and the function captures only the
buffer's pointer, count, and capacity.

The decision keeps value semantics out of the library. A struct that holds a `List` still shares
the list when the struct is copied, as a class reference does everywhere else in Wylow.
