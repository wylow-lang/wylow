# Interop, slice 3: callbacks, C sources, and what the SQLite binding lacked

Slice 2 built `void*`, `Span<T>.byteLength()`, and the SQLite binding in `examples/sqlite`. The
binding shows what the interop system still lacks. This slice builds callbacks first, because they
are the largest gap, and then closes the smaller gaps that the binding found. The `dependencies`
design comes last, because the steps before it change what a library package needs.

Commit each step on its own once it passes. Before each commit, run the whole suite with
`--interpret`, and the `interop` filter with `--cc clang`, `--cc msvc`, and `--cc asan`. A step that
changes the build or the runtime also runs on Linux through WSL (`testing.md`, "Linux through
WSL").

## Facts from slice 2

- `WylType.IsVoidPointer` names `void*`. A pointer of any type converts to it implicitly, also in
  safe code, and a cast back is unsafe (`WYL0517`). Indexing a `void*` and arithmetic on it are
  `WYL0520`. Decision 0096 records the reasons.
- `byteLength()` calls the package-visible `memory::byteLength<T>(count)`, which the runtime
  computes with `sizeof`. The interpreter computes the size with `CLayout`, its model of the C++
  layout of a blittable type.
- `BuildFailure.MissingSymbols` reads the missing symbols of link.exe, lld-link, and GNU ld. A
  symbol that an extern declaration names is `WYL0519` at the declaration. The MSYS2 `ld` of the
  development machine prints nothing for a missing symbol, so `interop/undefined_extern` fails in
  the gcc mode on Windows and passes on Linux.
- `examples/sqlite` is outside the test suite. On Windows, `fetch-sqlite.ps1` puts SQLite's DLL,
  header, and import library in `vendor/windows`, which the repository ignores. On Linux, the
  package links the system's `sqlite3`. The example runs with clang, MSVC, ASan, and g++ on Linux.
- Two native shims in the example pass `SQLITE_TRANSIENT`, which casts `-1` to a function pointer
  type that an extern block cannot express.

## 1. Callbacks (`@CCallback`)

The spec describes callbacks (`interop.callbacks`, and the `@CCallback` row of `interop.blittable`),
and no code implements them. The spec leaves one question open: how an extern function's
parameter writes the type of a C function pointer. Decide that question with the user first, and
record the answer in a decision record before the code.

- A static or top-level function marked `@CCallback` converts to a C function pointer. A lambda
  does not. It is an error if the function's parameters or return type are not blittable.
- A panic inside a callback ends the program. The C library may call the function on a thread
  that it created.
- The callback's state crosses as a `void*` context, and casting the context back is unsafe.
- The interpreter does not run C code, so every callback test is `// native`.
- Tests: each error, a C++ function that calls a callback with a context, a panic in a callback,
  and `sqlite3_exec` in `examples/sqlite`, which calls a function for each row.

## 2. C sources under `native/`

The build compiles each `.cpp` file under `native/`, and no `.c` file. If it compiled C files
too, a package could carry a library's source, such as SQLite's amalgamation, and need no prebuilt
library. The example would then build SQLite from `sqlite3.c` on every platform, and the
import library and its `lib.exe` step would go away. The amalgamation is about 9.6 MB, so the fetch
script stays, and it downloads the amalgamation instead of the DLL.

- Each toolchain needs C flags, and the build cache key must cover the C files as it covers the C++
  files.
- Tests: a package test with a `.c` file that an extern block calls, in every native mode.

## 3. Gaps the binding found

- An extern function cannot return a `@Repr` enum, which crosses only as a parameter. SQLite's
  result codes are therefore `int` constants. Decide whether a return value may be a `@Repr` enum,
  and what happens when C returns a value that no variant has.
- An array literal cannot end with a comma (`WYL0001`). A list of rows written one per line
  needs one.
- The types matrix has no row for pointer types, so `void*` has no row either. The fuzzer has no
  mutation that crosses the `void*` rules.

## 4. A safe layer in the example

Every call in `examples/sqlite` is unsafe, and the example wraps none of them. Write the layer that
a library package would export: for example, a `Database` class whose destructor closes the
connection, and a `Statement` that finalizes itself. Write down each place where the language made
the layer hard to write.

## 5. Designing `dependencies`

A library package needs the manifest's `dependencies`, which is not built. This step is a design
session with the user, not code. The binding found that a dependency's `native` block must apply
to the program that uses it, its `native/` sources must be compiled into that program, and each of
its relative paths must resolve against its own directory. A package that ships a prebuilt library
also ships a script that fetches it, because the manifest has no fetch step.
