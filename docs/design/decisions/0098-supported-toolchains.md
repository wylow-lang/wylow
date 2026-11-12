# 0098. Each supported C++ compiler has an oldest version, and clang-cl comes from Visual Studio

- Status: accepted (2026-11-12)
- Spec: `interop.native`

## Decision

A native source under `native/` is C++20. The spec names the language of a native source and no
compiler.

The compiler builds programs only with a supported C++ compiler, in a version that is at least the
oldest supported one. `ToolchainVersions` holds each oldest version, and `docs/design/runtime.md`
("Toolchains") lists them with their platforms. The oldest supported version of a compiler is the
oldest version that CI runs. Every CI job runs on a pinned runner image, so the versions that CI
runs change only when a commit changes the image.

If the compiler finds a C++ compiler that is older than the oldest supported version, it refuses to
build. The message names the version that it found and the oldest version that it accepts. A newer
version than CI runs is accepted.

On Windows, clang-cl is the one in the Visual Studio installation whose MSVC environment the build
uses (`VC\Tools\Llvm\x64\bin`). The compiler does not look for clang-cl on `PATH`. The build no
longer defines `_ALLOW_COMPILER_AND_STL_VERSION_MISMATCH`.

The compiler does not build programs on macOS. `wyl interpret` runs a program there.

## Alternatives

- **No oldest version.** The compiler would try any version it finds. A version that is too old
  then fails inside the C++ build, and the failure looks like a compiler bug, because a failed C++
  build is one (architecture.md). A user cannot tell an old compiler from a wrong program.
- **A warning instead of a refusal.** A warning lets the build go on with a compiler that no test
  has run. If that build fails or miscompiles, the failure still looks like a bug in Wylow.
- **The oldest version that compiles the runtime today.** That version is not tested, so it would
  stay supported only until a change used something newer, and no test would notice the change.
- **clang-cl from `PATH`, as before.** The clang-cl on `PATH` may be older than the MSVC standard
  library expects. The MSVC 14.51 library stops the build with error `STL1000` if
  `__clang_major__` is less than 20 (`yvals_core.h`), and the build defined
  `_ALLOW_COMPILER_AND_STL_VERSION_MISMATCH` to get past the error with clang-cl 19. Visual
  Studio's "C++ Clang tools for Windows" component installs a clang-cl that Microsoft updates "to
  stay current with updates in the Microsoft implementation of the Standard Library"
  ([Clang/LLVM support in Visual Studio](https://learn.microsoft.com/en-us/cpp/build/clang-support-msbuild)).
  The clang-cl and the standard library of one installation therefore match.
- **clang-cl from `PATH` if it is new enough, and Visual Studio's otherwise.** The build would then
  depend on which LLVM the machine happens to have, and the oldest clang-cl version would follow
  each Visual Studio release. Visual Studio's clang-cl has neither problem.
- **The spec names the compilers.** The spec defines the language, and
  [0001](0001-document-set.md) keeps implementation facts out of it. The language of a native source is part of what a package may
  contain, so the spec states it.
- **Support macOS through Apple clang.** No CI job runs on macOS, so no test would show that a
  program builds there. A manifest may still have a `macos` block, because the manifest's format
  does not depend on what the compiler builds.

## Why

A program's C++ is compiled by a compiler that Wylow does not control, and the safety rules hold
only if that compiler compiles the C++ correctly. CLAUDE.md records an MSVC 14.50 and 14.51
miscompile that the emitter works around. The compiler can trust only a version that the test
suite has run, so the supported versions are the versions that CI runs.
