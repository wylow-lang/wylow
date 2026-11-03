# Next: rewrite the spec chapters (restructure step 4)

Load the `rigor` skill first. Repo `E:\Repos\Wylow\wylow`, branch `dev`. `spec-mapping.md` (this
directory) says where each passage of the old spec files (`02-types.md` to `07-interop.md`) goes and
the rules for every rewrite; decision 0002 is the structure, chapter 1 the phrasing.

## Next session: chapters 16 and 17

The rewrites go in the order issue #6 gives: (2, 3), (4–6), (7–11), (12–15), (16–17),
(18–22 + appendix A). Chapters 1 to 15 are written, and parts of 20; the rest are anchors only.

- **Chapters 16 and 17** (`16-flow.md`, `17-memory.md`): from the rows of `spec-mapping.md` that
  point at `flow.*` and `memory.*`, mostly in `03-memory-model.md` and the nullability, unique-struct,
  and reference parts of `02-types.md`.
- **Diagnostics pages**: the catalog entries anchored in these chapters
  (`grep -E '"(flow|memory)[."]' src/Wyl.Compiler.Syntax/Diagnostics/Catalog*.cs`).
  Chapters 2 to 15's pages show the form; a code only a package produces gets a package example
  (`docs/diagnostics/README.md`).
- Delete each old passage once its new home is written, and update the rows of `spec-mapping.md`
  under "Already written".
- **Old passages still waiting.** Chapters 7 to 15 are written, but these passages stay until every
  row of them points at a written chapter:
  - `02-types.md` §2.5 "Nullability": narrowing (`flow.narrowing`, chapter 16), references into
    nullable values (`memory.references`) and `ref T?`/`ref? T` (`memory.nullable-references`).
  - `02-types.md` §2.7 "Mutability and constants": variables assigned once (`flow.assign-once`).
  - `02-types.md` "Unique structs" and "Destructors of unique structs" (`memory.moves`,
    `flow.moves`, `memory.holds`), "Construction" of classes (`flow.constructors`), "Bounds"
    (`library.printable`, `library.comparable`), and "Type properties" (`concurrency.sendable`).
  - `06-syntax.md` §6.3's definite-assignment rule (`flow.definite-assignment`), §6.7's
    "Escaping and non-escaping" remaining row and its `flow.lambdas` bullet, §6.9 "Unreachable
    statements" (`flow.returns`) and "Reference bindings" (`memory.second-class`).
  - `06-syntax.md` §6.5 "No properties" (the guide), and §6.1's `std::panic` and `std::memory`
    (`library.panic`, `library.memory`, appendix A).
- **Open bug found while writing chapter 11:** the compiler never finishes on a generic function or
  method that instantiates itself with growing type arguments (#22). `generics.recursion` states the
  error; it needs a catalog code, a page, and a test. Not part of the spec sessions.
- **Compiler restrictions that chapters 12 to 15 state** (specified, not implemented; #6): `this`
  receivers for extension methods, `defer`, `do`, indexing a type parameter, and moving a unique
  field out of a matched value with a pattern. Already known from earlier chapters: unique structs
  and their destructors, `discard`, `@Flags`, and enum `tryFrom`.

## Where it stands

The diagnostics catalog is merged (decision 0003): every diagnostic is a factory method in
`src/Wyl.Compiler.Syntax/Diagnostics/Catalog*.cs`, with a code (`WYL0001` to `WYL0434`, in spec-chapter
order) and the anchor of the spec section that makes it an error. Codes are now stable: **never
renumber one**, and give a new diagnostic the next number. `CatalogSelfTests` checks the catalog
(codes, anchors, a test per code, a page per code or a place on `docs/diagnostics/without-pages.txt`).

The new chapter files (`01-introduction.md`, `02-lexical.md`, `03-programs.md`, `04-types.md`, ...)
hold the anchors the catalog names, mostly as empty sections. A rewrite fills a section; the
catalog test fails if an anchor it names disappears, so a section that is renamed or merged is
renamed in the catalog in the same change.

## For each chapter

1. Write its sections from the rows of `spec-mapping.md` that point at it, then delete the old
   passages that are now written.
2. Check every rule against the tests (`tests/cases`), and say in the report which rule has none.
3. Write the diagnostics pages of the codes whose anchors are in the chapter:
   `docs/diagnostics/WYLnnnn.md` (what it means, a short example, how to fix it, the spec section;
   the first `wyl` block is a whole program that produces exactly that code), and remove each code
   from `docs/diagnostics/without-pages.txt`. The test fails until it is removed.
4. Message wording a page shows follows `docs/diagnostics/README.md`. If a rewrite changes what a
   rule says, change the catalog method's message and the tests' markers in the same change.

The last chapter's session deletes `spec-mapping.md`, the old files, and this handoff.

## Rules added to the old files since the mapping was written

Decisions 0028 to 0032 put their rules into the old files, and `spec-mapping.md` has a row for
each. When rewriting the chapter a row points at, write these too:

- **0028, destructors on unique structs** (`02-types.md`, "Destructors of unique structs";
  `03-memory-model.md`, values in use): to `structs.unique`, `memory.holds`. Of the errors it lists,
  a destructor's modifiers and parameters already have codes for classes (`WYL0144`, `WYL0149`,
  anchored at `classes.destructors`), and `WYL0146` ("cannot have destructors") would change to
  allow unique structs. New codes are needed for `discard` of anything but a by-value parameter of a
  unique type with a destructor, `discard` outside the type's file, and moving out of `this` in a
  destructor. None of this is implemented (#6).
- **0029, the end of the program** (`05-concurrency.md` §5.5): to `variables.statics`;
  `programs.execution` is written.
- **0030, statics initialized before `main`** (`05-concurrency.md` §5.5): to `variables.statics`.
  Static fields aren't implemented (#6), so it has no tests yet.
- **0031, elements released last to first** (`03-memory-model.md`, When references are released):
  to `memory.release.destruction`; implemented, pinned by `containers/release_order.wyl`.
- **0032, generic types have no static fields** (`05-concurrency.md` §5.5): to
  `variables.statics`; its error and catalog code come when static fields are implemented.
- **0036, `copyable` and `sendable` are type properties** (`02-types.md`, "Type properties";
  `05-concurrency.md` §5.2): to `types.copyable`, `generics.bounds`, `concurrency.sendable`. There
  is no `Copyable` declaration any more; `programs.builtin-names` and `library.names` already drop
  it. Neither property is implemented (#6), so neither has tests yet.
- **0037, interfaces the language calls**: written in `library.names`; a struct or enum is not
  `Borrowable` through an extending interface either (`03-memory-model.md`, `Borrowable`), pinned by
  `errors/containers.wyl`. To `memory.borrows.borrowable`.
- **0038, the built-in types' interfaces are declared** (`02-types.md`, Bounds; the interpolation
  rules are written in `expressions.interpolation`, and the declaration belongs to appendix A): to
  `library.printable`, `library.comparable`, and `types.primitives.methods`
  (each primitive's `extern struct` in `Stdlib/primitives.wyl`). Pinned by
  `basics/interpolation.wyl`, `errors/interpolation.wyl`, `errors/comparable.wyl`.
- **`is ref` holds** (`03-memory-model.md`, Nullable references): a hold lasts until the statement
  ends, even where the rest of the condition is false, pinned by
  `panics/list_reference_pattern_held_in_else.wyl` and `..._guard_held.wyl`. To `memory.holds`.

The old files still compare Wylow with other languages in places (`05-concurrency.md`: Rust's
`unsafe impl Send`, `std::jthread`, Rust's scoped threads), and use game examples; the rewrite
removes both.

## Open from the catalog

- `Binder.Visibility.cs` ("as in Java"), `Binder.Statements.cs` `Declare` ("Like Java"), and
  `Binder.Strings.cs` ("as in C#") still compare with another language without a source (CLAUDE.md,
  "Docs state checked facts"): cite or cut.
- Issue #6 lists each restriction that diagnostics state as rules (top-level variables, `const`,
  `@CallerLocation` and `ref` returns on virtual methods, `this(...)`/`super(...)` arguments,
  `extern`), checked 2026-11-02.
