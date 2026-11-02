# 0026. Costs are not documented

- Status: accepted (2026-11-01)
- Spec: none

## Decision

What operations cost is documented nowhere: not in the spec, and not in a document of its own. The
`costs.md` that [0001](0001-document-set.md) listed among the implementation documents is dropped.
How the runtime implements something, which is what a cost follows from, stays in `runtime.md`.

## Alternatives

- **A cost table** (`docs/design/costs.md`, from the old spec's "What the checks cost"): a second
  description of the implementation, which goes stale whenever an optimization changes, and which
  nothing checks.

## Why

Optimizations are invisible by design, so a cost describes the current compiler, not the language,
and `runtime.md` already says how each thing is done. A separate list of costs wasn't useful enough
to keep up to date.
