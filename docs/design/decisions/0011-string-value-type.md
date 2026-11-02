# 0011. `String` is a built-in value type of UTF-8 bytes

- Status: accepted (2026-10-24)
- Spec: `types.string`

## Decision

`String` is an immutable, reference-counted value type whose copies share one block of UTF-8
bytes. Offsets and lengths count bytes, and the method names say so (`byteLength`, `charCount`).
Strings are joined by interpolation; there is no `+` on strings.

## Alternatives

- **A class**: a non-null class reference has no default, and a `String`'s default is the empty
  string.
- **A user struct**: structs own no resource, and a `String` owns its block.
- **Offsets in characters or UTF-16 units** (Java, C#): finding and slicing would not be O(1) per
  step.
- **`+` for concatenation**: a second way to do what interpolation does.

## Why

Byte offsets make finding and slicing O(1) per step, and naming the unit means no `length` can be
mistaken for a character count. One way to join strings keeps code uniform, and interpolation
builds the result in one allocation.
