# 0003. Diagnostics: style, codes, and the catalog

- Status: accepted (2026-11-01)
- Spec: none (diagnostics are not part of the language); the style guide is
  `docs/diagnostics/README.md`

## Decision

### Style

Messages follow `docs/diagnostics/README.md`: lowercase, no final period, code in single quotes,
one statement of what is wrong, explanations in `note:` lines and fixes in `help:` lines, each
with its own location where one helps. It is modeled on the Swift and clang conventions, which the
compiler's messages mostly followed already, and on Rust's diagnostic guidelines for notes and help.

### Codes

- **Format**: `WYL` and four digits (`WYL0382`), shown as `error[WYL0382]: ...`.
- **One space, numbered in order, no ranges.** Lexer, parser, manifest, binder, and flow errors,
  and warnings, share it: which part of the compiler finds an error is an implementation detail.
  The first codes are handed out in spec-chapter order, so they start out grouped by topic.
- **Severity is not part of the code**; it is fixed in the code's catalog entry.
- **A code is never reused.** A retired code stays in the catalog, marked retired, and its index
  page says it is no longer emitted.
- **Panics have no codes** for now.

### Enforcement (step 3 of the restructure)

- **The catalog**: `src/Wyl.Compiler.Syntax/Diagnostics/Catalog*.cs`, split by spec chapter. Each
  diagnostic is one typed factory method (`Catalog.UseAfterMove(Location at, string name,
  Location movedAt)`) declaring its code, severity, message template, notes, and spec anchor.
  `Diagnostic`'s constructor is `internal` to the Syntax project, so only the catalog can make one;
  `DiagnosticBag.Error(Location, string)` is deleted, and `IrVerifier.Violation` takes a
  `Diagnostic`.
- **Tests match codes and messages**: `// error[WYL0382]: 'x' is used after being moved`, both
  exact, migrated from today's substring match by a script.
- **A catalog test** checks that codes are unique and none reuses a retired one, that each code has
  a page in `docs/diagnostics/` whose example produces exactly that code, that each spec anchor
  exists, and that each code is produced by some test case. Pages still to be written are listed
  in an allowlist that may only shrink.
- `docs/design/architecture.md`'s enforcement table gets both rules: diagnostics come only from the
  catalog, and the catalog is complete.

## Alternatives

- **No codes** (Java, Go, Zig, Swift until recently). Simple, but nothing stable to search for,
  document, or match in tests when wording improves.
- **Rust's `E0382`**: the same scheme without a prefix, which makes web searches find Rust.
- **Ranges by phase or category** (TypeScript: `TS1xxx` syntax, `TS2xxx` semantics). A range per
  compiler phase exposes the binder/flow split, and a range per topic forces a choice when a
  diagnostic fits several and fills up unevenly. Each page names its spec section, which groups
  codes better than their numbers can.
- **Severity in the code** (`E`/`W`). Promoting a warning to an error would change its code.
- **Matching only codes in tests.** Loses checking the message's arguments (which name, which
  type), where wrong messages hide. Matching only text (until now) can't tell two diagnostics with
  similar wording apart, and breaks when wording improves.
- **The Markdown pages as the source**, generating the catalog with a C# source generator. One
  definition, but more machinery than a cross-check test needs.

## Why

Formal, uniform messages are easier to read and to search, and codes give each diagnostic an
identity that survives rewording: the index explains it, the spec section justifies it, and tests
check it. Declaring each diagnostic once in a catalog the bag requires makes the rest follow from
structure rather than discipline.
