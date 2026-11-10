# 0077. Type arguments inferred through bounds

- Status: accepted (2026-11-09)
- Spec: `generics.arguments.inference`

## Decision

When a call infers a type for a type parameter, each bound of that parameter determines the type
parameters that the bound mentions. With `<I extends Iterator<T>, T>`, an argument of type
`ListIterator<String>` determines `I`. `ListIterator<String>` implements `Iterator<String>`, so the
bound `Iterator<T>` determines `String` for `T`. The bound determines nothing in two cases: the
arguments give the bounded parameter more than one type, or the bounded parameter's type implements
the bound's interface with more than one list of type arguments. A type parameter that nothing else determines is then
the usual inference error.

## Alternatives

- **Explicit type arguments.** Without this rule, every call of a function that takes an iterator
  through a bound must name both type arguments: `count<ListIterator<String>, String>(words.iterator())`.
  The type of a container's iterator is a detail that a caller should not have to write.
- **A parameter of the interface type**, such as `int count<T>(Iterator<T> source)`. A struct
  iterator does not convert to an interface type (`interfaces.structs`), so the function would
  accept only class iterators, and each call would dispatch.

## Why

Decision 0075 makes generic code take an iterator through a bound, `<I extends Iterator<T>, T>`, so
that a struct iterator is called without dispatch. The element type `T` appears in no parameter, so
inference from the arguments alone could never determine it. The adapters and terminal operations
of the next step of 0075 are extension methods of the same shape, so they need the rule too.
