# 0082. `current` is `mut`, containers give a `RefIterator`, and the adapters' shapes

- Status: accepted (2026-11-10); supersedes 0075's `current` signature and its copying container
  iterators, and 0076's `current` of `InIterator`
- Spec: `library.iterator`, `statements.for-each.iterators`

## Decision

- `current` is `mut` in all three iterator interfaces:

  ```java unchecked
  pub interface Iterator<T> { mut bool moveNext(); mut T current(); }
  pub interface RefIterator<T> { mut bool moveNext(); mut ref T current(); }
  pub interface InIterator<T> { mut bool moveNext(); mut in T current(); }
  ```

- `List<T>.iterator()` and `Array<T>.iterator()` return a `RefIterator<T>` and require no bound on
  `T`. A loop over one binds each element by `ref`, by `in`, or as a copy. A container that may only
  be read, such as `Frozen<T>`, gives an `InIterator`. `Iterator<T>` is for values that an iterator
  computes, such as the results of `map`.
- `filter`, `map`, `take`, and `skip` exist for each of the three interfaces. `map` always gives an
  `Iterator<U>`. The adapters' structs are named after the adapter and the interface:
  `FilterIterator`, `RefFilterIterator`, `InFilterIterator`, `MapIterator`, and so on.
- `zip` takes the other iterator and a function that combines one element of each:
  `a.zip(b, (x, y) -> x + y)`. It is declared for two sources of the same interface. For a
  `RefIterator` or an `InIterator`, the function takes each element by `in`.
- `sum` is declared for each numeric element type on each of the three interfaces and on spans.
- On spans, `count`, `any`, `all`, and `first` take a test, and `sum` and `toList` take nothing. On
  iterators, `count` and `first` take no test, because `filter(test).count()` and
  `filter(test).first()` already say it.

## Alternatives

- **`current` stays read-only in `Iterator` and `InIterator`.** An adapter whose `current` must
  call the `current` of a `RefIterator` source cannot be written, so `map` would exist for two of
  the three interfaces. Rust's
  [`Iterator::next`](https://doc.rust-lang.org/std/iter/trait.Iterator.html#tymethod.next) takes
  `&mut self`, and Swift's
  [`IteratorProtocol.next()`](https://developer.apple.com/documentation/swift/iteratorprotocol/next())
  is `mutating`: both reach each element through a call that may change the iterator.
- **One method for each kind of access**, as Rust's `Vec` has
  [`iter`, `iter_mut`, and `into_iter`](https://doc.rust-lang.org/std/vec/struct.Vec.html#method.iter).
  A program would choose among two or three ways to iterate one container. A `RefIterator` already
  gives a loop the choice of `ref`, `in`, or a copy.
- **Containers keep copying iterators (0075).** A list of unique elements then has no iterator, and
  no adapter chain can start from it.
- **`zip` gives a pair.** `std` has no pair or tuple type. C#'s
  [`Enumerable.Zip(second, resultSelector)`](https://learn.microsoft.com/en-us/dotnet/api/system.linq.enumerable.zip)
  has the combining form, and a form that gives a pair can be added if `std` gains a pair type.
- **The adapters' structs named `Filter`, `Map`, and so on**, as Rust's
  [`std::iter`](https://doc.rust-lang.org/std/iter/index.html#structs) names them. A program that
  imports `std::iteration::*` would then have `Map` in scope, and a map collection could not use
  the name.

## Why

Nothing relied on `current` being read-only. A for-each loop owns the iterator that it loops over,
and generic code that calls `moveNext` already needs a mutable iterator. Making `current` `mut` in
every interface lets each adapter be written for each interface, so a filtered `RefIterator` can
still be mapped.

A container gives the most access that it allows through one method, and the loop or the adapter
chooses how much of it to use. Unique elements can then be looped over and filtered in place, which
the copying iterator of 0075 could not do.
