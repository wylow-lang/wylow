# Interop, slice 3: callbacks, C sources, and what the SQLite binding lacked

Slice 2 built `void*`, `Span<T>.byteLength()`, and the SQLite binding in `examples/sqlite`. The
binding shows what the interop system still lacks. Step 1 built callbacks, the largest gap. The
steps below close the smaller gaps that the binding found. The `dependencies`
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

## Facts from step 1

- Callbacks are built (decision 0097). A C function pointer has the type `extern fn(P) -> R`, and
  only a reference to a function marked `@CCallback` converts to it. The emitter calls each
  callback through a `noexcept` function with C's types. If C's parameter type matches the
  `extern fn` type without being it, as `char**` matches `byte**`, the wrapper of the extern
  function gives C an adapter of C's own type (`wyl::interop::ToC`).
- `examples/sqlite` calls `sqlite3_exec` with a callback, and it passes `SQLITE_TRANSIENT` as an
  extern constant. The two shims and the example's `native/` directory are deleted.
- A field of an extern struct has C's own type, and Wylow code assigns it directly. A field of an
  `extern fn` type therefore compiles only if C's type is exactly the `extern fn` type's C++ type.
  A `byte*` field whose C type is `const char*` already had the same limit. Step 4 should record
  both limits if the safe layer meets them.

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
