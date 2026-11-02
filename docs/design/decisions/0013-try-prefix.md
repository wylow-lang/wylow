# 0013. `try` is a prefix keyword

- Status: accepted (2026-10-19)
- Spec: `expressions.try`

## Decision

`try expr` returns early with the error of a `Result`, or gives its value.

## Alternatives

- **A postfix `?`** (Rust).

## Why

`?` already means "nullable" in Wylow.
