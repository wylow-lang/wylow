# 0081. Overloads may differ only in their bounds

- Status: accepted (2026-11-10)
- Spec: `functions.overloading`, `functions.overloading.resolution`

## Decision

Two top-level functions of one name may have the same parameter types if the bounds of their type
parameters differ. Two methods may not, because a method that overrides or implements another is
matched to it by its parameter types alone.

Overload resolution drops each overload whose type arguments, written or inferred, do not satisfy
its bounds. If more than one overload remains and neither is better by the existing rules, the call
is ambiguous. No overload ranks above another because its bounds are narrower.

The adapters of `std::iteration` (0079) use the rule. Each adapter is declared once for each
iterator interface, for example:

```java unchecked
pub Filter<I, T> filter<I extends Iterator<T>, T>(this I source, escaping fn(T) -> bool test) { ... }
pub RefFilter<I, T> filter<I extends RefIterator<T>, T>(this I source, escaping fn(in T) -> bool test) { ... }
```

A call on a type that implements only one of the interfaces chooses that interface's overload.

## Alternatives

- **Ranking by narrower bounds.** C++20 orders constrained overloads by whether one's constraints
  subsume the other's ([temp.constr.order](https://eel.is/c++draft/temp.constr.order)). The rule
  needs a definition of when one list of bounds implies another, and it makes a call's meaning
  depend on that comparison. A type that implements two of the iterator interfaces is rare, so an
  ambiguity error costs little.
- **Bounds that filter, but no declarations that differ only in bounds**, which is C#'s rule since
  7.3 ([improved overload candidates](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-7.3/improved-overload-candidates)).
  The three versions of each adapter would still collide.
- **A different name for each interface**, such as `filter`, `filterRef`, and `filterIn`. The
  language would not change, but one operation would have three names, and each caller would have to
  know which interface its iterator implements.
- **Adapters for `Iterator<T>` only.** A loop over a filtered `RefIterator` could then not bind its
  elements by `ref`, so the loop could not change the elements that the filter lets through.

## Why

Decision 0076 gives a container up to three kinds of iterator, and 0075 makes the adapters
extensions that take their source through a bound. The bound is the only place where the three
versions differ, so resolution must be able to tell them apart by it. Dropping an overload whose
bounds fail is the same check that a call already makes on its one chosen overload, made before the
choice instead of after it.
