# 0070. `Frozen<T>`: an immutable list that `freeze` moves a list's elements into

- Status: accepted (2026-11-09); implements the `freeze()` of [0016](0016-channels-and-locks.md)
- Spec: `concurrency.frozen`, `library.frozen`, `library.names`, `generics.bounds`

## Decision

```java unchecked
pub shared class Frozen<T extends sendable & copyable> implements ReadOnlySpannable<T> {
    pub static Frozen<T> copyOf(ReadOnlySpan<T> items);
    pub int length();
    pub in T operator [](int index);
    pub in? T get(int index);
    pub ReadOnlySpan<T> asReadOnlySpan();
}
// in List<T>
pub Frozen<T> freeze() where T extends sendable & copyable;
```

- **What it is.** A frozen list is a shared class that holds elements that never change. Its
  elements are sendable and copyable, so a thread that reads one copies a value that belongs to no
  thread, and no normal object's count changes.
- **`freeze` moves.** `List.freeze` hands the list's buffer to a new frozen list and leaves the list
  with no elements and no capacity. No element is copied. The buffer comes from the global heap
  (`memory::allocate`), so any thread may free it.
- **`freeze` ignores reference counts.** It empties the list whatever other references to the list
  exist, as 0016 decided. It panics if the list is borrowed, as `clear` does.
- **`copyOf` copies** the elements a span views, for an array, a fixed array, or part of a list,
  which cannot be emptied.
- **The module.** `Frozen<T>` is declared in `std::threading` ([0066](0066-threading-module.md)), in
  `frozen.wyl`.
- **The storage.** The class's one field is a package struct, `FrozenStorage<T>`, which holds the
  buffer's pointer and its length. `StdlibSymbols.Register` marks it as a sendable handle, as it marks
  `MutexState` ([0067](0067-mutex.md)), and `TypeSendable.IsSendableIn` now answers for a marked type
  before it walks the type's fields. Every member of `Frozen` is ordinary `unsafe` Wylow, as `List`'s
  members are, so the runtime and the interpreter have nothing of their own for it.
- **The indexer and `get`.** They return read-only references, as `ReadOnlySpan<T>`'s do. The
  indexer panics on an index out of bounds, and `get` returns null.
- **A `where` clause covers the signature.** `freeze` returns `Frozen<T>`, which needs `T` to be
  sendable and copyable, and only `freeze`'s own clause says so. The binder now checks a type
  written in a member's signature with the member's clause, as it already checked a type written in
  the member's body. Before this change, the binder bound the signature before the member's symbol
  existed, so it checked those types without the clause.

## Alternatives

- **A value computed on first use**, as Rust's
  [`OnceLock`](https://doc.rust-lang.org/std/sync/struct.OnceLock.html) is. A static is already
  computed once, before `main`, so laziness helps only a field of an object. A second thread that
  asks while the first computes would have to wait, and the wait would fall under the lock rule.
- **A frozen graph of any `T`.** Threads would share normal objects whose counts are not atomic. The
  runtime would have to mark each object of the graph so that every retain and release skips it,
  which adds a branch to every count change in every program.
- **`freeze` only for a list with one reference.** Whether an optimization removed a count change
  would decide whether a program panics ([0016](0016-channels-and-locks.md)).
- **A constructor that takes the list**, `new Frozen<T>(list)`. A constructor that empties its
  argument surprises a reader at the call, and `list.freeze()` names what happens to the list.
- **`copyOf` only.** Every frozen table would be built twice, once in the list and once in the copy.
  A C++ [`std::vector`](https://en.cppreference.com/w/cpp/container/vector/vector) gives its buffer
  to another vector by its move constructor in constant time, and `freeze` gives a list's buffer to a
  frozen list the same way.
- **A runtime handle, as `MutexState` is.** The elements would live in C++ storage the interpreter
  would have to mirror, and `List`'s buffer would have to be copied into it. A struct of a pointer
  and a length keeps the buffer where `List` put it.

## Why

A static is read by every thread, so its type must be sendable and copyable, and a `List` or an
array is neither. A table that a program computes once and then only reads, such as a lookup table
or a parsed configuration, needs a type that is both and still holds many elements. A frozen list is
that type. Moving the buffer makes freezing cost nothing beyond the list the program already built.
