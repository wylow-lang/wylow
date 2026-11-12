# 0096. `void*` converts from every pointer, and `byteLength()` is an `int` that panics on overflow

- Status: accepted (2026-11-12)
- Spec: `types.pointers`, `conversions.pointers`, `unsafe.pointers`, `interop.buffers`

## Decision

A pointer of any type converts implicitly to `void*`, in safe code as well as in unsafe code. A
`void*` converts to another pointer type only by a cast, which is unsafe (`WYL0517`). It is an error
(`WYL0520`) to index a `void*` or to do arithmetic on it, in unsafe code too. A `void*` compares
with a pointer of any type.

`Span<T>.byteLength()` and `ReadOnlySpan<T>.byteLength()` return an `int`. They panic if the size
in bytes is greater than the largest `int`.

## Alternatives

- **The conversion to `void*` needs unsafe code.** The conversion only forgets the type, and
  nothing can be read or written through the result without a cast, which is unsafe. A safe
  conversion therefore permits nothing new, while unsafe code around every call that takes a
  `void*` would hide the casts that do need review.
- **Arithmetic on a `void*` that moves by bytes.** C++ requires a pointer to a complete object type
  for `+` and `-` ([\[expr.add\]/1](https://eel.is/c++draft/expr.add#1)), so the emitted C++ would
  need its own cast. A program that wants to move by bytes casts to `byte*` first, and the cast
  shows the reader which storage type the program assumes.
- **`byteLength()` returns a `long` or an `nuint`.** Lengths are `int` ([0006](0006-lengths-are-int.md)),
  and the C functions that take a buffer's size often take an `int`, as SQLite's
  `sqlite3_bind_blob` does ([C API](https://sqlite.org/c3ref/bind_blob.html)). A span whose elements
  take more than 2 GiB is rare, and the panic reports it instead of returning a wrong size.
- **`byteLength()` wraps on overflow in release builds, as `int` arithmetic does.** If a wrapped size
  is given to a C function, the function reads or writes the wrong number of bytes, so the check
  stays in every build mode.

## Why

`void*` exists to cross the boundary: a C function takes storage of any type, or keeps a context
that it hands back later. The rules let safe code pass any pointer to such a function, and they
keep every assumption about what the storage holds in an unsafe cast.
