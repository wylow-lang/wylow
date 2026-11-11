# 0086. The manifest's `native` block, and a package's `native/` directory

- Status: accepted (2026-11-10)
- Spec: `programs.manifest`, `interop.native`

## Decision

The manifest's `native` block holds lists of strings under `include`, `libraryPaths`, `link`,
`copy`, and `defines`. It may hold a `windows`, `linux`, or `macos` block with the same keys, and the
lists of that block are added when the program is built for that platform. Each path is relative to
the package's directory. `link` names a library without the platform's prefix or extension. `copy`
names files that are copied next to the built program, such as a shared library that the program
loads when it starts.

A package's `native/` directory holds C++ sources and headers. Each `.cpp` file in it is compiled and
linked with the program, and the directory is on the include path.

## Alternatives

- **Only `include` and `link`, as the spec had.** A library that is not installed system-wide needs a
  directory to be found in, and a program that loads a shared library needs that library next to it.
  A header also often needs a definition before it is included, so `defines` is needed too.
- **Conditions in the manifest's syntax, such as `if windows { ... }`.** Three named platform blocks
  cover what a package needs today, and the manifest's grammar stays the same.
- **Native source files listed in the manifest.** A directory needs no list to keep up to date, and
  `src/` already works this way for Wylow files.

## Why

Each entry is something that a build of a program using a C or C++ library cannot do without. The
build cache key must cover the entries and every file in `native/`. Otherwise a changed shim would
reuse an executable built from the old shim.
