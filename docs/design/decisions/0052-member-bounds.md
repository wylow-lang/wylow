# 0052. A member may bound its type's type parameters

- Status: accepted (2026-11-04); decides the open `Array` and `List.first`/`last` question of
  [0049](0049-getting-a-unique-value-out.md)
- Spec: `generics.bounds`, `functions.methods`, `structs.constructors`, `classes.construction`,
  `interfaces.implementing`, `types.arrays`

## Decision

- A method or constructor may add a `where` clause after its parameter list:
  `where T extends Bound & Bound`, with clauses for several parameters separated by commas.
- It names a type parameter of the type the member belongs to, and only those. A method's own type
  parameters keep their bounds where they are declared.
- Inside the member the parameter has the bounds: `copyable` makes its values copies, and an
  interface among them provides its methods.
- At every use, the type's type arguments are checked against the clause; one that does not
  satisfy it is a compile-time error at the use.
- It is a compile-time error on a free function, on a method of an interface, on a method that is
  `virtual`, `override`, or `abstract`, and on a method that implements an interface method: a call
  through the interface or a base class could not check the bound.
- A clause is not part of a member's signature, so two members of one name and parameter types
  conflict whatever their clauses.
- The word `where` is contextual, as `as` is: it means a clause only between a parameter list and
  a body, and a name everywhere else.
- `Array`'s value constructor uses it: `pub Array(int length, T value) where T extends copyable`
  copies `value` for each element. `List.first` and `last` return `ref? T` instead, as `List.get`
  does, since a reference needs no copy and so works for every element type.

## Alternatives

- **A std free function**, `Array<U> copies<U extends copyable>(int length, U value)`. It avoids a
  language rule, but a program must `import std;` to make an array of class elements, and the
  language's own array construction moves into a library name.
- **A static method on `Array` with its own type parameter**, `Array<T>.copies<U extends copyable>`.
  The class's type argument is unrelated to `U`, so `Array<int>.copies(2, "x")` is accepted and
  returns `Array<String>`; a type argument that can lie is worse than an import.
- **No fill constructor.** `new Account[3]` is already an error because a class reference has no
  default value, so this would remove arrays of class elements entirely.
- **A reserved `where` keyword.** Simpler for the parser, but it takes the identifier away for no
  benefit: nothing else may follow a parameter list but a body, a `;`, or this clause.
- **Swift's member `where` clauses.** Swift puts a `where` clause on a member or extension that
  constrains the type's parameters (Swift book, [Where
  Clauses](https://docs.swift.org/swift-book/documentation/the-swift-programming-language/generics/#Where-Clauses)),
  which is the form chosen here.
- **Rust's bounded `impl` blocks.** Rust gives a generic type's methods for some type arguments
  only with `impl<T: Copy> Foo<T>` (Rust Reference, [Implementations](https://doc.rust-lang.org/reference/items/implementations.html)).
  That groups members; Wylow has no block-level declaration to hang a bound on, and a per-member
  clause says what it needs where a reader looks.
- **C# constraints.** A C# method's `where` clause can name only the method's own type parameters,
  not the class's ([Constraints on type
  parameters](https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/generics/constraints-on-type-parameters)),
  so this shape cannot be written there; such a member takes the value by reference instead.

## Why

One rule does the work, and it is the rule generic code already follows: the declaration's bounds
decide what a value is, so a member that needs more of its type's parameter says so and the caller's
type arguments are checked against it. The text then means the same thing for every type argument,
and the check sits where the copy happens, at the use. It reuses the bound syntax and the type
property words, so it adds a position rather than a mechanism, and it keeps `new Array<T>(n, value)`
in the language without a std name or an import.
