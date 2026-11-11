# 0089. The module `std::collections` holds `List<T>` and the other collections

- Status: accepted (2026-11-11)
- Spec: `library.names`

## Decision

- The module `std::collections` declares `List<T>` and `ListIterator<T>`. The maps and sets that
  later decisions add are declared there too.
- A program imports a collection from the module: `import std::collections::List;`.
- `Array<T>`, `ArrayIterator<T>`, `Span<T>`, and `ReadOnlySpan<T>` stay in `std`'s root. The
  language names `Array<T>` for the type `T[]`, and the span types for spans, so a program uses them
  without an import.

## Alternatives

- **Keep `List<T>` in the root and put only the new maps and sets in `std::collections`.** No program
  would change, but one family of declarations would be split across two modules, which the
  convention for `std`'s modules in `docs/design/working.md` exists to prevent.
- **Declare `List<T>` in `std::collections` and re-export it from the root** with `pub import`. Two
  paths would then name one type, and a program could write either.
- **Move `Array<T>` too.** A program writes `int[]` rather than `Array<int>`, so the name `Array`
  is seldom written, and the language names the declaration. A name that the language depends on
  belongs in the root, as `String` and `Result` do.

## Why

`docs/design/working.md` puts a declaration that is one of a family, such as a collection, in a
module named for the family's subject. `std` is about to gain hash maps and sets, so the family gets
its module before it grows, and `List<T>` moves while one type moves rather than several. Decision
0079 left the question open when it named `std::iteration`. The compiler names no collection by
name, so the move changes the import of each program that uses `List`, and nothing in the compiler.
