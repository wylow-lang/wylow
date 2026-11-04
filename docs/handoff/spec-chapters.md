# Next: rewrite the spec chapters (restructure step 4)

Load the `rigor` skill first. Repo `E:\Repos\Wylow\wylow`, branch `dev`. `spec-mapping.md` (this
directory) says where each passage of the old spec files (`02-types.md`, `04-errors.md` to
`07-interop.md`; `03-memory-model.md` is deleted) goes and
the rules for every rewrite; decision 0002 is the structure, chapter 1 the phrasing.

## Next session: chapters 18 to 22 and appendix A

The rewrites go in the order issue #6 gives: (2, 3), (4–6), (7–11), (12–15), (16–17),
(18–22 + appendix A). Chapters 1 to 21 are written, and parts of 22; the rest are anchors only.

- **Chapters 18 to 22** (`18-errors.md`, `19-concurrency.md`, `20-unsafe.md`, `21-interop.md`,
  `22-annotations.md`) and **appendix A** (`a-library.md`): from the rows of `spec-mapping.md` that
  point at `errors.*`, `concurrency.*`, `unsafe.*`, `interop.*`, `annotations.*`, and `library.*`,
  mostly in `04-errors.md`, `05-concurrency.md`, `07-interop.md`, and the remaining parts of
  `02-types.md` and `06-syntax.md`. Take them in that order: errors, concurrency, unsafe, interop,
  annotations, appendix A.
- **Diagnostics pages**: the catalog entries anchored in these chapters
  (`grep -E '"(errors|concurrency|unsafe|interop|annotations|library)[."]' src/Wyl.Compiler.Syntax/Diagnostics/Catalog*.cs`).
  All 14 of them (`WYL0421`–`WYL0434`) are still on `without-pages.txt` and need a page.
  Chapters 2 to 17's pages show the form; a code only a package produces gets a package example
  (`docs/diagnostics/README.md`).
- Delete each old passage once its new home is written, and update the rows of `spec-mapping.md`
  under "Already written".
- **Old passages still waiting.** Chapters 16 and 17 are written, but these passages stay until
  every row of them points at a written chapter:
  - `02-types.md` "Strings" (the `StringBuilder` row, #6) and "Bounds"
    (`library.printable`, `library.comparable`, appendix A). "Arrays and spans" and "Type
    properties" are deleted.
  - `06-syntax.md` §6.1's `std::panic` and `std::memory` (`library.panic`, `library.memory`,
    appendix A), §6.5 "No properties" (the guide), and §6.10 "Annotations" (chapter 22).
  - `04-errors.md`, `05-concurrency.md`, and `07-interop.md` are deleted.
- **Open bug found while writing chapter 11:** the compiler never finishes on a generic function or
  method that instantiates itself with growing type arguments (#22). `generics.recursion` states the
  error; it needs a catalog code, a page, and a test. Not part of the spec sessions.
- **Compiler restrictions that chapters 12 to 17 state** (specified, not implemented; #6): `this`
  receivers for extension methods, `defer`, `do`, indexing a type parameter, and moving a unique
  field out of a matched value with a pattern. Already known from earlier chapters: unique structs
  and their destructors, use after move, `discard`, `@Flags`, and enum `tryFrom`.

## Where it stands

The diagnostics catalog is merged (decision 0003): every diagnostic is a factory method in
`src/Wyl.Compiler.Syntax/Diagnostics/Catalog*.cs`, with a code (`WYL0001` to `WYL0437`, in spec-chapter
order) and the anchor of the spec section that makes it an error. Codes are now stable: **never
renumber one**, and give a new diagnostic the next number. `CatalogSelfTests` checks the catalog
(codes, anchors, a test per code, a page per code or a place on `docs/diagnostics/without-pages.txt`).

Chapters 22 and appendix A hold the anchors the catalog names, partly as empty sections. A
rewrite fills a section; the catalog test fails if an anchor it names disappears, so a section that
is renamed or merged is renamed in the catalog in the same change.

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
5. Commit each chapter once it passes, so a usage limit costs at most one chapter.

The last chapter's session deletes `spec-mapping.md`, the old files, this handoff, and the
"Chapters being rewritten" list in `docs/spec/README.md`.

## Rules added to the old files since the mapping was written

Decisions 0028 to 0038 put their rules into the old files, and `spec-mapping.md` has a row for
each. The ones whose chapters are written are now in the spec. These remain:

- **0036, `copyable` and `sendable` are type properties**: `types.copyable`, `generics.bounds`,
  and `concurrency.sendable` are written. Neither property is implemented (#6), so neither has
  tests yet.
- **0038, the built-in types' interfaces are declared**: `types.primitives.methods` is written;
  `library.printable` and `library.comparable` come with appendix A. Pinned by
  `basics/interpolation.wyl`, `errors/interpolation.wyl`, `errors/comparable.wyl`.
- **0028, destructors on unique structs**: the rules are written (`structs.unique`,
  `memory.holds`, `flow.moves`). Of the errors it lists, `WYL0146` ("cannot have destructors")
  would change to allow unique structs, and new codes are still needed for `discard` of anything
  but a by-value parameter of a unique type with a destructor, `discard` outside the type's file,
  and moving out of `this` in a destructor. None of this is implemented (#6).

The remaining old files still compare Wylow with other languages in places and use game examples;
the rewrite removes both.

## Open from the catalog

- `Binder.Visibility.cs` ("as in Java"), `Binder.Statements.cs` `Declare` ("Like Java"), and
  `Binder.Strings.cs` ("as in C#") still compare with another language without a source (CLAUDE.md,
  "Docs state checked facts"): cite or cut.
- Issue #6 lists each restriction that diagnostics state as rules (top-level variables, `const`,
  `@CallerLocation` and `ref` returns on virtual methods, `this(...)`/`super(...)` arguments,
  `extern`), checked 2026-11-02.
