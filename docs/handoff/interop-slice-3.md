# Interop, slice 3: callbacks, C sources, and what the SQLite binding lacked

Slice 2 built `void*`, `Span<T>.byteLength()`, and the SQLite binding in `examples/sqlite`. The
binding shows what the interop system still lacks. Steps 1 to 3 built callbacks and closed the
smaller gaps that the binding found. The `dependencies` design comes last, because the steps before
it change what a library package needs.

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
  development machine prints nothing for a missing symbol, so `interop/undefined_extern` and
  `interop/c_linkage` fail in the gcc mode on Windows and pass on Linux.
- `examples/sqlite` is outside the test suite. Step 2 changed how it gets SQLite.

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

## Facts from the toolchain step

- Decision 0098 defines the supported C++ compilers. `ToolchainVersions` holds the oldest version of
  each, and `NativeCompiler.Find` refuses an older compiler. runtime.md ("Toolchains") lists the
  compilers and the versions that CI runs. CI pins `windows-2025` and `ubuntu-24.04`, and it runs
  the gcc mode on Linux as well.
- clang-cl is the one in the Visual Studio installation of the MSVC environment, never the one on
  `PATH`. The build no longer defines `_ALLOW_COMPILER_AND_STL_VERSION_MISMATCH`.
- The spec says that a `.cpp` native source is C++20.

## Facts from step 2

- C sources are built (decision 0099). The build compiles each `.c` file under `native/` as C17 and
  each `.cpp` file as C++20, each to an object of its own in `native-objects` beside the
  executable. The build keeps an object until a file that its source can include changes, so a
  change to the Wylow sources alone compiles no native source again.
- If the package has a C source and the missing symbol of `WYL0519` is a C++ name, the error has a
  note and a help line about `extern "C"`.
- `examples/sqlite` compiles SQLite's amalgamation on every platform. `fetch-sqlite.ps1` on Windows
  and `fetch-sqlite.sh` on Linux put `sqlite3.c` and `sqlite3.h` in the example's `native/`, which
  the repository ignores. The manifest has no `native` block.

## Facts from step 3

- An extern function may return a `@Repr` enum (decision 0100). The call panics if C++ returns an
  integer that no variant has, with the message of a cast. `FunctionSymbol.ReturnedInteger` names
  the enum's integer type. An `IrCall` of the function has that type, and lowering converts the
  integer to the enum after the call. A constant, a field, and a `@CCallback` function still take
  the integer type (`WYL0507`).
- `examples/sqlite` still declares SQLite's result codes as `int` constants. SQLite returns extended
  result codes that a binding may not list, and a call that returns an enum would panic on one.
- An array literal may end with a comma.
- The types matrix has the rows `pointer` and `void-pointer`. The fuzzer's `type-void-pointer`
  mutation makes a pointer type `void*`.
- `@Flags`, `E.tryFrom`, and `bitcast<T>` are in the spec, but the compiler does not build them.
  Step 4 cannot use them.

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
