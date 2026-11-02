# Next: the diagnostics catalog (restructure step 3)

Load the `rigor` skill first. Repo `E:\Repos\Wylow\wylow`, branch `dev`.

## Where the restructure stands

Roadmap issue #6, §2, "Spec and docs restructure" has the four steps. Steps 1 and 2 are done:
decision records 0001–0004 (`docs/design/decisions/`), the message style guide
(`docs/diagnostics/README.md`), chapter 1 of the new spec in full, chapters 2–22 and appendix A as
stubs with named anchors, implementation text in `docs/design/runtime.md`, rationale in records
0005–0026, and `docs/handoff/spec-mapping.md`, which maps every remaining old section to its new
home for step 4.

This session is **step 3, the diagnostics catalog**, as decision 0003 describes it, after a short
cleanup of step 1–2 work.

## First: fix what the review of steps 1–2 found

A review on 2026-11-02 found these. Fix them, in their own commit, before the catalog:

1. **`docs/diagnostics/README.md` uses game examples** (`world.wyl`, `'chunk'`, `Chunk`,
   `voxel_game::world::Chunk`, `enemy.wyl`, `'spawn(Vec3, int)'`, `'Vec3'`). Replace them with generic
   ones (`CLAUDE.md`, "Examples are generic"). Also check its worked examples against its own rule
   that a primary message is one statement ("'?.' on a value of non-nullable type 'Vec3'" isn't),
   and its "as rustc trims paths" against the citation rule (cite or cut).
2. **Uncited comparisons in decision records**: 0010 ("as in C++"), 0018 ("Swift and Go have the
   same rule"), 0017 ("This is Pony's `recover` block"), 0001 ("Go's FAQ inlined", which is also
   wrong: Go's FAQ is a separate document). Name the exact feature and link its primary source, or
   cut the comparison. Check the other records' comparisons the same way while there (most name a
   feature but give no link). `runtime.md`'s "mimalloc's design" too. A record's text is frozen once
   accepted, but these were written a day earlier and the fix changes no decision, so edit in
   place and say so in the commit.
3. **Decision 0005 (design principles)** was copied from the old README unchanged. "Every check can
   be turned off" contradicts 0025 (`unsafe` never removes checks), and "a stack trace" isn't what
   chapter 1 promises (a message and a location). Supersede it with a corrected record rather than
   editing an old decision's substance.
4. **`docs/guide/README.md`** is the old surprises list copied verbatim: comparisons, game examples,
   15 links to old chapter files that step 4 deletes, and stale claims (surprise 2 says a static
   needs only to be sendable; it must be copyable too). Mark it at the top as unrevised source
   material to be rewritten after step 4; don't rewrite it now, since the chapters it links to
   don't exist yet.

## Then: the catalog

Decision 0003 is the design; the roadmap item lists the parts. In order:

1. `Catalog*.cs` in the Syntax project, split by spec chapter: one typed factory method per
   diagnostic, declaring its code, severity, template, notes, and spec anchor. `Diagnostic`'s
   constructor becomes `internal`; `DiagnosticBag.Error(Location, string)` is deleted;
   `IrVerifier.Violation` takes a `Diagnostic`.
2. Move every message (about 350 in the binder and parser, plus the verifier's user errors) into
   it, rewritten to the style guide, with `note:`/`help:` lines and locations where they help.
   Codes go out in spec-chapter order.
3. Migrate the tests by script to `// error[WYLnnnn]: <exact message>` (about 1,050 lines), and
   change the runner to match both exactly.
4. The catalog test: unique codes, retired codes never reused, every anchor exists, every code
   produced by some test, and a page per code whose example produces it, with a shrinking
   allowlist for pages not written yet (step 4 writes most of them).
5. Two rows in `docs/design/architecture.md`'s enforcement table.

This is one or two sessions. A message's wording is the user's call where the style guide doesn't
decide it: collect those as a short list rather than guessing.

## When finished

Report as `docs/design/working.md` says (done, decided without approval, open, next). Update issue
#6. Replace this handoff with the next one: step 4, the chapter rewrites, which use
`spec-mapping.md`.
