# Interop, slice 2: the SQLite binding

Slice 1 (`interop-slice-1.md`) built extern blocks, the checks of signatures and layouts, pointers,
and the manifest's `native` block. Slice 2 binds a real C library by hand: SQLite, through its
prebuilt library. The binding is the first of several. Later bindings show what the interop system
can do, and each one also tests it.

Commit each step on its own once it passes.

## 1. A failed link names its cause (done)

Step 1 is committed. The next session starts at step 2. These facts from step 1 matter later:

- `BuildFailure.MissingSymbols` reads the missing symbols of link.exe, lld-link, and GNU ld. A symbol
  that an extern declaration names is `WYL0519` at the declaration. A symbol that a native source
  uses is the package's error, which names the symbol and the source.
- The MSYS2 `ld` of the development machine exits with status 5 and prints nothing when a symbol is
  missing, so `interop/undefined_extern` fails there in the gcc mode. The `build-failure/`
  self-tests read GNU ld's format from a copy of its output. `wsl-testing.md` describes how to run
  the suite with a working GNU ld on Linux.
- The wrapper of an extern function without parameters no longer declares the alias `F`, which
  clang reported as unused.

## 2. `void*` and `Span<T>.byteLength()` (done)

Step 2 is committed. These facts from step 2 matter later:

- `WylType.IsVoidPointer` names `void*`. A pointer of any type converts to it implicitly, also in
  safe code, and a cast back is unsafe (`WYL0517`). Indexing a `void*` and arithmetic on it are
  `WYL0520`. A `void*` argument gives a generic `T*` parameter no type argument.
- `byteLength()` calls the package-visible `memory::byteLength<T>(count)`, which C++ computes with
  `sizeof` and which panics above the largest `int`. The interpreter computes the size with
  `CLayout`, its model of the C++ layout of a blittable type. For any other type, the run ends as
  unsupported.
- `interop/buffers` passes a byte buffer's `data()` and `byteLength()` to a C++ function that
  takes `const void*`.

## 3. Loose ends of slice 1 (done)

Step 3 is committed. The interpreter now assigns a struct or a fixed array into the cells it
already has (`Interpreter.Exchange`), so a pointer to a field reads the new value, as in C++
(`unsafe/field_pointers`). Decision 0095 records why each path of the `native` block must exist,
and decision 0096 records the choices of step 2.

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
