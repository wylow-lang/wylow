# 0075. Iteration: `Iterator<T>` with `moveNext` and `current`, and adapters as extension methods

- Status: accepted (2026-11-09); its `current` signature and its copying container iterators are
  superseded by [0082](0082-mutable-current-and-container-ref-iterators.md) (2026-11-10)
- Spec: `statements.for-each`, `statements.for-each.iterators`, `statements.for-each.indices`,
  `library.iterator`, `memory.borrows.borrowable`, `unsafe.blocks`

## Decision

The standard library declares one interface for iteration:

```java
pub interface Iterator<T> {
    mut bool moveNext();
    T current();
}
```

A for-each loop accepts an iterator as well as the sources it already accepted. The loop calls
`moveNext` before each iteration and `current` once in each iteration. The loop takes the iterator
by value, so a unique iterator moves into the loop, and the loop releases the iterator when the loop
ends. The loop variable is a copy: it is a compile-time error to declare it `ref` or `in` in a loop
over an iterator. The index of a two-variable loop over an iterator is the number of earlier
iterations.

The library builds on the interface as follows. The spec does not state these points, because the
language does not rely on them. They reach the spec as doc comments when the library implements
them.

- **Container iterators are unique structs.** An iterator such as `List<T>`'s holds the container
  and a position. The iterator borrows the container when it is created, and its destructor ends the
  borrow. A program that changes the length of a list while an iterator over the list exists
  therefore panics, as a for-each loop over a list already makes it panic. To make the borrow
  possible, unsafe code may now call `beginBorrow` and `endBorrow`. Safe code still may not. The
  library wraps the two calls in a safe unique struct, so a program's own container can borrow an
  inner container by holding that struct or an inner iterator.
- **Containers name their iterators.** `List<T>` and `Array<T>` have `iterator()`, and a map would
  have methods such as `keys()` and `entries()`. No `Iterable` interface exists. Generic code takes
  an iterator through a bound: `<I extends Iterator<T>, T>`.
- **An iterator returns copies.** `List.iterator()` requires `T extends copyable` through a member
  `where` clause (0052). A program iterates a list of unique values through the list's span with
  `ref` or `in`. A moving iterator, such as a `drain()`, may come later.
- **Lazy adapters and terminal operations are extension methods** on `this I source` where
  `I extends Iterator<T>`. The adapters are `filter`, `map`, `take`, `skip`, and `zip`. Each adapter
  returns a struct that holds its source and an escaping function. The terminal operations include `count`, `any`, `all`, `first`, `sum`, and
  `toList`. No `enumerate` adapter exists, because the two-variable for-each already gives the
  index.
- **Spans and fixed arrays have no iterator.** A `ref struct` cannot be a field or a type argument
  (`structs.ref`), so no adapter can hold a span. A for-each loop iterates spans directly, and their
  terminal operations are extension methods that take a non-escaping function. A lazy chain starts
  from an `Array` or a `List`, which are classes.

## Alternatives

- **`mut T? next()`**, as Rust's
  [`Iterator::next`](https://doc.rust-lang.org/std/iter/trait.Iterator.html#tymethod.next) returns
  an `Option<Item>` and Swift's
  [`IteratorProtocol.next()`](https://developer.apple.com/documentation/swift/iteratorprotocol/next())
  returns an `Element?`. In Wylow, `T?` is the same type as `T` when `T` is already nullable
  (`generics.nullable`). An iterator over a `List<Order?>` would then return `null` both for a null
  element and at the end. A rule that an iterator's element type may not be nullable would leave a
  list of nullable elements with no iterator.
- **Java's `hasNext()` and `next()`**
  ([`java.util.Iterator`](https://docs.oracle.com/en/java/javase/21/docs/api/java.base/java/util/Iterator.html)).
  A `filter` adapter must look ahead to answer `hasNext()`, and must keep the element it found in a
  `T?` field. When `T` is nullable, the adapter cannot tell a stored null element from an empty
  field. The pair of `moveNext` and `current`, as in C#'s
  [`IEnumerator<T>`](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.ienumerator-1),
  needs no adapter to store an element. A `filter` calls its source's `moveNext` until the test
  accepts the source's `current`, and a `map`'s `current` returns the function applied to its
  source's `current`.
- **Interface objects, as in Java and C#.** An `Iterable<T>` whose `iterator()` returns the
  interface type `Iterator<T>` would allocate each iterator on the heap and dispatch every call.
  Wylow has no associated types, so a struct iterator cannot be the result of an interface method.
  C# also has a second, pattern-based path for struct enumerators in `foreach`
  ([the `foreach` statement](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/iteration-statements#the-foreach-statement)),
  which would be two ways to do one thing.
- **Internal iteration**, where the container calls a function for each element, as Kotlin's
  [`forEach`](https://kotlinlang.org/api/core/kotlin-stdlib/kotlin.collections/for-each.html) does.
  It cannot express `zip`, and a `break` or `return` inside the function would need new rules.
- **An iterator that does not borrow its container**, which reads by index and sees every change. A
  hash map's iterator would then skip or repeat entries after a change, without any error. The
  borrow gives the same panic that a for-each loop over a list already gives.

## Why

Before this decision, a for-each loop could iterate only storage that converts to a span. A map, a
tree, or any computed sequence could not be looped over, and no lazy query could be written.

Wylow already makes a struct that implements an interface usable through a bound, with calls made
without dispatch (`interfaces.structs`). Iterators as structs therefore cost no allocation and no
virtual call. That is the model of Swift's
[`IteratorProtocol`](https://developer.apple.com/documentation/swift/iteratorprotocol), with C#'s
method pair instead of an optional result because of Wylow's nullable rule. The borrow reuses
`Borrowable` and destructors on unique structs (0028), so an iterator's lifetime needs no new
analysis. Extension methods were already the plan for members that need a bound (`functions.extensions`),
and they keep the interface small enough that a program's own iterator implements two methods.

The name `iterator()` follows Java's
[`Iterable.iterator()`](https://docs.oracle.com/en/java/javase/21/docs/api/java.base/java/lang/Iterable.html#iterator()),
since Java's conventions win toss-ups in surface syntax.
