# 0076. Element iterators, and returned references that hold what they refer into

- Status: accepted (2026-11-09); its read-only `current` of `InIterator` is superseded by
  [0082](0082-mutable-current-and-container-ref-iterators.md) (2026-11-10)
- Spec: `functions.ref-returns`, `memory.holds`, `statements.for-each.iterators`, `library.iterator`

## Decision

A method may return a reference to a place **reached through its receiver**: a field or element of
a class object that the receiver refers to, directly or through other objects, or a place that a
method of such an object returned. Before this decision, a returned place had to lie in the
receiver itself. A reference returned this way holds and borrows what the same reference would hold
and borrow if the caller had taken it: the class object that the place is in, or the container
whose method returned the place. The hold or borrow lasts for as long as the caller uses the
reference. A method that returns a reference may now be `virtual` or `abstract`, and an interface
may declare one.

The standard library adds two interfaces beside `Iterator<T>` (0075):

```java
pub interface RefIterator<T> {
    mut bool moveNext();
    mut ref T current();
}

pub interface InIterator<T> {
    mut bool moveNext();
    in T current();
}
```

A for-each loop over a `RefIterator` binds `ref T x`, `in T x`, or a copy `T x`. A loop over an
`InIterator` binds `in T x` or a copy. The reference that the loop variable binds keeps its hold and
borrow until the iteration ends. A type may implement only one of the three iterator interfaces.

The library follows these points, which reach the spec as doc comments when it implements them:

- A container whose element order depends on the elements, such as a map's keys, a set, or a sorted
  tree's keys, gives an `InIterator`, since changing an element in place would break the
  container. A container that may only be read, such as `Frozen<T>`, also gives only an
  `InIterator`.
- `filter`, `take`, and `skip` pass elements through, so the library has a version of each for each
  of the three interfaces. `map` produces new values, so its result is always an `Iterator<U>`.

0075 still holds: an `Iterator<T>` returns copies, and a loop over one cannot bind `ref` or `in`.

## Alternatives

- **Only copies (0075 alone).** A container whose elements are not contiguous, such as a tree, a
  linked structure, or a map that does not store its entries densely, could then not be looped over
  by reference. Unique elements in such a container could not be looped over at all, except through
  a function that the container calls for each element, which cannot `break` or `return` from the
  loop.
- **A compile-time rule for the receiver**, like Rust's rule that a reference returned from a
  `&mut self` method keeps `self` mutably borrowed
  ([the borrowing rules](https://doc.rust-lang.org/book/ch04-02-references-and-borrowing.html)).
  The returned reference would stay a plain pointer, and the compiler would forbid assigning,
  moving, or calling a `mut` method of the receiver while the reference is used. The rule costs
  nothing at run time, but it is a new analysis. It is sound only for a struct receiver, since
  another reference to a class object can assign the object's `mut` field. It also cannot keep a
  list's buffer in place, which needs a borrow.
- **`ref struct` iterators**, as C# lets `foreach` bind `ref` through an enumerator whose `Current`
  returns by reference
  ([the `foreach` statement](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/iteration-statements#the-foreach-statement)),
  and as C# 13 lets a `ref struct` implement an interface and be a type
  argument ([`allows ref struct`](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-13#allows-ref-struct)).
  In Wylow, a `ref struct` cannot be a field or a type argument (`structs.ref`). Adapters and
  generic code over such iterators would need both rules lifted, and a `ref struct` method still
  could not return a place in an object that a field refers to.
- **Generators with `yield`**, such as C#'s
  [`yield return`](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/yield).
  A generator lets a container's author write a recursive traversal without an explicit stack. It
  is a large lowering feature with its own rules for a reference held across a `yield`, and it
  still needs the returned references that this decision allows. It may come later.
- **A single element interface whose `current` returns `in T`**, with `ref` reached some other way.
  Mutating the values of a map or a tree in place is ordinary code, and a separate method for it
  would be a second way to read an element.

## Why

The rule that a returned reference must lie in the receiver exists because the caller receives a
bare pointer. If the pointer were into another object, nothing would keep that object alive, and
nothing would keep a container's storage in place. Every useful iterator refers into another
object: a tree's node, a map's buckets, or a list's buffer.

Wylow already keeps a reference's target alive with holds and borrows (`memory.holds`,
`memory.borrows`). A reference that a function takes into a class object holds the object, and a
reference into a container borrows the container. The decision lets that hold or borrow travel
with the reference out of the method that took it. The rule needs no new analysis, and it is sound
for class receivers and for container storage alike. Its cost is a hold or a borrow for each
element, which is small next to following a tree's pointers. A loop over a span keeps its current
cost.

Once a returned reference carries its own hold, dispatch adds nothing unsafe, so a virtual,
abstract, or interface method may return a reference. `RefIterator` and `InIterator` need this.

`InIterator` is needed where elements cannot be copied, or are costly to copy, and the container
must forbid changing them. A map with unique keys is the case that requires it. The pair mirrors
`Span<T>` and `ReadOnlySpan<T>`.
