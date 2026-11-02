# 0025. `unsafe` permits operations and never removes checks

- Status: accepted (2026-11-01)
- Spec: `unsafe.blocks`, `unsafe.unchecked`

## Decision

Unsafe code may use pointers, address-of, pointer casts, and unchecked library functions, but code
that would also compile outside it means the same inside it. Skipping a check is a named unsafe
function, such as `getUnchecked(i)`.

## Alternatives

- **An `@Unchecked` annotation or unsafe context that turns off checks** in the code it covers: cut in the trim review.

## Why

Rust works this way: "`unsafe` doesn't turn off the borrow checker or disable any of Rust's other
safety checks" ([the Rust book](https://doc.rust-lang.org/book/ch20-01-unsafe-rust.html)). Reading
code never requires knowing whether it is inside `unsafe` to know what it does, and every unchecked
operation is visible at its call.
