# 0001. The document set

- Status: accepted (2026-11-01)
- Spec: all

## Decision

Each kind of content has one home:

| Content | Home | Holds |
|---|---|---|
| The language definition | `docs/spec/NN-name.md` | Normative rules only, each stated once, grammar included ([0002](0002-spec-structure-and-conventions.md)) |
| Rationale | `docs/design/decisions/NNNN-slug.md` | One record per decision: the decision, alternatives, why |
| Implementation | `docs/design/` | The compiler (`architecture.md`, `ir.md`, `stack-objects.md`, `testing.md`), and the runtime: object layout, pools, weak blocks, the cycle collector's internals, C++ names (`runtime.md`), and what operations cost (`costs.md`) |
| Diagnostics | `docs/diagnostics/` | The style guide (`README.md`) and one page per code (`WYLnnnn.md`): meaning, an erroneous example, the fix, the spec section ([0003](0003-diagnostics.md)) |
| Standard library behavior | doc comments in its source ([0004](0004-doc-comments.md)) | What each type and function does. The spec covers only the library types the language itself names (`Result`, `Array`, `Span`, `Copyable`, ...) |
| Status and open questions | issue #6 and other GitHub issues | What is implemented, what is planned, what is undecided |
| Learning material | `docs/guide/` (later) | Tutorials and the "behaviors that surprise people" list |

The spec never says what is implemented, what is open, why a rule was chosen, how it is
implemented, or what it costs.

## Alternatives

- **Topic chapters mixing rules, rationale, implementation, and status** (the spec until now).
  Easy to write, but stale status and implementation detail hide the rules, and a rule appears in
  several places that drift apart.
- **Rationale as informative notes in the spec** (the C# spec's "Note" blocks, Go's FAQ inlined).
  Keeps reasons close to rules, but the spec grows with history, and a reversed decision leaves
  its old reasons behind.
- **RFC-style documents written before deciding** (Rust RFCs, Swift Evolution). Suited to a
  community process; with one designer, proposals are discussed in sessions and issues, and only the
  outcome needs keeping.

## Why

The rules are what every reader needs and what tests check against; the reasons are what the next
design session needs; the implementation is what the compiler's maintainers need. Separating them
lets each be complete and correct without the others getting in the way, and lets the spec be
checked rule by rule against the tests. The model is Rust's Reference, RFCs, and error index, and
Swift's book, Evolution proposals, and diagnostic docs, scaled down to one person.

Costs leave the spec because optimizations are invisible by design: what an operation costs
depends on the compiler, not the language, so it is implementation documentation.
