# 0091. The compiler writes `equals` and `hash` for a struct or enum

- Status: accepted (2026-11-11)
- Spec: `library.equatable`, `interfaces.implementing`

## Decision

- A struct or enum that implements `Equatable` of itself, directly or through `Comparable` or
  `Hashable`, and declares no `equals`, gets an `equals` that the compiler writes from its fields.
  One that implements `Hashable` of itself and declares neither `equals` nor `hash` gets both.
- The written `equals` calls each field's `equals`. For an enum, both values are the same variant,
  and each field of the variant equals the other's. The written `hash` gives the hasher each field
  through the field's `hash`, and an enum's first adds the variant's index.
- Each field's type must implement the interface of itself (`WYL0490`). A field of a type parameter
  qualifies when the parameter's bounds require the interface.
- A struct or enum that implements `Hashable` writes both methods or neither (`WYL0491`).
- The compiler never writes a class's methods.
- The binder writes the methods as syntax, at the type's name in the program's own file, and binds
  them as it binds a method the program wrote.

## Alternatives

- **No written methods.** Every key type would write `equals` and `hash` by hand, and the two could
  drift apart from each other and from the fields.
- **Write the methods only on request**, with an annotation, as Rust's
  [`#[derive(PartialEq, Eq, Hash)]`](https://doc.rust-lang.org/reference/attributes/derive.html)
  does. Declaring the interface already says that the type wants it, and an annotation would be a
  second way to say so.
- **Forbid a struct or enum to write its own `equals`.** Its equality would always be the written
  one, but a key that ignores case or a cached field could not be expressed.
- **Write the methods for a class too.** A class's `==` compares identity, and comparing its fields
  could follow a cycle of objects without end.
- **Conditional conformance**, where `Pair<A, B>` implements `Hashable` only when `A` and `B` do, as
  Swift's [conditional conformances](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0143-conditional-conformances.md)
  allow. It is a language feature with its own design, and it is left for a later decision. Until
  then, a generic struct requires the interface in its type parameters' bounds.
- **Write the bodies in lowering, as IR.** Lowering would need its own form of every check that the
  binder already makes for a written method, such as the bounds of a type parameter, `in` arguments,
  and the patterns of a variant.

## Why

A struct or enum is a value whose identity is its fields, so equality from the fields is what most
keys need, and Swift writes the same methods for a struct or enum that declares the protocol
([SE-0185](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0185-synthesize-equatable-hashable.md)).
If the type wrote only one of the two methods, the written one could disagree with it, so the
compiler writes both or neither. Writing the methods as syntax gives them every check and every
lowering a method of the program gets, so no later layer knows that the compiler wrote them.

The generated `hash` adds to a local copy of the hasher and assigns the copy back at the end. The
verifier cannot tell that a `ref` parameter never refers into a field whose type is a type
parameter, but it knows that a local of the function never does.
