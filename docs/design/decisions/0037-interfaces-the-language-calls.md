# 0037. Interfaces the language calls are ordinary interfaces

- Status: accepted (2026-11-02)
- Spec: `library.names`, `memory.borrows.borrowable`

## Decision

- `Printable`, `ReadOnlySpannable`, `Spannable`, and `Borrowable` are ordinary interfaces of `std`.
  A type takes part by implementing one; what is special is only that the language calls their
  methods: `toString` in string interpolation, `asReadOnlySpan` and `asSpan` in conversions to
  spans and in for-each loops, `beginBorrow` and `endBorrow` around borrows.
- `library.names` says this once for all of them. Each interface's own section says which types may
  implement it and whether a program may call its methods.
- A struct or enum can't be `Borrowable` through an interface that extends `Borrowable` either. The
  compiler checked only an interface listed by name, so `struct S implements I` with
  `interface I extends Borrowable` compiled, and its `beginBorrow` and `endBorrow` were never called.

## Alternatives

- **Keywords or annotations instead of interfaces**, such as an `@Borrowable` class annotation. A
  type would then mark itself in a second way besides `implements`, and generic code couldn't name
  the capability as a bound.
- **Only the standard library may implement them.** A program's own container built on raw memory
  in `unsafe` code (`unsafe/memory.wyl`) needs `Borrowable` for the same reason `List` does, and
  user types implement `Spannable` to be viewed and iterated (`spans/generic_borrow.wyl`).

## Why

These interfaces are explicit: a type implements them, and its declaration says so. Defining "an
interface the language calls" once keeps them from reading as special cases.

One special case of the standard library remains, and is left open in issue #6: the cycle
collector visits a class's elements through `asReadOnlySpan` only for `std`'s own classes
(`StdlibSymbols.ContainerElement`), since a user's span might view its own fields, which the
collector already visits. So a cycle through objects that a program's own raw-memory container
holds is never collected, while the same cycle through a `List` is (checked 2026-11-02, in the
interpreter and with MSVC). User containers are meant to be written this way, so a program's class
needs a way to give the collector its elements.
