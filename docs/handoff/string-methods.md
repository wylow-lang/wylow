# `String`'s methods in Wylow

This is the next piece of work, and it comes before interop (`docs/handoff/interop-slice-1.md`),
because interop reuses the emitter form that this work builds. Implement decision 0078 with the floor
that decision 0088 sets. The spec rules are in `interop.extern.extern-structs`, `interop.strings`,
`types.string`, and `library.memory`.

Write the declarations in today's syntax, `pub extern struct String` with `extern` on each native
method. Interop slice 1 moves every declaration of `std` into extern blocks (decision 0084), so this
work does not change the syntax.

## 1. `memory::compare` and `memory::equal`

- Declare both functions in `Stdlib/memory/memory.wyl` as safe, `@Pure` externs, with doc comments
  as `library.memory` states them.
- Implement `wyl::memory::compare` and `wyl::memory::equal` in the runtime with `std::memcmp`. Each
  function returns early when a length is 0, because `memcmp` is undefined for a null pointer even
  with a count of 0, and an empty span's pointer may be null. `compare` returns -1, 0, or 1, never
  `memcmp`'s own result.
- Implement both functions in the interpreter's intrinsics, by their C++ names.
- Tests: equal spans, a smaller and a larger byte, a byte above 127 (bytes compare as unsigned), a
  prefix in each order, two empty spans, and one empty span.

## 2. Methods with bodies in extern structs

Done: `FunctionSymbol.IsExternStructBody` and `wyl::MethodsOf<T>` (`docs/design/architecture.md`),
with `isEmpty` and `contains` moved into `string.wyl` as the first methods with bodies.

## 3. The floor of `String`

The floor that stays in C++ is `byteLength`, `cstr`, `toString`, and `copyOf`:

- `pub unsafe byte* cstr()` returns the pointer to the bytes, which are null-terminated. `slice`
  copies today (`runtime/wyl/strings.h`), so every `String` stays null-terminated, and `cstr()` never
  copies. Keep that true.
- `package unsafe static String copyOf(ReadOnlySpan<byte> bytes)` copies bytes that the caller
  guarantees are valid UTF-8 into a new `String`.

Write these in `string.wyl` on top of the floor and `std::memory`, and delete each one from
`runtime/wyl/core.h` and from the interpreter's intrinsics in the same change:

- `bytes` (`ReadOnlySpan<byte>(cstr(), byteLength())` in an `unsafe` block), `charCount`,
  `charAt`, and `nextChar`.
- `startsWith` and `endsWith`: a length check, then `memory::equal` on a slice of `bytes()`.
- `indexOf`: a loop over `memory::equal`.
- `compareTo`: `memory::compare(bytes(), other.bytes())`.
- `slice`: the bounds and character-boundary checks with today's panic messages, then `copyOf`. It
  keeps `@CallerLocation`.
- The new `pub unsafe static String fromCString(byte* p)`: find the terminator, check the UTF-8, and
  call `copyOf`. It panics on invalid UTF-8, and the message names the offset of the first bad byte.

Notes:

- The interpreter must give `cstr()` a pointer that `ReadOnlySpan`'s constructor and `p[i]` accept:
  a `PointerValue` into a read-only buffer over the string's bytes (`ValueKinds.cs`).
- `bytes()` built in Wylow lets the runtime stop naming std's `ReadOnlySpan`
  (`StdlibSymbols.Register` marks it `IsNamedByRuntime`, and `TypeSymbol.RuntimeName` gives the
  name). Remove that hook if nothing else needs it.
- The pipeline drops the functions of `std` that a program cannot reach (`Reached.Find`). The emitter
  or the interpreter may call a moved method outside the IR, for example to print a `String`. If one
  does, add the method to the roots in `Reached.IsRoot`, and to the architecture row "Every program
  pays only for the `std` it reaches".

Tests: every moved method keeps its tests in `tests/cases/values/` and passes in every mode, and the
generated C++ of a program that calls them compiles without warnings. `fromCString` and `cstr` get
tests of their own, including invalid UTF-8 and an empty string.

## What the iteration session found

- A test run used to take about five minutes, because every program verified all of `std`. A run of
  the whole suite in the interpreter now takes about two and a half minutes. If the time grows again, profile
  one `wyl check` with `dotnet-trace` before guessing.
- A visible member still hides an extension of the same name (decision 0083). `List` has `count()`
  and `first()`, so a program reaches the span operations `count(test)` and `first(test)` on a list
  through `asReadOnlySpan()`.
- `Frozen<T>` has no iterator yet. Decision 0082 says that it would give an `InIterator`.

## Open for the user

- The roadmap (issue #6) still needs the updates of the extension-methods and iteration sessions.
  The user applies them.
