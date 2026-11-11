# Interop, slice 1

Do this after `docs/handoff/string-methods.md`, whose emitter form for methods with bodies this work
reuses. The rules are in `docs/spec/21-interop.md`, `programs.manifest`, `unsafe.blocks`, and
`annotations.list`. The reasons are in decisions 0084 to 0087. The goal is a package that binds a C
library by hand, with a few dozen declarations, and calls the library from Wylow.

Commit each step on its own once it passes. Each step names the tests it needs.

## 1. Extern blocks, with `std` moved into them

- Parse `ExternBlock`, `ExternConstant`, `OpaqueStruct`, and `UnionDeclaration`. `union` is a word
  with this meaning only as the first word of a member of an extern block (`lexical.keywords` has
  the same kind of rule for `as`). Remove `extern` from the modifiers of functions, methods, and
  structs.
- Bind `@Include`, `@CppNamespace`, `@CppName` (on members and on fields), `@Safe`, and `@Pure`. A
  member's C++ name comes from its declaration and its block, and nothing else derives it. Delete
  `Binder.ExternName`, and update the runtime naming rule in `CLAUDE.md` that cites it.
- Put the test "is this block in `std`" in one predicate, and use it for every exemption that
  `interop.extern.std` lists.
- Move every extern declaration of `std` into blocks in the same change, as "replace, don't layer"
  requires. That includes `primitives.wyl`, `string.wyl`, `weak.wyl`, `memory`, `panic`, `replace.wyl`,
  `arguments.wyl`, and `threading`. A native function that was safe to call gets `@Safe`, and one that
  was `@Pure` keeps `@Pure`. A static extern method of an ordinary struct becomes a `package` member
  of a block in its module.
- Diagnostics: give each new error a catalog entry, a test, and a page or a line in
  `without-pages.txt`, written as `docs/diagnostics/README.md` says. Retire `WYL0425` and `WYL0426`,
  and reword `WYL0427` to say that only the standard library declares a struct backed by a C++ class.
  Decision 0003 says how to retire a code.
- Tests: a block in `std` and the exemptions, and each error outside `std`. Nothing outside `std`
  can call a user extern yet, so the user-side tests start in step 3.

## 2. The interpreter refuses user externs

- The interpreter refuses a program that calls an extern function it does not implement, with a
  diagnostic at the call (decision 0087).
- Add the test marker `// native`. A test with the marker skips the interpreted mode, and the fuzzer
  does not compare it with the interpreter. Document the marker in `CLAUDE.md` ("Writing tests") and
  in `docs/design/testing.md`.

## 3. Calls, constants, structs, and the checks

- Emit `#include` for each block's header, once per header, after `wyl.h`.
- Emit each call of a user extern through a `noexcept` wrapper that passes each argument and the
  result through a runtime adapter (`runtime/wyl/interop.h`). The adapter converts between matching
  types that are not the same type (`interop.signatures`). Pick a form that does not depend on the
  order of overload resolution, and write down in `runtime.md` how it works.
- Emit a signature check for each extern function whose C++ name has one declaration, and a layout
  check for each struct and union with fields. Both checks are `static_assert`s in the generated C++.
- The rule "a C++ compile failure is a compiler bug" needs a stated exception. If a header does not
  match an extern block, the C++ compiler reports the failure. The build must report a failed
  signature or layout check as a Wylow diagnostic at the declaration: give each `static_assert` a
  message that names the declaration, and translate the message. A mismatch that no check catches,
  such as a wrong name, appears as the C++ compiler's own error. Report that error with the
  declaration it came from, as far as the build can tell, and add the exception to the enforcement
  table in `docs/design/architecture.md`.
- A struct or union with fields is emitted by its C++ name, with its C++ field names, and never as a
  struct the emitter writes. A method with a body on one uses the free-function form from the string
  work.
- Reading a union field is unsafe, and the emitter compiles the read as `std::memcpy` from the
  union's storage into a value of the field's type.
- The blittable check covers signatures, constants, and fields, and refuses a `@Repr` enum anywhere
  but in a parameter.
- Tests: these go in `tests/cases/interop/`, as packages with a `native/` directory whose headers
  and sources the tests define themselves, so that the suite needs no library installed. Each
  package is marked `// native`. Cover a call in each direction of each conversion, a constant from
  a macro, an opaque struct passed back and forth, a struct by value and by pointer, a union written
  by C++ and read by Wylow, each layout and signature mismatch, and each error of the blittable
  check. Add a row or column to the matrices where the feature crosses one.

## 4. Pointers and buffers

- `&place` and `(U*) p` (`unsafe.pointers`). `&place` produces a place that the verifier must know.
  Teach `PlacePath.Of` and the IR effects about it, as `CLAUDE.md` says for any new way to produce a
  place.
- `Span<T>.data()` and `ReadOnlySpan<T>.data()`, both unsafe.
- Tests: an out-parameter filled by C++ through `&local`, a span's data passed to C++, and a C++
  function writing into a Wylow array through `Span<T>.data()`.

## 5. The manifest's `native` block and `native/` sources

- Read `include`, `libraryPaths`, `link`, `copy`, `defines`, and the platform blocks, and remove
  `native` from the rule "It is a compile-time error if the manifest has a `dependencies` or `native`
  entry" in `programs.manifest`.
- Compile each `.cpp` file under `native/` with the toolchain of the build, and link it. Each of the
  seven modes builds the native sources with its own flags.
- Map each `link` name to the toolchain's spelling, and copy each `copy` file next to the executable.
- Hash the `native` entries and every file under `native/` into the build key, and add them to the
  architecture row "A reused executable is the one a new build would produce". Add a `build-cache`
  self-test that fails if a changed native file reuses the old executable.

## After this slice

`docs/design/runtime.md`'s "Interop" section, `CLAUDE.md`, and `architecture.md` describe what the
slice built. The roadmap's "Interop" list names what was proposed and is not queued yet. The next
step is a hand-written binding of a real C library in a package of its own, outside the test suite.
