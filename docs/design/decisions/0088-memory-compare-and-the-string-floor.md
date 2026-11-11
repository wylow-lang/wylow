# 0088. `memory::compare` and `memory::equal`, and the native floor of `String`

- Status: accepted (2026-11-10)
- Spec: `library.memory`, `interop.strings`, `types.string`

## Decision

`std::memory` gains two safe functions over byte spans:

- `compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)` returns -1, 0, or 1. It compares the bytes in
  order as unsigned numbers. If one span is a prefix of the other, the shorter span sorts first.
- `equal(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)` returns whether the two spans have the same
  length and the same bytes.

With these functions, the native floor of `String` from decision 0078 shrinks to four members:
`byteLength`, `cstr`, `toString`, and a package-visible, unsafe `copyOf(ReadOnlySpan<byte>)`. The
caller of `copyOf` guarantees that the bytes are valid UTF-8. `cstr()` is both the floor's view of
the bytes and the interop member of `interop.strings`. Every other method of `String` is written in
Wylow on top of the floor and the two functions. The methods written in Wylow include `compareTo`,
`bytes`, and the new `fromCString`.

## Alternatives

- **A generic `compare<T>`.** Equal bytes do not mean equal values for every type. `-0.0` and `0.0`
  are equal but have different bytes, two NaNs with the same bytes are not equal, and a struct's
  padding bytes hold unspecified values. A bound that admits only the integer types would make a
  generic version safe, and the generic version can be added when such a bound exists.
- **An unsafe `compare(byte* a, byte* b, int count)`.** The span version cannot read out of bounds,
  and it costs the same. Unsafe code that holds pointers builds spans from them.
- **Keep `startsWith`, `endsWith`, and `compareTo` in C++ for speed.** With `equal` and `compare`,
  each of these methods runs one `memcmp`, as the C++ versions do today.
- **A substring search (`memmem`) in the floor.** MSVC's C library does not provide `memmem`, and
  `indexOf` is fast enough as a loop over `equal`. A search can join the floor if profiling shows the
  need.

## Why

`compare` and `equal` are the operations whose speed depends on the C++ runtime, so they belong in
the floor. They take byte spans, so they are safe. They are also general: any code that compares
bytes needs them, not only `String`. `memcmp` is undefined for a null pointer even with a count of
0, and an empty span's pointer may be null, so the runtime returns early for an empty span.
