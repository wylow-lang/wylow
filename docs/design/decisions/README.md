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
| [0005](0005-design-principles.md) | Design principles (superseded by 0027) |
| [0006](0006-lengths-are-int.md) | Lengths and indices are `int` |
| [0007](0007-float-literals.md) | An unsuffixed floating-point literal is a `float` |
| [0008](0008-arithmetic-keeps-operand-types.md) | Arithmetic keeps its operands' type |
| [0009](0009-unique-values-and-moves.md) | Unique structs and enums, and moves |
| [0010](0010-construction-sees-base-class.md) | While a base class constructs, the object is that class |
| [0011](0011-string-value-type.md) | `String` is a built-in value type of UTF-8 bytes |
| [0012](0012-nullable-references.md) | Nullable references, `ref? T` |
| [0013](0013-try-prefix.md) | `try` is a prefix keyword |
| [0014](0014-panicking-and-non-panicking.md) | Panics are for bugs; the library has non-panicking versions |
| [0015](0015-threading-model.md) | Threading model: confinement, one sendable fact, no `static mut` |
| [0016](0016-channels-and-locks.md) | Channels and locks |
| [0017](0017-isolated-graphs.md) | Isolated object graphs |
| [0018](0018-temporaries-are-read-only.md) | Temporary values can't be written |
| [0019](0019-function-values.md) | Non-escaping functions are second-class values |
| [0020](0020-lambda-typing.md) | How lambdas are typed |
| [0021](0021-switch-matches-in-place.md) | A switch matches its value in place |
| [0022](0022-construction-syntax.md) | Construction: `new` only for the heap, one way to build a struct |
| [0023](0023-assignment-is-a-statement.md) | Assignment is a statement |
| [0024](0024-printing.md) | Printing: opt-in `toString`, no cultures |
| [0025](0025-unsafe-permits.md) | `unsafe` permits operations and never removes checks |
| [0026](0026-no-cost-documentation.md) | Costs are not documented |
| [0027](0027-design-principles.md) | Design principles |
| [0028](0028-destructors-on-unique-structs.md) | Destructors on unique structs |
| [0029](0029-statics-are-not-destroyed.md) | Statics are not destroyed at the end of the program |
| [0030](0030-statics-are-initialized-before-main.md) | Statics are initialized before `main`, on one thread |
| [0031](0031-elements-are-released-last-to-first.md) | Elements are released last to first |
