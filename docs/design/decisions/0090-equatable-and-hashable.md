# 0090. `Equatable`, `Hashable`, and `Hasher`, and `Comparable` extends `Equatable`

- Status: accepted (2026-11-11)
- Spec: `library.comparable`, `types.primitives.methods`, `generics.bounds`

## Decision

- `std`'s root declares `Equatable<T>`, with `bool equals(in T other)`, and `Hashable<T>`, which
  extends `Equatable<T>` and adds `void hash(ref Hasher hasher)`.
- `Hasher` is a struct of `std`'s root, written in Wylow. A type adds the parts that its `equals`
  compares with `add(long)` and `addBytes(ReadOnlySpan<byte>)`, and `finish()` returns the hash. The
  standard library does not specify the number a hash comes to. The hasher starts from a fixed seed,
  so a program computes the same hashes on every run and in both executors.
- `Comparable<T>` extends `Equatable<T>`. A type that implements `Comparable` returns `true` from
  `equals` exactly when `compareTo` returns zero, and the compiler does not check it.
- Every primitive type and `String` implement `Hashable` of themselves. Their `equals` and `hash`
  are methods with bodies of their extern structs (decision 0078), so the interpreter runs the same
  code as a native build. `float` and `double` define `equals` by `compareTo`'s total order, so
  every NaN equals every other NaN and `-0` does not equal `0`, and their `hash` adds every NaN as
  one NaN.
- `==` stays an error on a value of a type parameter, even one bounded by `Equatable`. Generic code
  calls `equals`.
- None of the three names is one that every file sees. A program imports them from `std`.

## Alternatives

- **Make `==` on a `T extends Equatable<T>` call `equals`.** Generic code would be shorter, but `==`
  would compare identity on a class value and call `equals` on the same value reached through a type
  parameter.
- **A lowercase type property `equatable`**, like `copyable` (decision 0036), meaning that `==`
  applies to the type. A class could then be compared only by identity, and a key type could not
  define its own equality.
- **Java's `int hashCode()`**
  ([`Object.hashCode`](https://docs.oracle.com/en/java/javase/21/docs/api/java.base/java/lang/Object.html#hashCode())).
  Each type would combine its fields' hashes by hand, and the quality of every hash table would
  depend on that arithmetic.
- **A random seed for each process**, as Rust's
  [`RandomState`](https://doc.rust-lang.org/std/collections/hash_map/struct.RandomState.html)
  chooses, against inputs crafted to collide. A program that prints a hash, or iterates a hash map,
  would then print something different on each run, and the test harness could not check it. The
  spec leaves hash values unspecified, so a later decision can add a seed without changing it.
- **Keep `Comparable` apart from `Equatable`**, as Java does. A sorted collection would need both
  bounds, and nothing would tie the two methods together.

## Why

A hash map needs to compare keys and hash them in generic code, and `==` cannot do either on a type
parameter. A type that feeds a hasher leaves the algorithm to the standard library, which Rust's
[`Hash`](https://doc.rust-lang.org/std/hash/trait.Hash.html) and Swift's
[`Hashable.hash(into:)`](https://developer.apple.com/documentation/swift/hashable/hash(into:)) also
do. The compiler can then write a struct's `hash` from its fields without choosing a combining
formula (decision 0091). Rust's [`Ord`](https://doc.rust-lang.org/std/cmp/trait.Ord.html) requires
`Eq`, and Swift's [`Comparable`](https://developer.apple.com/documentation/swift/comparable)
inherits from `Equatable`, so in both languages an ordered type is also an equatable one. A float's
`equals` follows its total order for the reason that Java's
[`Double.equals`](https://docs.oracle.com/en/java/javase/21/docs/api/java.base/java/lang/Double.html#equals(java.lang.Object))
departs from `==`: a NaN key that never equals itself could never be found again.
