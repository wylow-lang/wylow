# 0078. Methods with bodies in extern structs, and `String`'s methods written in Wylow

- Status: accepted (2026-11-10)
- Spec: `interop.extern.extern-structs`

## Decision

A method of an extern struct is extern or has a body written in Wylow. An extern method still calls
the C++ member function of its name. A method with a body is compiled as a method of any struct:
its receiver is the value it is called on, and it may call the extern methods.

`String` keeps a small native floor in C++: its length in bytes, a read-only view of its bytes,
and the operations whose speed depends on the C++ runtime. Its other methods, such as `startsWith`,
`contains`, `indexOf`, `slice`, and `charCount`, move into `string.wyl` as methods with bodies
built on that floor. The same rule applies to the primitive types' extern structs
(`primitives.wyl`), which may gain methods with bodies.

## Alternatives

- **Extension methods**, which the roadmap had planned. An extension is found only through an import
  (`functions.extensions`), so every file that calls `s.startsWith(x)` would need
  `import std::startsWith;`. Decision 0049 already rejected finding an extension without an import
  when the extension is declared in its type's file, because the call would not show why one
  extension needs an import and another does not. Kotlin declares `String.startsWith` as an
  extension in [`kotlin.text`](https://kotlinlang.org/api/core/kotlin-stdlib/kotlin.text/starts-with.html),
  and Kotlin imports that package into every file by default
  ([default imports](https://kotlinlang.org/docs/packages.html#default-imports)).
- **Extensions in the names every file sees.** The prelude would grow with every method `String`
  gains, and the user leans towards no prelude at all.
- **Keeping every method in C++.** Each method of `String` is then C++ that neither the binder nor
  the IR verifier checks, and the interpreter needs its own copy of each method.

## Why

A method of `String` is part of `String`, so a program should call it without an import, as it calls
a method of `List`. Swift writes most of `String` in Swift on top of a small set of built-in
operations ([`String.swift`](https://github.com/swiftlang/swift/blob/main/stdlib/public/core/String.swift)).
Wylow can do the same once an extern struct may declare a method with a body. Each method that moves
out of the runtime is checked like any other Wylow code and runs in the interpreter without a
second implementation.
