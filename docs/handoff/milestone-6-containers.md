# Milestone 6: containers (done)

> Implemented as proposed, with these additions: `get -> value` property getters, `T?[]` for arrays of
> nullable elements, `Result.orPanic`, and a `// panic-here:` test directive. The spec chapters are
> now the reference; this file records the plan.

Goal: arrays, spans, `List<T>`, and for-each, with the containers written as **readable VPL
source** in a standard library. The compiler knows about them only through narrow hooks. Pools,
handles, `Map`, and `Box` follow in milestone 7, on the same hooks.

## Standard library layout

- `src/Vpl.Compiler/Stdlib/*.vpl`, one file per type (`Result.vpl`, `Span.vpl`, `Array.vpl`,
  `List.vpl`, `Memory.vpl`), replacing `Prelude.vpl`. All are compiled with every program until
  packages exist (milestone 8); then they become `vpl.*` packages and the auto-imported prelude.
- The binder finds the types it has hooks for (`Result`, `Span`, `ReadOnlySpan`, `Array`, the hook
  interfaces) in a table of stdlib symbols, looked up by name in stdlib sources only. This
  replaces the current `resultSymbol` special case.

## Hooks (compiler features)

1. **`unsafe` blocks and methods, with raw pointers.** `T*`, `p[i]` (an unchecked place), `p + n`,
   and a few intrinsics in `Memory.vpl` declared as `extern` and implemented in `vpl.h`:
   `allocate<T>(int count)` (uninitialized), `free(p)`, `initialize(p, value)` (construct in
   place), `drop(p)` (destroy in place), `copyTo`/`moveTo` for growth. Usable only inside
   `unsafe`. This is the spec's existing `unsafe` (01-safety.md 1.4), not a new concept.
2. **`ref` locals and `ref` returns.** `ref T x = place;` aliases a place for its scope. The open
   ref-return rule becomes: *a method may return `ref`/`in` only into its receiver's storage, and
   the result borrows the receiver.* Free functions cannot return refs (yet). While a borrow lives
   (a ref local's scope, a for-each loop, a call taking the ref or span, or the rest of the
   statement for a temporary), the receiver is pinned if it's an object, and borrow-counted if it
   implements `Borrowable` (hook 5). Unlike ref parameters, a ref local always pins its owner, even
   a local, since the function can reassign that local while the ref is alive.
3. **Indexers**, as already specified (06-syntax.md 6.6): `ref T this[int i] { ... }`, and `in T`
   for read-only access.
4. **Second-class structs**: `ref struct Span<T> { ... }` (C#'s term). A ref struct may be a local,
   parameter, or field of another ref struct, never a field of a normal type, a type argument, or
   captured. Like `ref`, it can be returned only from a method, and then borrows the receiver.
5. **Borrow counting**: `interface Borrowable { void beginBorrow(); void endBorrow(); }` in the
   stdlib. The compiler calls these around every borrow of a receiver that implements it. The
   counter and the "modified while borrowed" panic are ordinary VPL code in `List.vpl`.
6. **Span conversion**: a type with `Span<T> asSpan()` (interface `Spannable<T>`) converts
   implicitly to `Span<T>` and `ReadOnlySpan<T>`, and for-each iterates it through that span.
7. **`@CallerLocation`** on a function makes panics inside it report the caller's source location,
   so a failed `list[i]` points at the user's line, not `List.vpl`. This also unblocks
   `Result.orPanic`.
8. **Get-only properties** (`int count { get -> size; }`, 06-syntax.md 6.5), so `list.count` and
   `array.length` don't need to change from methods later.

## Types

- **`T[N]`**: built in (it needs a compile-time size). An inline value emitted as a `std::array`;
  copying copies the elements. Bounds-checked indexing, `length`, implicit conversion to spans.
  Default value needs a defaultable `T`; otherwise use an array literal `[a, b, c]`.
- **`T[]`**: sugar for the stdlib `final class Array<T>`, and `new T[n]` for its constructor (`T`
  must be defaultable). Never reallocates, so it is pinned but not borrow-counted.
- **`Span<T>` / `ReadOnlySpan<T>`**: stdlib ref structs holding a pointer and a length, with a
  checked indexer and `slice`. `Span<T>` converts to `ReadOnlySpan<T>`.
- **`List<T>`**: stdlib `final class` implementing `Borrowable` and `Spannable<T>`: `add`,
  `insert`, `removeAt`, `clear`, `count`, `capacity`, the indexer. Mutating while borrowed panics.

## For-each

`for (T x : e)`, `for (ref T x : e)`, `for (in T x : e)`, and `for (int i, ref T x : e)`, over
ranges (`0..n`, `0..=n`), `T[N]`, spans, and `Spannable` types. Iterating borrows `e` for the loop.

## Not in this milestone

Pools and handles (they also need `ref T?` and `is ref` patterns), `Map` (needs a hashing and
equality protocol), `Box`, moving `String` and `print` into the stdlib, `@Unchecked`, ref returns
from free functions, and array literals beyond `T[N]` initializers.

## Order of work

1. Stdlib file layout and the symbol table; move `Result`.
2. `T[N]`, indexing, for-each over ranges and `T[N]`.
3. `ref` locals, indexers, `ref` returns, get-only properties.
4. `unsafe`, pointers, `Memory` intrinsics, `@CallerLocation`.
5. `ref struct`, `Span`/`ReadOnlySpan`, span conversion.
6. `Borrowable`, `Array<T>`, `List<T>`, for-each over them.

Each step lands with tests (behavior and every error), passes on MSVC and g++, and updates the spec.
