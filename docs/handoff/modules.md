# Packages, modules, and `pub` visibility

**Status:** designed 2026-10-31 (spec `7f41683`, `33a499c`). Step 1 is done: `pub` levels and
file privacy on one file plus the standard library, with one definition of reach (`Reach.cs`) and
the checks in `Binder.Visibility.cs`. Step 2 is next. Delete this file when step 2 lands.

Read first: `docs/spec/06-syntax.md` §6.1–6.2 (the design, which wins over this file), CLAUDE.md,
and `docs/design/architecture.md`.

## Where this came from

A design review on 2026-10-31 dropped pools and operator overloading (indexers stay, with
`operator []=`), chose `fn(A) -> R` function types and Java 21 pattern additions, and, after one
round of Java-style access modifiers, settled on Rust-style explicit visibility with Go/Swift-style
directory modules:

- **Package** = a project: a directory with `package.wylcfg` and `src/`. A lone `.wyl` file is a
  one-module package. The whole package compiles together (cycles between its modules are fine;
  between packages, not). The standard library is the package `std`.
- **Module** = a directory. `src/` is the root; `src/world/gen/` is `world::gen`. Every file in a
  directory belongs to that module and shares its namespace; no `package` line.
- **Visibility:** nothing = this file; `pub(module)` = exactly the files of this directory (not
  submodules); `pub(package)`; `pub` = everyone. No type-private level, no `protected`.
- File-private names stay out of the module namespace (two files may each have a private `Node`).
- Paths use `::`, members `.`: `math::clamp(x)`, `world::Chunk.make()`, `Vec3.zero()`. Modules
  have their own namespace, so `panic::checkIndex` and `panic("...")` coexist, and a local may be
  named like a module.
- Per-file `import` (`import world::Chunk;`, `{A, B}`, `*`, `as`, a module itself, `pub import`
  re-exports). Paths appear only in imports.
- Rules: an override (or interface implementation) repeats the overridden visibility exactly; a
  visible signature (parameters, return, field type, base class, interfaces, their type arguments)
  can't name a less visible type; the memberwise constructor is as visible as its least visible
  field; visibility comes first among modifiers, after annotations; destructors, enum variants,
  and interface methods take none.
- Guideline: free functions for concepts (`math::clamp`), static methods for types (`Vec3.zero()`).

Why file privacy rather than Go's directory privacy: safe abstractions over `unsafe` (`List`'s
`data`/`size`) are only sound while nothing else can write their fields, so the privacy boundary
must stay small. `unsafe` belongs only in the floor (`Array`, `List`, `Span`, native `String`);
every other container is built safely on them.

## Step 1 (done): `pub` levels and file privacy on today's programs

`ce3c1c6`, then `3bea6b8` (`Bounds` is `pub`), `70d77ec` (`::` paths, `std::panic` in the spec),
and `e4deb4d` (snake_case files). `Visibility` is on each `Declaration` (`Ast.cs`); `Reach`
(`Semantics/Reach.cs`) is the one definition of what a visibility reaches; `Binder.Visibility.cs`
has `CheckVisible`, `VisibleFrom` (overloads), and the signature rule; override and implementation
visibility are in `Binder.Inheritance.cs`. Tests: `errors/visibility.wyl`,
`errors/visibility_syntax.wyl`, `classes/visibility.wyl`.

## Left open by step 1

- A subclass in another file declaring a method with the signature of a base method it can't see
  is allowed (`CheckOverride` returns early), but C++ would make it an override. Rename one in
  the emitter or reject it; untestable until a class can be extended from another file.
- Overload filtering by visibility (`VisibleFrom` in `Resolve`) has no test: no stdlib type has
  overloads of mixed visibility. Add one with the cross-module tests.
- Nothing in `std` is `pub(package)`, so no test reaches a package-level declaration from outside
  its package and fails. Add one with the cross-module tests.
- `Bounds` is `pub`: a container whose logical index isn't its storage index (a ring buffer on
  `Array`) needs its own check with the caller's location. It becomes the module function
  `std::panic::checkIndex` in step 2.
- `module` and `package` are reserved words in the lexer already, which `pub(...)` matches by text.

## Step 2: multi-file packages and imports

- The lexer gets a `::` token (there is none yet), and the parser reads paths with it in imports
  and in expressions (`gen::seed()`, `world::Chunk.make()`), which bind through the module
  namespace, apart from types, functions, and locals.
- `std::panic` (§6.1): `panic` moves into it, re-exported by `std`, and `Bounds.check` and
  `checkRange` become its `checkIndex` and `checkRange`. `struct Memory`, also only static
  functions, becomes a module the same way (§6.1: a type holding only functions is a module in
  disguise); its spec mentions (`Memory.allocate`, ...) change with it.
- The driver and CLI accept a package directory (with `package.wylcfg`) as well as one file;
  modules come from the directory tree under `src/`.
- The manifest (§6.1, The manifest): a reader in Syntax that uses Wylow's `Lexer` and reports
  syntax errors like any source, then a fixed schema. Step 2 needs only `name`; `dependencies`
  and `native` stay unimplemented (an error saying so), but the reader parses their shape.
- The binder gets one namespace per module (its files' non-file-private names plus submodules),
  per-file import scopes, the language's own std types seen without an import (no prelude, §6.1;
  which also replaces the "a program can't name a type like a stdlib type" rule in
  `docs/spec/README.md`), `pub import`, and the module-name rules.
- Name collisions: two files of a module declaring the same non-private name; a top-level module
  named like a dependency.
- The emitter maps module paths to C++ namespaces (or mangled names) so equal names in different
  modules can't collide in C++.
- The test runner gains multi-file cases (a directory under `tests/cases/` with `package.wylcfg`), and
  the cross-module visibility errors get tests there. The interpreter, fuzzer, and generator keep
  working on single files.
- Extensions are found through imports (§6.4).

### Decided with the user (2026-10-31), now in §6.1

No prelude beyond the types the language is built on (`StdlibSymbols`' hooks); an import that
clashes with the module's own name or another import is an error, `*` gives way and is ambiguous
only at a use; a name hides overloads from elsewhere (no merging); a path in code starts with an
imported module or one of the file's own submodules (Rust without its "dependencies anywhere").
The user wants the no-prelude choice revisited as std changes.

### Working assumptions (proposed, not objected to; put each in the spec when implemented)

- `std` stays embedded, with no manifest; its resources mirror its modules (root, `panic/`, `memory/`).
- A lone file's package is unnamed ("only in this package"), and its C++ names are unprefixed.
- C++ names: the built package's root module unprefixed (`c_Chunk`); any other module prefixed with
  its length-prefixed package and path (`c_3std_List`, `c_5world3gen_Noise`).
- Locations print the path under `src/` (`world/chunk.wyl:3:5`).
- A directory holding `.wyl` files whose name isn't an identifier, or is a keyword, is an error; no
  `src/` is an error; `.wyl` outside `src/` is ignored; `main` outside the root module is a function.
- A subclass method with the signature of a base method it can't see is a new method: a method's
  C++ name comes from the root method it overrides.
- Multi-file test: a directory with `package.wylcfg`; `// error:` in any file (the manifest too);
  `// output:`, `// panic:`, `// mode:` only in the file with `main`. The fuzzer skips them (done).
- A function's overloads may be in any files of a module; no file may see one signature twice
  (decided with the user, in §6.1).
- The cycle collector names a class by its name alone, as before; a class's module path in its
  diagnostics is open.

### Commits planned

1. `lexer, parser`: `::`, import declarations, paths in expressions and types (done, `2400d1f`).
2. `binder, emitter`: `SourceText.Module` and `Package` (`Text/Packages.cs`), `Reach` from them, C++
   names qualified by module, `TypeSymbol.RuntimeName` (done).
3. `cli, binder`: package directories, the manifest, per-module namespaces with file-private
   scopes, multi-file test cases (done). Until 4, every file still sees all of `std`, functions
   merge across tables, and a program can't redeclare a std type's name.
4. `binder`: imports, paths in code, std's built-in names, collisions, cross-module visibility
   tests (done). Imports resolve lazily (`Binder.Imports.cs`); every lookup is
   `Binder.ResolveName`/`ResolveModule` (`Binder.Names.cs`). Self-test programs see std through
   `Wyl.Harness/TestPrograms.WithStd`. Left for 5: a package-level miss, which needs a
   `pub(package)` declaration in std.
5. `stdlib`: `std::panic` and `std::memory`.
6. `emitter`: a method doesn't override a base method it can't see; delete this file.

## After this

`fn(Params) -> Return` function types (§6.7; the parser, `FunctionType.BuildName`, error messages,
every test), then the pattern additions (§6.9: `when` guards, struct patterns, `case null`), then
`operator []=` (§6.6). Design sessions queued with the user: threading (statics, what jobs may
call, isolates), the stdlib's collections and iteration interfaces, and a proposal to make the
cycle collector easier or automatic. The live list is the roadmap, GitHub issue #6.

## Working notes

- Commit each step on `dev` after the pre-commit check (whole suite `--interpret`, plus MSVC
  filtered to the area, e.g. `errors` and `classes`); don't push. Subjects are `area: what
  changed`; comments cite the spec, never this file.
- A change that shifts lines in the matrix generators changes panic locations in
  `tests/matrix/*.snapshot`. Update with `--interpret matrix --update-snapshots`, then check the
  diff differs only in numbers (strip digits from the removed and added lines and compare).
- The Bash tool can eat backslashes in heredocs; write scripts with the file tool instead.
- `tests/cases/basics/.wyl` and `tests/cases/classes/.wyl` are stray untracked directories; skip
  directories when globbing.
- The test runner takes one filter per run (`Wyl.Tests.dll errors`), so run MSVC per area.
- In PowerShell, `git commit -F -` with a here-string doesn't work; write the message to a file in
  the scratchpad and pass `-F <file>`.
- Stdlib files are snake_case (`list.wyl`), and error messages print them ("visible only in its
  file (list.wyl)").
