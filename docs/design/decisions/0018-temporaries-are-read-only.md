# 0018. Temporary values can't be written

- Status: accepted (2026-11-01)
- Spec: `structs.methods.mut`

## Decision

A temporary struct or fixed array is a value, not a variable: its `mut` methods can't be called,
its fields and elements can't be assigned, and it can't be passed by `ref` or referred to.

## Alternatives

- **Allowing the write** (C#, C++, and Rust allow some of these): it changes a copy no one sees.

## Why

What the write changed would be gone after the statement. Swift and Go have the same rule.
