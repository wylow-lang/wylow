# Interop, slice 1

This work reuses the emitter form for methods with bodies of extern structs
(`FunctionSymbol.IsExternStructBody`). The rules are in `docs/spec/21-interop.md`, `programs.manifest`, `unsafe.blocks`, and
`annotations.list`. The reasons are in decisions 0084 to 0087. The goal is a package that binds a C
library by hand, with a few dozen declarations, and calls the library from Wylow.

Commit each step on its own once it passes. Each step names the tests it needs.

## 1. Extern blocks, with `std` moved into them (done)

Step 1 is committed. The next session starts at step 3. These facts from step 1 matter later:

- The parser keeps each member of a block among the file's declarations, with the block as its
  `Declaration.Block`, and lists the blocks in `CompilationUnit.ExternBlocks`.
  `TypeSymbol.IsExtern` says that a struct or union is declared in a block, and
  `TypeSymbol.IsCppClass` says that a C++ class backs it.
- The binder checks an extern constant's type and annotations, but declares no symbol for it, so
  no code can read a constant yet. Step 3 declares the symbol.
- The emitter writes nothing for a struct, union, or function of a program's block, so a program
  that uses one fails to compile in C++ until step 3.
- `WYL0428` is retired along with `WYL0425` and `WYL0426`, because a struct with fields in a block is
  now a C struct. Decision 0094 lets the standard library declare a struct backed by a C++ class
  `unique`, which `ElementVisitor<T>` needs.
- `String.copyOf` is now the top-level `package` function `copyString` in the standard library's
  root module, which C++ names `wyl::copyString`.

## 2. The interpreter refuses user externs (done)

Step 2 is committed. `Interpreter.NativeCall` refuses a program that calls an extern function
without an intrinsic, with the status `InterpretationStatus.Native`. A test marked `// native`
skips the interpreted mode, and `RunComparison.IsNative` keeps the fuzzer from comparing it with
the interpreter. Every test of step 3 that calls C++ code needs the marker.

## 3. Calls, constants, structs, and the checks (done)

Step 3 is committed, with the part of step 5 that its tests need. These facts from step 3 matter later:

- The binder declares an extern constant as an extern function without parameters
  (`FunctionSymbol.IsExternConstant`), and a use of the constant's name is a call of it.
  `FunctionSymbol.IsProgramExtern` says that a function or constant belongs to a block outside the
  standard library.
- `runtime.md`'s "Interop" section describes the wrappers, the checks, and the adapters of
  `runtime/wyl/interop.h`. `BuildFailure.Classify` turns a failed check, or another C++ error on
  the lines of an extern declaration, into a diagnostic (`WYL0510` to `WYL0513`).
- The build compiles each `.cpp` file under a package's `native/` directory, puts the directory on
  the include path, and hashes every file of it into both build keys, which the
  `build-cache/native` self-test pins. The `native` block of the manifest is still an error.
- The tests of `&place` in step 4 can replace the pointer that `interop/calls` gets from C++.

The original list:

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

## 4. Pointers and buffers (done)

Step 4 is committed. These facts from step 4 matter later:

- `&place` binds to `BoundAddressOf` and lowers to the IR value `ptr P` (`IrPointer`). It takes
  only a place that an assignment could write (`WYL0514` to `WYL0516`), and a pointer cast is
  unsafe (`WYL0517`). `PlacePath.Of` needs no change: `&place` makes a pointer value, and the
  place it points to is the operand, which lowering finds as it finds any place.
- `LambdaWrites` counts every local that a `ptr` or a span's `data` points into as one that code
  may change when it runs, through `ref` locals too. The verifier counts only `ptr`
  (`IsAddressTaken`), and null checks do not narrow such a local. A call of a program's extern
  function may write anything (`Writes`). The same change fixed the emitter, which read a local
  array after a call that wrote it through a span (`spans/write_order`).
- The span field that holds the pointer is now `elements`, so that `data()` can be a method.
- `void*` and `Span<T>.byteLength()` are in the spec but not built. The callbacks work needs
  `void*`.
- The interpreter gives a pointer to a field the field's cell. If the whole struct is assigned
  later, the pointer still reads the old cell, while C++ reads the new value. No test does that
  yet.

## 5. The manifest's `native` block (done)

Step 5 is committed, and the slice is complete. These facts from step 5 matter later:

- `Manifest.Native` holds the lists of the `native` block and of its platform blocks.
  `PackageSources.Load` turns the lists for this platform into `NativeInputs`, which replaces the
  old `NativeDirectory` everywhere, and reports a missing path as `WYL0518`.
- The build gives the manifest's definitions to every C++ source, so a program built with
  definitions does not use the precompiled `wyl.h`. clang-cl refuses a file name in `/Fo` when its
  call has more than one input, so `/Fo` names a directory whenever the program's call also takes
  the precompiled object or a native object.
- `interop/manifest_native` links `shlwapi` on Windows, and fails to link without it. On Linux and
  macOS, it links `m`, which a C++ program links anyway, so only the spelling is tested there.
- `build-cache/native-rebuild` builds a package four times with the default toolchain and checks
  each run's output and the copied file. No test copies a shared library that the program loads.

## After this slice

`docs/design/runtime.md`'s "Interop" section, `CLAUDE.md`, and `architecture.md` describe what the
slice built. The roadmap's "Interop" list names what was proposed and is not queued yet. The next
slice, `interop-slice-2.md`, binds SQLite by hand.
