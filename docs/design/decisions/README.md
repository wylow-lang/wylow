# Decision records

One file per decided design question: what was decided, what else was considered, and why. The
spec (`docs/spec/`) states only the rules; the reasons live here, so a rule can be read without its
history and a reason can be found without searching the spec.

- **Name**: `NNNN-short-slug.md`, numbered in order. A number is never reused.
- **When**: a design decision gets a record when it is made. A question still open is a GitHub
  issue (or an entry in the roadmap, issue #6), not a record; it becomes a record once decided.
- **Changes**: once accepted, a record's text doesn't change, except its status. A decision that
  replaces it is a new record, and the old one's status becomes "superseded by NNNN".
- **Form**:

```markdown
# NNNN. Title

- Status: accepted (YYYY-MM-DD) | superseded by NNNN (YYYY-MM-DD)
- Spec: the sections it decides, by anchor (`memory.moves`)

## Decision

## Alternatives

## Why
```

## Records

| # | Decision |
|---|---|
| [0001](0001-document-set.md) | The document set: spec, decision records, implementation docs, diagnostics index, guide |
| [0002](0002-spec-structure-and-conventions.md) | The spec's outline, anchors, grammar notation, and fixed phrasing |
| [0003](0003-diagnostics.md) | Diagnostics: style, codes, and the catalog that enforces them |
| [0004](0004-doc-comments.md) | Doc comments: `///` Markdown, Javadoc style, fixed sections |
