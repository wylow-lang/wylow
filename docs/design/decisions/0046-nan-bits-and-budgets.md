# 0046. NaN bits and budgeted collection stay implementation-defined

- Status: accepted (2026-11-03)
- Spec: `introduction.implementation-defined`, `memory.cycles`

## Decision

The bits of a NaN that arithmetic produces, and which objects `memory::collectCycles(int maxVisits)` visits, stay implementation-defined.

## Alternatives

- **Remove the budget**: collection becomes fully deterministic, and programs lose bounded pauses.
- **Define which objects a budget visits**: pins and buffering become language rules, so optimizations could not remove them.

## Why

Rust also leaves [NaN bit patterns](https://doc.rust-lang.org/std/primitive.f32.html) non-deterministic. The budget is a known exception to "observable behavior never depends on codegen", kept because its purpose belongs to the open proposal for automatic cycle collection (issue #6), which decides it.
