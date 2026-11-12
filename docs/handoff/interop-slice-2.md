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

## 4. The SQLite binding (done)

The binding is `examples/sqlite`, a program package outside the test suite. On Windows,
`fetch-sqlite.ps1` downloads SQLite 3.54.1's x64 DLL and its amalgamation from sqlite.org, checks
each archive's SHA3-256 hash, copies `sqlite3.dll`, `sqlite3.def`, and `sqlite3.h` to
`vendor/windows`, and makes `sqlite3.lib` with `lib.exe`. The repository ignores `vendor/`. On Linux,
the package links the system's `sqlite3`. `main` opens an in-memory database, inserts rows with
bound text, integers, doubles, blobs, and nulls, and prints each row of two queries. The output is
the same with clang, MSVC, and ASan on Windows, with g++ on Linux through WSL, and in a release
build on each.

What the binding needed:

- `void*` (step 2) carries a blob to `sqlite3_bind_blob` and back from `sqlite3_column_blob`, and
  `byteLength()` gives the blob's size.
- An extern constant reads a C macro, so each result code is a `static int` in the extern block.
- `const char*` and `const unsigned char*` both match `byte*`, so `cstr()` passes SQL text and
  `String.fromCString` reads `sqlite3_errmsg` and `sqlite3_column_text`.
- `&` of a `mut` local of type `Database*` gives the `sqlite3**` out-parameter of `sqlite3_open`.
- One native shim each for `sqlite3_bind_text` and `sqlite3_bind_blob`, because
  `SQLITE_TRANSIENT` casts `-1` to a function pointer type, which an extern block cannot express.

What the binding lacked:

- Every call is unsafe, and the example wraps none of them. A safe layer, such as a class whose
  destructor closes the database and finalizes its statements, is the next thing a library package
  would add.
- A result code is an `int` compared with constants. A `@Repr` enum crosses only as a parameter, so
  a function cannot return one.
- An array literal is a fixed array, so the rows are an `Order[3]` that the insert takes as a
  `ReadOnlySpan<Order>`. An array literal also cannot end with a comma.
- A Windows build needs an import library, which the build cannot make from a `.def` file. The
  fetch script makes it with `lib.exe`.

What a library package would need from `dependencies`:

- The dependency's `native` block applies to the program that depends on it: its include
  directories, library directories, `link` libraries, `copy` files, and definitions.
- The dependency's `native/` sources are compiled into the program, and its `native/` directory is
  on the program's include path.
- Each relative path in the dependency's manifest resolves against the dependency's own directory.
- A prebuilt library has to be on disk before the build. The manifest has no step that fetches it,
  so a package that ships a prebuilt library also ships a script like `fetch-sqlite.ps1`.

## After this slice

Callbacks (`@CCallback`) come next. `sqlite3_exec`, which calls a function for each row, is their
first test. Compiling `.c` files under `native/` would let a package carry a library's source, such
as SQLite's amalgamation, instead of a prebuilt library.
