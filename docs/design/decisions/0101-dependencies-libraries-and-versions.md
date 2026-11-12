# 0101. Dependencies are local paths, a library declares its kind and version, and a version is a check

- Status: accepted (2026-11-12)
- Spec: `programs.packages`, `programs.manifest`, `programs.manifest.versions`,
  `programs.manifest.dependencies`, `programs.imports`, `programs.paths`, `programs.entry`,
  `errors.panics`, `interop.native`

## Decision

- **Kinds.** A package is a program or a library, and the manifest's `kind` entry says which. A
  package without the entry is a program. A program must have `main`, and a library must not. A
  library's demonstration is a separate program that depends on the library.
- **Sources.** A dependency names a directory with `path`, relative to the manifest that names it.
  The compiler reads no other source: it has no registry, no git source, no lock file, and no fetch
  step. A package that needs a prebuilt C library ships a script that fetches it.
- **Names.** An entry's key must equal the `name` in the dependency's manifest. A file that wants
  another name writes `import x as y`.
- **Versions.** A library must declare `version = "major.minor.patch"`. A program may declare one.
  An entry of `dependencies` may state the version it needs, as `"major.minor"` or
  `"major.minor.patch"`. The library satisfies the stated version if it is not lower and has the
  same major number. Below 1.0, the minor number must also be the same. The compiler checks the
  version and reports a mismatch as an error. It never chooses between versions, because `path`
  names exactly one copy.
- **Paths.** A dependency's name is a path root in every file of the package, as `std` is, and needs
  no import. It means the dependency's root module. A module directly in `src/` must not have a
  dependency's name.
- **The graph.** A package sees only its own dependencies. A library re-exports a name of its own
  dependency with `pub import`. Two paths that reach one directory name one package, which is
  compiled once. Two packages of one name in different directories are an error, and so is a cycle
  of dependencies.
- **Native settings.** Each package's native paths resolve against its own directory. Each
  package's native sources compile with that package's own `include` and `defines` lists. An
  extern block finds its header only in its own package's `native/` and `include` directories. The
  `link`, `libraryPaths`, and `copy` lists of every package apply to the program. Every package's
  `defines` reach the C++ that includes an extern block's header. Two packages that define one name
  with different values are an error, and so are two `copy` files with one file name.
- **Panic locations.** A file of a dependency is named with the library's name and `::` before its
  path: `parser::lexer/tokens.wyl:3:5`. A file of the program keeps its path alone.
- **Compilation.** A program is compiled from source together with every library it depends on, as
  it is compiled with `std` today. No Wylow library is prebuilt.
- **Warnings.** When the compiler builds a program, it reports the errors of a dependency and not
  its warnings. A library's author sees the warnings by checking the library on its own. The spec
  does not state this rule, because which warnings an implementation reports is not part of the
  language (`introduction.scope`).

## Alternatives

- **A package without `main` is a library, with no `kind` entry.** If a program's `main` were
  deleted or renamed, the program would become a library without an error, and the error would
  appear only when something tried to run it. A declared kind gives each mistake its own error at
  the package that made it.
- **A dependency's `main` as an ordinary function.** A library could then keep its demonstration in
  itself. A separate package that depends on the library also tests the library the way its users
  will use it, through `pub` names only.
- **No versions.** If the language has no version, each library writes its version its own way, in
  a comment, a file name, or a constant, and no tool can compare two of them. A version that the
  compiler reads costs one manifest entry.
- **Versions with a resolver**, which picks one version from a registry to satisfy several ranges
  ([Cargo's resolver](https://doc.rust-lang.org/cargo/reference/resolver.html) works this way).
  Vendoring places one copy of each library in the program's tree, so there is nothing to choose
  between. The check catches a vendored copy that was updated across a major version.
- **An exact version in every entry.** Each patch release of a vendored copy would then need an
  edit in every manifest that names it. The rule of [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html),
  that a release with the same major number keeps compatibility, lets an entry state the oldest
  version it works with.
- **Version ranges such as `">=1.2, <1.6"`.** A range is useful only when a resolver chooses inside
  it.
- **Pre-release and build suffixes**, as in `1.4.0-beta.1`. Nothing needs them yet. They can be
  added later without changing what a three-number version means.
- **The key as an alias of the dependency's name.** One package would then have different names in
  different packages of one program, and a reader would have to look up each manifest to know what
  `parser::` means.
- **Every package of the program visible everywhere.** A program would then see the libraries that
  its libraries chose, and a library could not replace one of its own dependencies without breaking
  a program that had started to use it.
- **A dependency's name as a path root only after `import parser;`.** `std` already needs no import,
  and decision [0072](0072-std-is-a-path-root.md) gives the reasons. A dependency is the same kind
  of thing, a package that the program names by its root.
- **One include path for every package, searched in package order.** Two libraries that each have a
  `native/util.h` would then find the first library's header, and the result would depend on the
  order of the manifest's entries.
- **A panic location of `parser/lexer/tokens.wyl`.** It looks like a file in a module directory
  named `parser`. `::` never appears in a file path, so `parser::` can name only a package.
- **Reporting a dependency's warnings in the program's build.** The program's author cannot fix them
  without editing the vendored copy, and they would hide the program's own warnings.

## Why

Vendoring keeps a program's whole build in its own tree: the build never reaches a network, and a
reviewer reads the exact code that the program compiles. The manifest therefore describes where
each library is and what the program expects of it. It never describes how to obtain a library. A
version entry turns an expectation that a reader once kept in their head into a check that the
compiler makes.

The rules for names reuse the ones that `std` already follows, so a dependency adds no new kind of
lookup. The rules for native settings follow from one requirement that the SQLite binding showed:
a library's C code must build the same way inside every program that uses the library. For that
reason each package's paths, include directories, and definitions stay attached to the package.
