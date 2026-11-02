# 0006. Lengths and indices are `int`

- Status: accepted (2026-10-19)
- Spec: `types.primitives.integers`

## Decision

Every length, count, and index in the language and the standard library is an `int`.

## Alternatives

- **An unsigned size type** (C++'s [`size_t`](https://eel.is/c++draft/support.types.layout#3),
  Rust's [`usize`](https://doc.rust-lang.org/std/primitive.usize.html)): can't be negative, and
  covers all of memory on 64-bit targets.
- **`long`**: covers more than 2^31 elements.

## Why

Signed lengths avoid the classic unsigned-underflow bug: `i < length - 1` when `length == 0`. A
single fixed-size type also keeps lengths the same on every target.
