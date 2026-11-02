# 0010. While a base class constructs, the object is that class

- Status: accepted (2026-10-20)
- Spec: `classes.construction`

## Decision

While a base class's constructor or destructor runs, virtual calls dispatch to the base class's
implementations, as in C++
([\[class.cdtor\]/4](https://eel.is/c++draft/class.cdtor#4)). Until every field that needs a value is assigned, a constructor can use
`this` only to assign and read its fields.

## Alternatives

- **Dispatch to the derived class during construction**: a virtual call can observe the derived
  class's fields before they are assigned (Java: [JLS 12.5](https://docs.oracle.com/javase/specs/jls/se27/html/jls-12.html#jls-12.5),
  example 12.5-2; Kotlin:
  [derived class initialization order](https://kotlinlang.org/docs/inheritance.html#derived-class-initialization-order)).
- **Two-phase initialization**: a derived class assigns its fields *before* calling `super`
  (Swift's
  [initialization](https://github.com/swiftlang/swift-book/blob/main/TSPL.docc/LanguageGuide/Initialization.md),
  "Two-Phase Initialization").

## Why

It is the simplest sound rule: each class can only observe its own fields (fully assigned before
`this` escapes) and its base's (completely constructed).
