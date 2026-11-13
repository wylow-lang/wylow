# Interop, slice 4: what the SQLite library still lacks

Slice 3 built callbacks, C sources under `native/`, extern functions that return a `@Repr` enum,
libraries, and dependencies. It also wrote a safe layer over SQLite and made the layer a library.
Writing the layer showed where the language is awkward to use. This slice closes those gaps: three
small additions to the standard library and the compiler first, then statics as patterns, then two
designs that need the user.

Commit each step on its own once it passes. Before each commit, run the whole suite with
`--interpret`, and the filter of the area touched with `--cc clang`. A step that touches interop
also runs the `interop` filter with `--cc msvc` and `--cc asan`. A step that changes the build or
the runtime also runs on Linux through WSL (`testing.md`, "Linux through WSL").

Each step that removes a workaround from `examples/sqlite` changes the example in the same commit.
The example is outside the test suite, so the step runs `wyl run examples/sqlite/demo` and checks
its output by hand. When code is awkward to write, probe the awkward spot with a small program, and
record the workaround and the diagnostic that forced it in this handoff.

## Facts from slice 3

- `examples/sqlite/sqlite` is a library named `sqlite`. It holds the safe layer in its root module,
  the fetch scripts, and SQLite's amalgamation in its `native/` directory, which the repository
  ignores. `examples/sqlite/demo` is a program that depends on the library and holds only
  `main.wyl`. The fetch script must run before the first build.
- The layer's C API is in `c_api.wyl`, and its members are `module`, so a program that depends on
  the library cannot call them (`WYL0054`).
- `ReadOnlySpan(T* data, int length)` and `Span(T* data, int length)` are unsafe constructors of a
  span over memory that a pointer points to.
- `String.fromCString` panics on invalid UTF-8 and stops at the first null byte. It is the only
  function of the standard library that makes a `String` from bytes.
- An extern function may return a `@Repr` enum, and the call panics if C++ returns an integer that
  no variant has (decision 0100). SQLite returns extended result codes that a binding may not list,
  so the layer declares the result codes as `int` statics.
- The emitter checks the boundary with C++ by `static_assert`s, and the build reports a failed
  assertion as a diagnostic at the declaration (`CppEmitter.Interop`). A signature or layout that
  does not match C++ fails this way.
- Decision 0043 gives the language no constant declaration. `const` is a reserved word with no
  meaning, and the decision rejected `const` with a meaning only in extern blocks. A value fixed
  for the whole run is a static, and every static is read-only.
- A pattern names a value only by a literal (spec `patterns.kinds`). A `case` therefore cannot name
  a static, whether a Wylow static or an extern one.
- If the block's own package does not hold its header, the emitter writes the `@Include` path as
  written (`NativeInputs.HeaderPath`). The standard library's headers are found this way. A
  missing header is then a C++ error, `WYL0513` at the `@Include`, with the compiler's note, such
  as `'sqlite3.h' file not found`.

## 1. Strings from bytes

The layer cannot report invalid text as an `Error`, and it cannot read text by its length, because
the standard library has no function that makes a `String` from a byte span. A function such as
`String.fromUtf8(ReadOnlySpan<byte> bytes)` would return the string, or an error that says where
the first invalid byte is. A null byte inside the span is part of the string. Choose the names and
the error type to match the standard library's other fallible functions, and write the spec text
in `a-library.md`.

- With the unsafe `ReadOnlySpan` constructor, the layer reads `sqlite3_column_text` by the length
  that `sqlite3_column_bytes` gives, and it returns invalid text as an `Error` instead of panicking.
  `column` and `forEachRow` lose their `# Panics` lines about UTF-8.
- Tests: valid text, an empty span, a null byte inside the span, and each kind of invalid sequence
  (a stray continuation byte, a truncated sequence, an overlong encoding, a surrogate, and a value
  past U+10FFFF), each with the offset that the error reports.

Done: `String.fromUtf8` returns `Result<String, Utf8Error>`, and `Utf8Error` holds the offset.
The layer reports invalid text as an `Error` with the code `SQLITE_MISMATCH`. Writing the layer
showed one awkward spot. `try` passes an error on only if its type is the function's error type, so
the layer converts a `Utf8Error` to an `Error` with a `switch` at each of the two calls. A
conversion of the error type in `try` is not designed.

## 2. Copying a span into an array

`column` copies a blob into a `byte[]` one byte at a time, because the standard library has no
function that copies a span into an array. Add one, such as `toArray()` on `ReadOnlySpan<T>` and
`Span<T>` for a copyable `T`, and use it in `column`. Check first whether the iteration library
already gives a way that the layer missed, and if it does, record that instead.

- Tests: an empty span, a span of structs, and an array that the copy does not share with the span.

Done: the iteration library had `toList` on `ReadOnlySpan<T>` and no way to make an array, so
`std::iteration` gains `toArray`, an extension on `ReadOnlySpan<T>` beside `toList`. A span, an
array, and a list reach it by the span conversions of an extension's receiver. A program imports it
as it imports `toList`: `import std::iteration::toArray;`.

## 3. `E.tryFrom`

The spec defines `E.tryFrom(x)` for a `@Repr` enum (`enums.repr`), and the compiler does not build
it. With `tryFrom`, a binding can declare SQLite's primary result codes as a `@Repr` enum and
convert an extended code without a panic. Build `tryFrom`, and then decide with the user whether the
layer's `Error` keeps the integer code or holds an enum and the integer. `@Flags` and `bitcast<T>`
stay unbuilt.

- Tests: a value that a variant has, a value that no variant has, each integer type of `@Repr`, and
  the interpreter's result, which must match the native result.

## 4. Statics as patterns, and extern statics with values

The user agreed to this design, which needs a decision record before the spec and the code.

- A `case` may name a static if the static's initializer is a constant expression (spec
  `expressions.constants`) of an integer, `char`, or `bool` type. The pattern matches a value equal
  to the static's value. The rule in `patterns.kinds` that a lone name is a variant or a type gains
  a third reading, and a qualified name may name a static of a type.
- The compiler knows the value of such a static, so it still reports two cases of one value, and
  the emitter still writes a C++ `switch`.
- A static of an extern block may take an initializer: `static int SQLITE_ROW = 100;`. The program
  uses the value of the initializer. The emitter checks it with a `static_assert` that the C++
  constant equals the value, and the build reports a failure at the declaration, as it reports a
  signature that does not match.
- An extern static without an initializer works as it does today and is not a pattern.
- The user and the assistant rejected one alternative: any static as a pattern, compared at run
  time. If a pattern were compared at run time, the compiler could not report two cases of one
  value, and a `case` would no longer name a value known at compile time.
- Decide in the decision record what a static whose initializer is not a constant expression
  reports when a `case` names it, and whether the initializer of an extern static may be any
  constant expression or only a literal.
- `examples/sqlite` gives each result code and type code a value and uses `switch` in `step` and
  `column`.
- Tests: a Wylow static in a `case`, an extern static with a value in a `case`, an extern value that
  does not match C++ (a `// native` test that lists the build's error), two cases of one value, a
  static without a constant initializer in a `case`, and an extern static without a value in a
  `case`. Add the pattern to the patterns matrix, and a fuzzer mutation that replaces a literal
  pattern with a static of the same value.

## 5. A header outside the block's own package

Design this step with the user, and record the decision before the code. Today, a block whose
package does not hold its header may still find a header of another package through the include
path, and the spec says nothing about that case. The rule decides two things.

- The rule decides whether a header outside the block's package is an error, is allowed, or is
  allowed only for a system header, such as `<sqlite3.h>` installed with the operating system. The
  compiler cannot tell a system header from a missing one without asking the C++ compiler.
- The rule decides what the error says when the header is missing. A note could name the package
  and the directories that the compiler searched. A library could also name, in its manifest, how
  to get its native files, so that the error tells the user to run `fetch-sqlite.ps1`.

## 6. A non-escaping function through a C context

Design this step with the user in a session of its own. `forEachRow` takes an `escaping` function,
although `sqlite3_exec` calls the function only during the call. The function reaches the callback
through a `void*` to a struct, and a field holds only an escaping function (`WYL0265`). An escaping
lambda has only a copy of a local (`WYL0351`), so `main.wyl` counts the rows in an object. Every C
API that takes a callback and a context pointer meets the same rules, such as `qsort_r` and most C
event APIs.

- One direction is a struct that may hold non-escaping values and that cannot escape itself. The
  compiler would check such a struct as it checks what a span points into.
- Another direction is an unsafe way to pass a non-escaping function as a pointer for the length of
  one call. The unsafe way makes the programmer responsible for C not keeping the pointer.
- The design must keep the rule that what a reference points into stays alive while it is used.

## Later, and not planned in this slice

- Each program that depends on a library compiles the library's native sources into its own
  `.wyl/<program>/native-objects`, because a library has no build directory of its own. A second
  program that depends on `sqlite` compiles the amalgamation again. On the development machine, a
  debug build of the example took 4.8 seconds with clang-cl, and a release build took 21 seconds
  (decision 0099).
- Code that reads its own package's version, such as for a `--version` flag, is not designed. The
  user suggested a preprocessor definition that a function of `std` reads.
- The MSYS2 `ld` of the development machine prints nothing for a missing symbol, so
  `interop/undefined_extern` and `interop/c_linkage` fail in the gcc mode on Windows and pass on
  Linux. A linker flag may make `ld` name the symbol. If none does, the runner needs a way to mark a
  test as unsupported in one mode, which is item 5 of the backlog in `testing.md`.
- A field of an extern struct has C's own type, and Wylow code assigns it directly. A field of an
  `extern fn` type therefore compiles only if C's type is exactly the `extern fn` type's C++ type,
  and a `byte*` field whose C type is `const char*` has the same limit. No binding has needed such a
  field yet.
- `Database` stays a class. A statement must keep its connection open, and a field cannot refer to
  a value that another variable owns. A class method cannot consume its object, so the layer has no
  `close` that returns an error. The destructor ignores the result of `sqlite3_close`, which cannot
  be `SQLITE_BUSY` while every statement keeps its database alive. The user and the assistant chose
  to keep this design, because references in fields would change the ownership model.
