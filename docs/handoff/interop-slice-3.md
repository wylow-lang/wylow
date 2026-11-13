# Interop, slice 3: callbacks, C sources, and what the SQLite binding lacked

Slice 2 built `void*`, `Span<T>.byteLength()`, and the SQLite binding in `examples/sqlite`. The
binding shows what the interop system still lacks. Steps 1 to 3 built callbacks and closed the
smaller gaps that the binding found. Step 4 wrote a safe layer over the binding, and step 5 designed
the manifest's `dependencies` with the user. Steps 6 to 8 built the manifest's new keys, libraries,
dependencies, and the native settings of dependencies. Step 9 split `examples/sqlite` into a library
and a program that depends on it.

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

## Facts from step 4

- `examples/sqlite` has a safe layer in its `sqlite` module, and `main.wyl` has no unsafe code. The
  extern block's members are visible only in the module. `Database` is a class whose destructor
  closes the connection. `Statement` is a unique struct whose destructor finalizes the statement,
  and its field `database` keeps the connection open. `column` returns a `Value` enum, and every
  call that SQLite can fail returns `Result<T, Error>`.
- The layer enforces three of SQLite's rules itself. `column` panics if the statement is not on a
  row or the column is out of range, because SQLite leaves both undefined. `prepare` returns an error
  for SQL text that holds no statement, because SQLite returns a null statement for it. `bind` binds
  an empty span with `sqlite3_bind_zeroblob`, because SQLite binds null for a null pointer.
- These places made the layer hard to write:
  - `Database` is a class and not a unique struct. A statement must keep its connection open, and
    a field cannot refer to a value that another variable owns. A class method cannot consume its
    object, so the layer has no `close` that returns an error. The destructor ignores the result
    of `sqlite3_close`, which cannot be `SQLITE_BUSY` while every statement keeps its database
    alive.
  - `forEachRow` takes an `escaping` function, although `sqlite3_exec` calls the function only
    during the call. The function reaches the callback through a `void*` to a struct, and a field
    holds only an escaping function (`WYL0265` rejects `escaping` on a field). An escaping lambda
    has only a copy of a local (`WYL0351`), so `main.wyl` counts the rows in an object.
  - An extern constant is not a constant expression, so a `case` cannot name `SQLITE_TEXT` or
    `SQLITE_ROW`. The layer compares the codes with `if`. A `@Repr` enum result is no help either,
    because SQLite returns extended result codes, and `E.tryFrom` is not built.
  - `String.fromCString` panics on invalid UTF-8 and stops at the first null byte. SQLite stores
    text without checking it, so `column` and `forEachRow` panic on invalid text. The standard
    library has no public function that makes a `String` from a byte span, or that returns an error
    for invalid UTF-8, so the layer cannot report invalid text as an `Error` or read text by its
    length.
  - The standard library has no function that copies a span into an array, so `column` copies a
    blob into a `byte[]` one byte at a time.
- The two limits that step 1 found, an `extern fn` field and a `byte*` field whose C type is
  `const char*`, did not come up: the layer declares no extern struct with fields.
- `@CallerLocation` on `column` and `columnName` makes their panics report the caller's line.

## Facts from step 5

- Decision 0101 records the design, and `programs.manifest.dependencies`,
  `programs.manifest.versions`, `programs.packages`, `programs.paths`, `programs.entry`,
  `errors.panics`, and `interop.native` state its rules. The compiler builds none of it yet.
- A package is a program or a library, and `kind = "library";` makes it a library. A library must
  not have `main`, and it must declare `version = "major.minor.patch"`.
- A dependency is a local `path`. The compiler has no registry and no fetch step. An entry may state
  a version, and the compiler checks it with the compatibility rule of Semantic Versioning: the
  same major number, not lower, and below 1.0 the same minor number.
- A dependency's name is a path root without an import, as `std` is. A package sees only its own
  dependencies.
- Each package keeps its own native settings. An extern block finds its header only in its own
  package's `native/` and `include` directories.
- A panic in a dependency names its file as `parser::lexer/tokens.wyl:3:5`.
- The compiler reports a dependency's errors and not its warnings.
- `tests/cases/packages/manifest_keys` pins today's behavior: `version` is not a key (`WYL0023`), and
  `dependencies` cannot be used (`WYL0031`). Step 6 changed the first line, and step 7 the second.
- Later, and not designed: code that reads its own package's version, such as for a `--version`
  flag. The user suggested a preprocessor definition that a function of `std` reads.

## Facts from step 6

- `Manifest.Read` reads `kind`, `version`, and the `version` of a dependency entry.
  `Manifest.Kind`, `Manifest.KindLocation`, and `Manifest.Version` hold what it read, and
  `PackageSources.Manifest` keeps the manifest.
- `PackageVersion.Parse` reads a version. Each number is a `BigInteger`, so a version has no largest
  number.
- `Package.Kind` is `PackageKind.Program` or `PackageKind.Library`. `std` is a library, and a file
  compiled on its own is a program.
- The new errors are `WYL0526` (a `kind` that is neither kind), `WYL0527` (a library without a
  `version`, at the `kind` entry), `WYL0528` (a version of the wrong form), `WYL0529` (a `main` in a
  library's root module), and `WYL0530` (a library given to a command that builds or runs it).
  `WYL0058` has a help line that names `kind = "library";`.
- `wyl check` stops after `Compilation.Lower`, so it takes a library. `Compilation.Compile` and
  `Compilation.Interpret` add `WYL0530` after a library passes every other check, so `wyl build`,
  `wyl run`, `wyl interpret`, `wyl emit`, and `wyl ir` refuse a library. The catalog test compiles a
  library's page example with `Compilation.Compile` for that reason.
- The test runner needs no change for a library. `packages/library_check` lists `WYL0530` at its
  `kind` line, which shows that the library has no other error. The other new tests are
  `manifest_kind`, `manifest_versions`, `library_no_version`, `library_main`, and `program_kind`.
  `manifest_keys` now lists `WYL0528` for its `version` line.

## Facts from step 7

- `PackageSources.Load` reads the program and every library that it depends on (`Loader`). It
  identifies a package by the full path of its directory, and compares paths without case on
  Windows. `PackageSources.Files` holds the files of every package, and `PackageSources.Package` is
  the package that was asked for. `PackageSources.Manifests` holds every manifest that was read.
- `Package.Dependencies` maps each name of a package's `dependencies` to its library. `Manifest`
  keeps each entry as a `Manifest.Dependency`, and `PackageVersion.Satisfies` decides a stated
  version. A dependency that breaks a rule is reported and left out of the graph, so the binder
  never sees it.
- The graph's errors are `WYL0531` (a path that holds no package), `WYL0532` (a key that is not the
  library's name), `WYL0533` (a program as a dependency), `WYL0534` (a cycle), `WYL0535` (two
  packages of one name), `WYL0536` (a dependency with the name of a module directly in `src/`), and
  `WYL0537` (an unsatisfied version). `WYL0031` is retired. `WYL0029` and `WYL0030` have pages now.
  A key given twice in `dependencies` is `WYL0024`.
- The binder's `TopLevel` and `ResolveModule` find a dependency's root module. `ResolveModule` looks
  for it after imports and before `std`. The warning `WYL0480` suggests a path through a dependency,
  such as `parser::count`, only for a dependency of the calling file's own package.
- `CppEmitter.ModulePrefix` writes `00`, the library's name with its length, and then the module
  path: `006parser5lexer`. The prefix must differ from the program's own module of the library's
  name, which the program may have if only another library depends on that library
  (`packages/dependencies`, whose program has a module `util`).
- A file of a library is named `parser::lexer/token.wyl` when `PackageSources` reads it, so panics
  and messages name it that way without other code.
- `Compilation.Lower` drops each warning whose location is outside the program's own package.
- `Driver.SourceKey` hashes every manifest and every package's files (`build-cache/dependencies`).
- A package test keeps its libraries in package directories inside it, which `testing.md`
  ("Libraries") describes. `TestCases.Packages` skips a package directory inside another one. The
  runner reads markers from every manifest and source under the test, and a library's file may
  hold the test's `// panic-here:` marker.
- The tests are `packages/dependencies`, `dependency_panic`, `dependency_names`, `dependency_graph`,
  and `library_dependency`.
- The manifest reader checks the keys of a dependency's `native` block, but nothing checks its paths
  or uses it yet. The program's build uses only the program's own native settings, so a library
  with an extern block did not build inside a program before step 8.

## Facts from step 8

- `NativeInputs` holds a `PackageNative` for each package of the program that has files, the
  program's first. A `PackageNative` holds one package's `native/` directory and its lists for this
  platform, with each path made absolute from that package's directory. `NativeInputs` gives the
  union of every package's `Link`, `LibraryPaths`, `Copy`, `Defines`, and include directories.
- Each native source compiles with the include path and the definitions of its own package. The
  native-object key of a source covers its own package's lists and files, so a change to one
  package's files compiles only that package's sources again.
- The binder keeps one `ExternHeader` for each package that names a header, and
  `ExternHeader.Package` is the package of its `@Include`. `Compilation.Emit` takes the
  `NativeInputs`, and the emitter includes each header by the full path that
  `NativeInputs.HeaderPath` finds in the block's own package. If the package does not hold the
  header, the emitter writes the path as written, which is how the standard library's headers are
  included.
- The generated program compiles with the definitions of every package and with the include path
  of every package, in package order, so that a header finds the headers that it includes. For that
  reason, a block whose package does not hold its header may still find a header of another package
  through the include path. The spec does not say what the compiler does with such a header.
- `PackageSources.Load` reports `WYL0538` for two packages that define one name with different
  values, and `WYL0539` for two packages that copy two different files of one name. A definition
  without a value has the value `1`. The compiler reports either error at the list of the package
  that comes later in an order where every package comes after its dependencies, so the program's
  own list gets the error when the program takes part. A library's missing native path is
  `WYL0518` in the library's manifest.
- A build failure names a library's native source as `native source 'codec.c' of library 'codec'`,
  and a compile error in a library's native files as an error of `the native sources of library
  'codec'`.
- The tests are `interop/dependency_native`, `packages/dependency_defines`,
  `packages/dependency_copy`, and `packages/dependency_native_paths`. The self-tests
  `build-cache/native`, `build-cache/native-rebuild`, and `build-cache/native-objects` each build a
  program with a dependency now.
- On Linux through WSL, the `interop` and `packages` tests pass in the gcc mode, and so do the
  self-tests `build-cache`, `build-failure`, and `catalog`, and the whole suite with `--interpret`.
  On Windows, the gcc mode still fails only `interop/undefined_extern` and `interop/c_linkage`.

## Facts from step 9

- `examples/sqlite/sqlite` is a library named `sqlite`, at version 0.1.0. It holds the safe layer in
  its root module, the fetch scripts, and the amalgamation in its `native/` directory, which the
  repository ignores. The library's manifest has no `native` block.
- `examples/sqlite/demo` is the program `sqlite_demo`. Its manifest depends on `../sqlite` with the
  version `0.1`, and it holds only `main.wyl`.
- The split changed no code. `main.wyl` still imports `sqlite::{Database, Error, Statement}`, because
  a dependency's name is a path root as a module's name was. The layer's C API stays hidden: a call
  of `sqlite::sqlite3_close`, a use of `Database.handle`, and a use of `sqlite::SQLITE_OK` from the
  program are each `WYL0054`. The library has one module, so `module` and `package` hide the same
  members, and the layer keeps `module`.
- The generated program includes `sqlite3.h` by its full path in the library's `native/` directory.
  The build compiles `sqlite3.c` into the program's own `.wyl/sqlite_demo/native-objects`. A second
  program that depends on the library would therefore compile the amalgamation again, because a
  library has no build directory of its own.
- The compiler has no fetch step, so the user runs the library's fetch script before building the
  program. If the amalgamation is missing, `wyl build examples/sqlite/demo` reports `WYL0513` at the
  library's `@Include`, with the note `'sqlite3.h' file not found`. Nothing in the message tells the
  user to run the script.
- A panic in the library names the library's file. If `forEachRow` reads text that is not valid
  UTF-8, the panic is at `sqlite::database.wyl:113:59`, in `visitRow`. A panic in `column` or
  `columnName` names the program's line, because both functions are `@CallerLocation`.
- `wyl check examples/sqlite/sqlite` checks the library alone and reports nothing, and `wyl build`
  reports `WYL0530` for it. `wyl interpret examples/sqlite/demo` stops at the first call of an
  extern function, as it did before the split, because C++ code provides SQLite.
