# 0079. One module, `std::iteration`, for the iterator interfaces and their functions

- Status: accepted (2026-11-10); moves the interfaces that 0075 and 0076 declared in `std`'s root
- Spec: `library.names`, `library.iterator`

## Decision

- The module `std::iteration` declares `Iterator<T>`, `RefIterator<T>`, and `InIterator<T>`. The
  lazy adapters and terminal operations of 0075 are extension methods in the same module.
- A program imports the module's names to use them: `import std::iteration::*;` brings in the
  interfaces and every extension, and `import std::iteration::Iterator;` brings in one name.
- `ListIterator<T>` and `ArrayIterator<T>` stay beside `List<T>` and `Array<T>` in `std`'s root.
  Each container's `iterator()` returns its own iterator type, so the iterator type belongs with its
  container.

## Alternatives

- **`std`'s root**, where 0075 put the interfaces. The adapters have short, common names such as
  `map`, `count`, and `first`. A program that imports `std::*` would bring all of them into each of
  its files.
- **`std::iter`**, the name of Rust's [`std::iter`](https://doc.rust-lang.org/std/iter/) and Go's
  [`iter`](https://pkg.go.dev/iter) package, which hold an iterator interface and its functions. It
  is shorter, but every other module of `std` is named with a full word.
- **`std::collections`**, after C#'s
  [`System.Collections.Generic`](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.ienumerable-1),
  which declares `IEnumerable<T>`. An iterator over a computed sequence is not a collection. C#
  itself declares its query functions apart, in
  [`System.Linq`](https://learn.microsoft.com/en-us/dotnet/api/system.linq.enumerable). The name
  would also move `List<T>` and `Array<T>` into the module now, and that question is still open.
- **`std::sequences`**, after Kotlin's
  [`kotlin.sequences`](https://kotlinlang.org/api/core/kotlin-stdlib/kotlin.sequences/). Wylow has no
  `Sequence` type, so the name refers to nothing a program declares.

## Why

A module groups the declarations that a program uses together (0066). A program that filters or
counts the elements of an iterator also names the iterator interfaces in its bounds. The name
`std::iteration` names the subject that the declarations share, as `std::threading` does, and the
interfaces leave `std`'s root before the root collects the adapters' common names.
