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

- **Package** = a project: a directory with `wylow.toml` and `src/`. A lone `.wyl` file is a
  one-module package. The whole package compiles together (cycles between its modules are fine;
  between packages, not). The standard library is the package `std`.
- **Module** = a directory. `src/` is the root; `src/world/gen/` is `world.gen`. Every file in a
  directory belongs to that module and shares its namespace; no `package` line.
- **Visibility:** nothing = this file; `pub(module)` = exactly the files of this directory (not
  submodules); `pub(package)`; `pub` = everyone. No type-private level, no `protected`.
- File-private names stay out of the module namespace (two files may each have a private `Node`).
- Per-file `import` (`import world.Chunk;`, `{A, B}`, `*`, `as`, a module itself, `pub import`
  re-exports). Paths appear only in imports; a local can't take an imported module's name.
- Rules: an override (or interface implementation) repeats the overridden visibility exactly; a
  visible signature (parameters, return, field type, base class, interfaces, their type arguments)
  can't name a less visible type; the memberwise constructor is as visible as its least visible
  field; visibility comes first among modifiers, after annotations; destructors, enum variants,
  and interface methods take none.
- Guideline: free functions for concepts (`math.clamp`), static methods for types (`Vec3.zero()`).

Why file privacy rather than Go's directory privacy: safe abstractions over `unsafe` (`List`'s
`data`/`size`) are only sound while nothing else can write their fields, so the privacy boundary
must stay small. `unsafe` belongs only in the floor (`Array`, `List`, `Span`, native `String`);
every other container is built safely on them.

## Step 1: `pub` levels and file privacy, on today's programs

No multi-file user programs yet, but the user's file and each `Stdlib/*.wyl` file are already
separate sources, and the stdlib is another package (`std`), so file and package checks are real
now.

- **Syntax** (`Lexer.cs`, `Token.cs`, `Parser.cs`, `Parser.Declarations.cs`, `Ast.cs`): add the
  `pub` keyword, with an optional `(module)` or `(package)`; remove `public`, `protected`, and
  `private` as access keywords (a leftover one gets one clear error pointing at `pub`, like the
  leftover access block does now). Replace `Modifiers.Public/Protected/Private` with a visibility
  value on declarations (an enum: File, Module, Package, Public), not three flags that can combine.
  The parser's struct/enum `protected` errors and the interface "no access modifier" error become
  the `pub` equivalents.
- **Symbols and the binder** (`Symbols.cs` `IsPrivate`, `Binder.Expressions.cs` `CheckAccessible`,
  `Binder.Calls.cs`, `Binder.Statements.cs`, `Binder.Inheritance.cs`, `Binder.Declarations.cs`):
  a symbol records its visibility and its declaring source and package. `CheckAccessible` asks
  "is the use's file/module/package inside the declaration's reach", replacing
  `currentType != owner`. One definition of reach, used by every caller (fields, methods,
  constructors, base calls, overrides, the memberwise constructor).
- **New checks:** override and interface-implementation visibility must match exactly; a visible
  signature can't name a less visible type; a member is capped by its type's visibility.
- **Stdlib:** everything a program uses becomes `pub`; helpers shared between stdlib files become
  `pub(package)`; `List.data`, `size`, and the like stay unmarked (file-private). Names the
  compiler finds through `StdlibSymbols` must stay findable.
- **Conversion:** `public` → `pub`, `private` → nothing, `protected` → whatever the use needs
  (usually nothing in a one-file test), across `tests/cases`, the stdlib, `examples/`, and the
  Wylow embedded in `tests/Wyl.Tests/*.cs` (matrix generators, self-tests). Script it; the last
  conversion was one Python script over every file, then a hand pass over the templates in
  `MatrixPlaces.cs` and `TypeSelfTests.cs`.
- **Tests:** single-file "X is private" tests stop being errors (one file sees everything), so
  they become passing tests of same-file access, or move to reaching a stdlib file's private member
  (`list.size`), which must fail. New error tests: a leftover Java keyword, a bad `pub(...)`, an
  override with different visibility, a `pub` signature naming a file-private type. Cross-module
  cases wait for step 2.
- **Docs in the same commit:** the "Not implemented yet" note in §6.2, CLAUDE.md if it mentions
  access, and architecture.md's enforcement table for the reach definition.

## Left open by step 1

- `Reach.PackageOf`/`ModuleOf` say "std or the program, one root module each"; step 2 replaces
  them with the source's real package and module path, and nothing else should need to change.
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

- The driver and CLI accept a package directory (with `wylow.toml`) as well as one file; modules
  come from the directory tree under `src/`.
- The binder gets one namespace per module (its files' non-file-private names plus submodules),
  per-file import scopes, the `std` prelude (implicit imports a module's own names override, which
  also lifts the "a program can't name a type like a stdlib type" rule in `docs/spec/README.md`),
  `pub import`, and the module-name rules.
- Name collisions: two files of a module declaring the same non-private name; a top-level module
  named like a dependency.
- The emitter maps module paths to C++ namespaces (or mangled names) so equal names in different
  modules can't collide in C++.
- The test runner gains multi-file cases (a directory under `tests/cases/` with `wylow.toml`), and
  the cross-module visibility errors get tests there. The interpreter, fuzzer, and generator keep
  working on single files.
- Extensions are found through imports (§6.4).

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
- `tests/cases/basics/.wyl` is a stray untracked directory; skip directories when globbing.
