# 0094. The standard library may declare a struct backed by a C++ class `unique`

- Status: accepted (2026-11-11)
- Spec: `interop.extern`, `interop.extern.std`

## Decision

A struct in an extern block cannot be declared `unique` or `ref`, as decision 0084 gives. The
standard library's extern blocks are exempt for a struct backed by a C++ class: such a struct may be
declared `unique`. `std::memory`'s `ElementVisitor<T>` is the struct that uses the exemption.

## Alternatives

- **Make `ElementVisitor<T>` copyable.** A program receives the visitor as a `ref` parameter of
  `visitElements`. If the program could copy the visitor, it could keep the copy and call `visit`
  after the collection ends, when the collector's state that the visitor points to no longer exists.
- **Allow `unique` on every struct backed by a C++ class.** Only the standard library declares such
  a struct, so this rule would differ from the exemption only in its wording. The exemption keeps
  every difference between the standard library's blocks and other blocks in one list.

## Why

A struct backed by a C++ class keeps its data in the C++ class, so the C++ class decides what a copy
means. The rule against `unique` protects a struct whose layout C++ also reads, and a struct backed
by a C++ class has no such layout. The standard library already decides alone which of these
structs exist, so the exemption adds no rule that another package must follow.
