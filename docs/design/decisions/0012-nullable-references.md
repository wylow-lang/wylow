# 0012. Nullable references, `ref? T`

- Status: accepted (2026-10-20)
- Spec: `memory.nullable-references`

## Decision

`ref? T` and `in? T` are references that may refer to nothing, used for lookups that can fail
without copying what they find (`list.get(i)`). One can be used as a place only after a check.

## Alternatives

- **A plain reference that may be null**, like what C#'s
  [`CollectionsMarshal.GetValueRefOrNullRef`](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.collectionsmarshal.getvaluerefornullref)
  returns, which the caller must remember to test with `Unsafe.IsNullRef`.
- **Returning a copy as `T?`**: a failed lookup costs nothing extra, but a found element can't be
  changed in place.

## Why

Rust's `Option<&mut T>`, which
[`slice::get_mut`](https://doc.rust-lang.org/std/primitive.slice.html#method.get_mut) returns, is
the model: the type says the reference may be missing, and the compiler makes the caller check
before using it.
