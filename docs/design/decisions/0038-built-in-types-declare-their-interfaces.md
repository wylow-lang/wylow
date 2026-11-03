# 0038. The built-in types declare their interfaces in the standard library

- Status: accepted (2026-11-02)
- Spec: `generics.bounds`, `library.printable`, `library.comparable`, `types.primitives.methods`

## Decision

- Each primitive type except `void` has an `extern struct` of its name in `std`
  (`Stdlib/primitives.wyl`), as `String` already had one (`string.wyl`). The declaration gives the
  type members and interfaces and nothing else: `int` still names the built-in type.
- Every primitive type and `String` implements `Printable`; the numbers, `char`, and `String`
  implement `Comparable` of themselves. `bool` is not `Comparable`.
- Whether a type satisfies a bound is one check, whether the type implements the interface, with
  no exception for built-in types. So a nullable built-in type satisfies no bound, as no other
  nullable type does; `int?` was `Printable` before. Interpolation still writes a nullable value.
- A primitive's `toString` and `compareTo` are ordinary methods found by ordinary lookup, so a wrong
  call reports what any method's would (`no overload of 'int.compareTo' accepts (long)`). WYL0198,
  which only these methods produced, is retired.

## Alternatives

- **Keep the conformances in the compiler**, as five special cases did (`StdlibSymbols`, two
  made-up builtin symbols, the binder's call binding, the emitter, and the interpreter). Each new
  built-in conformance, such as `Equatable` (issue #14), would add another in each layer.
- **Keep `int?` printable through a bound.** The spec already said that no nullable type satisfies a
  bound (old `02-types.md`, Bounds), while its interpolation section said built-in nullables do;
  `Animal?` was rejected but `int?` accepted. Making it a rule for every nullable would need
  `toString` on null, which the language doesn't call on a null of any other type.

## Why

The conformance of a built-in type is a fact a library can state, so the library states it, in the
same form as for any other type. Rust declares the primitives' trait implementations in its
standard library (`impl Ord for i32`, [i32 docs](https://doc.rust-lang.org/std/primitive.i32.html)),
and Swift's integers are structs of its standard library that conform to `Comparable` through
their declared protocols
([IntegerTypes.swift.gyb](https://github.com/swiftlang/swift/blob/main/stdlib/public/core/IntegerTypes.swift.gyb)).
