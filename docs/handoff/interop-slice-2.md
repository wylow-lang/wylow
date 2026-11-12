# Interop, slice 2: the SQLite binding

Slice 1 (`interop-slice-1.md`) built extern blocks, the checks of signatures and layouts, pointers,
and the manifest's `native` block. Slice 2 binds a real C library by hand: SQLite, through its
prebuilt library. The binding is the first of several. Later bindings show what the interop system
can do, and each one also tests it.

Commit each step on its own once it passes.

## 1. A failed link names its cause

`BuildFailure.Classify` reports every unresolved symbol in a program with extern blocks as "a C++
function that an extern block declares is not defined". If a native source calls a function of a
library that the manifest does not link, the report blames the extern block, which is wrong.

- Read the missing symbols from the linker's output (MSVC `LNK2019`/`LNK2001`, lld-link
  `undefined symbol:`, GNU ld `undefined reference to`).
- If an extern block declares a missing symbol, report the declaration. Otherwise, name the symbol
  and the object that refers to it, and say that the program links no library that defines it.
- Tests: a package whose native source calls a function of a library that its manifest does not
  name, and the existing case of an extern function that nothing defines.

## 2. `void*` and `Span<T>.byteLength()`

Both are in the spec (`interop.blittable`, `interop.buffers`, `interop.callbacks`) and not built.
SQLite passes blobs as `const void*` with a length in bytes, and frees memory through `void*`.

- `void*` is blittable. A pointer of any type converts to it, and a conversion back is a pointer
  cast, which is unsafe (`WYL0517`). Reading or writing through a `void*` is an error.
- `Span<T>.byteLength()` and `ReadOnlySpan<T>.byteLength()` return the size of the elements in
  bytes.
- Tests: each conversion, each error, and a call that passes a buffer's `data()` and `byteLength()`
  to C++.

## 3. Loose ends of slice 1

- The interpreter gives a pointer to a field the field's cell. If the whole struct is assigned
  later, the pointer still reads the old cell, while C++ reads the new value. Add a test that prints
  the difference, and fix the interpreter.
- Write a decision record for `WYL0518`, the rule that each directory and file that the manifest's
  `native` block names must exist.

## 4. The SQLite binding

The binding is a program package outside the test suite, with its extern block and a `main` that
uses it. A library package would need the manifest's `dependencies`, which is not built, and the
binding should show what `dependencies` needs before anyone designs it.

- On Windows, the package uses the x64 DLL from sqlite.org: `sqlite3.dll`, `sqlite3.def`, and
  `sqlite3.h`. `lib /def:sqlite3.def /machine:x64` makes the import library. On Linux, the package
  links the system's `sqlite3` (`libsqlite3-dev`). The manifest names the include directory, the
  library directory, `link = ["sqlite3"]`, and on Windows `copy` for the DLL.
- Bind `sqlite3_open`, `sqlite3_close`, `sqlite3_prepare_v2`, the `bind`, `step`, `column`, `reset`,
  and `finalize` functions, `sqlite3_errmsg`, and the result codes as extern constants.
- `SQLITE_TRANSIENT` casts `-1` to a function pointer, which Wylow cannot express. A native shim
  binds each function that takes it.
- `main` opens an in-memory database, creates a table, inserts rows with bound text, integers,
  doubles, and a blob, queries them, and prints each row. The output is the same on every run.
- Write down what the binding needed and lacked: each declaration that was hard to write, each
  shim, and what a library package would need from `dependencies`.

## After this slice

Callbacks (`@CCallback`) come next. `sqlite3_exec`, which calls a function for each row, is their
first test. Compiling `.c` files under `native/` would let a package carry a library's source, such
as SQLite's amalgamation, instead of a prebuilt library.
