# Next: rewrite the spec chapters (restructure step 4)

Load the `rigor` skill first. Repo `E:\Repos\Wylow\wylow`, branch `dev`. `spec-mapping.md` (this
directory) says where each passage of the old spec files (`02-types.md` to `07-interop.md`) goes and
the rules for every rewrite; decision 0002 is the structure, chapter 1 the phrasing.

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

## Open from the catalog

- `Binder.Visibility.cs` ("as in Java"), `Binder.Statements.cs` `Declare` ("Like Java"), and
  `Binder.Strings.cs` ("as in C#") still compare with another language without a source (CLAUDE.md,
  "Docs state checked facts"): cite or cut.
- Issue #6 does not yet list the restrictions that diagnostics now state as rules (`variables cannot
  be declared at the top level`, `'const' is a reserved word and cannot be used`, the `'@CallerLocation'`,
  `'ref'`-returning virtual, `this(...)`/`super(...)` argument, and `extern` rules); check each.
