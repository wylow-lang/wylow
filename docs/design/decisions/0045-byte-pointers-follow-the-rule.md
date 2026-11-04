# 0045. A `byte*` has no exception to the storage rule

- Status: accepted (2026-11-03)
- Spec: `unsafe.aliasing`

## Decision

Reading through a `byte*` is undefined unless the storage holds a `byte`, as for any other `T*`. `bitcast` reads a value's bits as another type.

## Alternatives

- **`byte*` may read any storage**: C++ exempts `char`, `unsigned char`, and `std::byte` ([\[basic.lval\]/11](https://eel.is/c++draft/basic.lval#11)). Wylow would then have to say what padding bytes read as, and would have two ways to read bits.

## Why

One rule and one way to read bits. Allowing it later breaks no program; taking it back would. The cost is that viewing a value's bytes copies them, through `bitcast` to a byte array of its size.
