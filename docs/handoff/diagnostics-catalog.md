# Next: finish the diagnostics catalog (restructure step 3)

Load the `rigor` skill first. Repo `E:\Repos\Wylow\wylow`, **branch `diagnostics-catalog`** (not
`dev`). Decision 0003 is the design; `docs/diagnostics/README.md` is the style guide.

## Where it stands

Done on `dev`: the review fixes of steps 1–2 (`102ecdc`): comparisons in decision records cited or
cut, 0005 superseded by 0027, the style guide's examples made generic, the guide marked as
unrevised; and the "Java's syntax" claim removed (`24c35ad`).

Done on the branch (`6d8c001`), with the whole suite passing under `--interpret`:

- `src/Wyl.Compiler.Syntax/Diagnostics/Diagnostic.cs`: `Diagnostic` has a `DiagnosticDescriptor`
  (code, severity, anchor), `note:`/`help:` lines (`AttachedLine`), and an `internal`
  constructor. `DiagnosticBag.Add(Diagnostic)`.
- `Catalog.cs` plus `Catalog.<Chapter>.cs`: one `public static` factory per diagnostic, with
  `[Error(number, "anchor")]`; the body calls `Make(at, message)` and chains `.Note(...)` /
  `.Help(...)`. A method name is its identity (`[CallerMemberName]`), so no overloads.
- Migrated: the lexer, `Manifest.cs`, `PackageSources.cs`, and the parser (its `Report` keeps the
  one-error-per-position rule; `Disallow` takes a `Func<Token, Diagnostic>`).
- The runner (`tests/Wyl.Tests/Program.cs`) matches `// error[WYLnnnn]: <message>` exactly; those
  178 markers are migrated. The fuzzer's corpus filter (`Wyl.Fuzz/Mutator.cs`) knows the new form.

**Temporary, all deleted before the merge**: `DiagnosticBag.Error` and `Catalog.Legacy` (code
9999), which bridge the unmigrated callers; the runner's uncoded `// error:` substring match; and
`tools/catalog-migration/`.

## What's left, in order

1. **The binder** (`src/Wyl.Compiler.Semantics/Binder/`, about 300 sites: `Error`,
   `ErrorExpression`, `ErrorPattern`, `ReportMisplacedAnnotation`, `ReportNotEscaping`). Change the
   helpers to take a `Diagnostic` (`ErrorExpression(Diagnostic)` returning a
   `BoundErrorExpression` at its location). The hard part is the string-building helpers, which
   must become catalog calls or notes: `HiddenElsewhere`, `PrivateIn`, `DescribeModule`,
   `Reach.Describe`, `VariantHint`, `NullableOperandHint`, `NoInstanceReason`, `ReferenceHint`,
   `OverloadEscapingHint`, `EscapingDifference`, `MayReferToNothingMessage`,
   `NotFunctionTypeMessage`, `NonEscapingMutMessage`, `StoreTemporaryHint`, `UnsafeReason`, and
   the `failure.Message`/`mismatch.Message` records in `Binder.Overloads.cs` and
   `Binder.Lambdas.cs`. A hint appended to a message becomes a `help:` or `note:` line.
2. **The verifier**'s user errors (`src/Wyl.Compiler.Analysis/IrVerifier*.cs`: `Violation`,
   `ReportNullable`, `ReportNullableValue`, `MissingReturn`; not `Error`/`Report`, which are compiler
   bugs). `Violation` takes a `Diagnostic`; it works out the location itself
   (`at ?? captureOrigin ?? current?.Origin ?? function.Body.Origin`), so have it take a
   `Func<Location, Diagnostic>` or compute the location first, and say which in the commit.
   Without a bag (after a pass) it reports the diagnostic's message as a compiler bug.
3. **Delete the bridges** above, and the runner's substring path.
4. **Renumber** every `[Error(n, ...)]` in spec-chapter order (chapter, then the anchor's order in
   its chapter, then declaration order), with a script. Current numbers are placeholders. Then
   rerun the migration so the markers carry the final codes (or renumber markers by the same map).
5. **The catalog test** (in `tests/Wyl.Tests`): codes unique, none in `Catalog.Retired`; every
   anchor exists in `docs/spec/` (the new chapter files only); every code produced by some test
   case (the markers say which); and a page `docs/diagnostics/WYLnnnn.md` per code whose example
   produces exactly that code, with an allowlist of codes without pages that may only shrink.
   Also check that no catalog method is overloaded.
6. **Docs in the same change**: two rows in `docs/design/architecture.md`'s enforcement table
   (diagnostics come only from the catalog, and the catalog is complete);
   `docs/design/testing.md` and CLAUDE.md ("Writing tests") for the marker syntax; the runner's
   header comment; the "Turn this into a test" lines in `Wyl.Fuzz/Program.cs` and
   `Wyl.Generate/Program.cs`; the style guide's intro sentence about step 3; the index of codes.
7. **Merge** into `dev` as one squashed commit. No area in `.githooks/commit-msg` fits a change
   across every layer (the branch's first commit used `cli` by mistake); propose adding
   `diagnostics` to the user. Before it, run the whole suite `--interpret` and MSVC filtered to
   `errors`.

## The migration tools (`tools/catalog-migration/`)

- `inventory.py out.json`: every reporting call site with its text (also lists `[Error(` attribute
  lines of the catalog; ignore those).
- `swap.py spec`: replaces the call that starts on a given line. Spec lines are
  `@ <path under src/>` then `<line> <CallName>|| <replacement>`; write the spec with the Write
  tool, not a bash heredoc (heredocs with these quotes failed twice). Line numbers come from a
  fresh `inventory.py` run; the tool applies bottom-up. A call written `x.Error(...)` keeps its
  `x.`, so write the replacement without it.
- `dump_diags.py out.json`: runs `wyl check` on every test unit with error markers (`dotnet build`
  first). `baseline.json` is that output from before any message changed; keep it until every
  marker is migrated.
- `migrate.py baseline.json after.json`: rewrites each marker whose diagnostic now has a code,
  pairing diagnostics by position on their line. It reports lines whose diagnostic count changed;
  fix those by hand.

## Wording calls for the user

The style guide doesn't decide these. Each is implemented as recommended; ask before the merge:

1. **Statements for lexical errors**: `string literal is not closed` rather than clang's
   `unterminated string literal`, following the guide's "one statement" rule. Recommended: keep.
2. **One code for every "expected X"** syntax error (and one for the manifest's), rather than a
   code per production. Recommended: keep; a per-production code would number in the dozens and
   say nothing the message doesn't.
3. **A code per declaration kind for "'x' cannot be used on ..."** modifier errors (import, type,
   function, field, constructor, destructor, struct member, enum member, interface method, static
   method), because each kind's rules are in a different spec section. Recommended: keep.
4. **Reserved keywords**: `'goto' is reserved and not implemented yet`. Recommended: keep, or say
   only `'goto' is reserved` if no reserved word is planned.

## When finished

Report as `docs/design/working.md` says (done, decided without approval, open, next). Update issue
#6. Replace this handoff with step 4's: the chapter rewrites, using `spec-mapping.md`.
