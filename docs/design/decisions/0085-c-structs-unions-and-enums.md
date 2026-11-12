# 0085. Structs and unions in extern blocks are the C++ types, and enums cross only as parameters

- Status: accepted (2026-11-10); [0100](0100-repr-enum-results.md) allows a `@Repr` enum as the return type of an extern function
- Spec: `interop.extern.c-structs`, `interop.blittable`, `interop.blittable.layout`, `unsafe.blocks`,
  `interop.strings`, `interop.buffers`

## Decision

- A struct declared with fields in an extern block is the C++ struct of its name. The generated C++
  uses that type, with the C++ names of its fields. The struct has the memberwise constructor, and
  its default value has every field at its default value. The struct is copyable, and it may declare
  methods with bodies. It declares no constructor, no destructor, and no field initializer.
- An extern block may declare a `union`, which is the C++ union of its name. Writing a field is safe.
  Reading a field is unsafe, and the read gives the value whose bits are the union's bytes at that
  field. The layout check covers unions as it covers structs. `union` has this meaning only as the
  first word of a member of an extern block.
- A `@Repr` enum may be the type of an extern function's parameter. It may not be a return type, the
  type of an extern constant, or the type of a field of a struct or union in an extern block.
- A copyable struct of the program whose fields are all blittable is blittable. C++ sees it as a
  struct with the same fields in the same order.
- `ReadOnlySpan<T>` gains `data()`, as `Span<T>` has. Writing through the pointer that
  `ReadOnlySpan<T>.data()` or `String.cstr()` returns is undefined behavior.

## Alternatives

- **No unions: a byte array and a pointer cast.** C++ libraries often pass a union of event types,
  and the program has to read one of its members. Reading a `byte[N]` through a pointer of another
  type breaks the rule of `unsafe.aliasing`, so the program would have undefined behavior even when
  the bytes are right.
- **Safe reads of a union whose fields accept every bit pattern.** Integers and floating-point
  numbers accept every bit pattern, so a read of such a field cannot produce an invalid value. The
  rule would make whether a read is safe depend on every field's type, and `bool`, `char`, and
  pointers would still need unsafe code. Rust makes every read of a union field unsafe
  ([Unions](https://doc.rust-lang.org/reference/items/unions.html)).
- **Check an enum's value where the value crosses back into Wylow.** A C++ library can return a value
  that the binding does not list, for example a value that a newer version of the library added. A
  check on a returned value is cheap, but a field read from a struct in memory would need the check
  on every read. The program instead reads the integer and casts it, and the cast panics on an
  unknown value ([Integer representations](../../spec/09-enums.md#enums.repr)).
- **Emit a Wylow copy of the struct and convert it at each call.** Each call and each pointer would
  need a conversion, and a pointer into an array of the C++ structs could not be used at all.

## Why

A struct or union in an extern block describes memory that C++ code reads and writes, so the program
uses the C++ type itself, and the layout check proves that the description is right. The generated
C++ compiles a read of a union field as a byte copy. The generated C++ therefore never reads an
inactive member of a C++ union, which C++ leaves undefined.
