# 0036. `copyable` and `sendable` are type properties, not interfaces

- Status: accepted (2026-11-02)
- Spec: `types.copyable`, `generics.bounds`, `concurrency.sendable`, `programs.builtin-names`,
  `library.names`

## Decision

- Whether a type is copyable, and whether it is sendable, follows from the type: its declaration
  (`unique`, `shared class`) and its fields, elements, and type arguments. No declaration states
  either, and there is no `Copyable` interface.
- A bound may name either property, in lowercase: `T extends Comparable<T> & copyable`,
  `Mutex<T extends sendable>`. The words have this meaning only in a bound and are ordinary
  identifiers elsewhere.
- A type parameter has a property only if a bound names it, so an unbounded `T` may be unique, as
  [0009](0009-unique-values-and-moves.md) decided. A type built from type parameters, such as
  `Pair<T, int>`, has a property by the same rules as any other type.
- Function types are copyable: copying a function value copies the reference to the object that
  holds its captures.
- `unique` declares that a type isn't copyable, and `copyable` asks whether it is. There is no way
  to declare a type copyable.

How the standard library's `unsafe` floor declares a type sendable that the field rules don't make
sendable (`concurrency.sendable`) is left open in issue #6.

## Alternatives

- **An interface every type satisfies without implementing it**, which the spec had as `Copyable`.
  Swift does this: `Copyable` is a protocol every type and generic parameter conforms to by
  default, suppressed with `~Copyable`, and the proposal itself notes it "is unlike a protocol in
  some ways" ([SE-0427](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0427-noncopyable-generics.md)).
  A generic type's conformance has to be written as `extension List: Copyable where T: Copyable`.
  `Sendable` is a marker protocol, inferred only for non-public or frozen types
  ([SE-0302](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0302-concurrent-value-and-concurrent-closures.md)).
  In Wylow the name would read as an interface that no one implements, and sendability would need
  a second one.
- **Auto traits**, which Rust uses for `Send` and `Sync`: a trait that the compiler implements for
  a type when all of its fields implement it, unless a negative implementation says otherwise, and
  negative implementations exist only in the standard library
  ([Reference, Auto traits](https://doc.rust-lang.org/reference/special-types-and-traits.html#auto-traits)).
  It has the same problem as an interface: a nominal trait whose implementation is structural.
- **An opt-in marker**, as Rust's `Copy` is, implemented with `derive` or an empty `impl`
  ([`std::marker::Copy`](https://doc.rust-lang.org/std/marker/trait.Copy.html)). Wylow already
  states copyability once, where a type is declared, by the presence or absence of `unique`; a
  second marker would be another way to say it.
- **Copyable by default for a type parameter**, as Swift's generic parameters are. That reverses
  0009, under which a container written once works for unique elements.

## Why

A bound on a property the compiler computes is an existing idea: C#'s `unmanaged` constraint is a
property of a type's fields, written in lowercase, used only as a constraint
([docs](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/unmanaged-types)),
and Go's predeclared `comparable` may be used only as a constraint
([spec, Type constraints](https://go.dev/ref/spec#Type_constraints)). Lowercase shows that the word
is part of the language, not a declaration of the library. Both properties work the same way, so
`Mutex` and channels can ask for `sendable` in the same form that generic code asks for
`copyable`. Each property is one question with one answer, used for bounds and for the concrete
checks (statics, `shared class` fields, captures) alike.
