<a id="library"></a>
# Appendix A. The standard library the language names

<a id="library.names"></a>
## A.1 The names

The standard library is the package `std`, compiled with every program. The language relies on
these of its declarations, and on nothing else in it:

| Declaration | What the language uses it for |
|---|---|
| `String` | The type of string literals ([`String`](04-types.md#types.string)) |
| `Array<T>` | The type `T[]` ([Arrays](04-types.md#types.arrays)) |
| `Span<T>`, `ReadOnlySpan<T>` | Span types and their conversions ([Spans](04-types.md#types.spans)) |
| `Spannable<T>`, `ReadOnlySpannable<T>` | Conversions to spans ([`Spannable`](#library.spannable)) |
| `Borrowable` | Borrows ([`Borrowable`](17-memory.md#memory.borrows.borrowable)) |
| `Printable` | String interpolation ([`Printable`](#library.printable)) |
| `Comparable<T>` | Ordering of the built-in types ([`Comparable`](#library.comparable)) |
| `Result<T, E>` | `try` ([`Result`](18-errors.md#errors.result)) |
| `Weak<T>` | Weak references ([Weak references](17-memory.md#memory.weak)) |
| `Isolated<T>` | Isolated object graphs ([Isolated object graphs](19-concurrency.md#concurrency.isolated)) |
| `std::panic` | Panics ([`std::panic`](#library.panic)) |
| `std::memory` | Raw memory and the cycle collector ([`std::memory`](#library.memory)) |

A program may declare its own type with the name of any other declaration of `std`.

The language calls the methods of some of these interfaces: `Printable`'s `toString` in string
interpolation, `ReadOnlySpannable`'s `asReadOnlySpan` and `Spannable`'s `asSpan` in conversions to
spans and in for-each loops, and `Borrowable`'s `beginBorrow` and `endBorrow` around borrows. A
type takes part by implementing the interface. Each interface's section says which types may
implement it and whether a program may call its methods.

<a id="library.spannable"></a>
## A.2 `Spannable` and `ReadOnlySpannable`

These are the interfaces through which a type's elements are viewed as a span:

```java unchecked
pub interface ReadOnlySpannable<T> {
    ReadOnlySpan<T> asReadOnlySpan();
}

pub interface Spannable<T> extends ReadOnlySpannable<T> {
    mut Span<T> asSpan();
}
```

- A value of a type that implements `ReadOnlySpannable<T>` converts implicitly to
  `ReadOnlySpan<T>` by a call of `asReadOnlySpan`, and one that implements `Spannable<T>` converts
  to `Span<T>` by a call of `asSpan` ([Span conversions](05-conversions.md#conversions.spans)).
- A for-each loop over such a value iterates its span
  ([For-each loops](14-statements.md#statements.for-each)).
- `Array<T>` and `List<T>` implement `Spannable<T>`. `Span<T>` implements `ReadOnlySpannable<T>`,
  and a `Span<T>` value is already a span.
- A type of the program may implement either, to be viewed and iterated
  ([Implementing an interface](10-interfaces.md#interfaces.implementing)). Its methods are
  ordinary methods, which the language calls where the interface is used and a program may call
  directly.

<a id="library.printable"></a>
## A.3 `Printable`

`Printable` is the interface of a type whose values string interpolation writes:

```java unchecked
pub interface Printable {
    String toString();
}
```

- Every primitive type and `String` implement `Printable`. A type of the program implements it to
  be written by interpolation.
- Interpolation writes a value of a type that implements `Printable` by calling its `toString`
  ([String interpolation](13-expressions.md#expressions.interpolation)).
- A nullable type does not implement `Printable`, even when its non-nullable type does. Interpolation
  writes a nullable value as `null` or as its value
  ([String interpolation](13-expressions.md#expressions.interpolation)).

<a id="library.comparable"></a>
## A.4 `Comparable`

`Comparable` is the interface of a type whose values are in an order:

```java unchecked
pub interface Comparable<T> {
    int compareTo(T other);
}
```

`compareTo` returns a negative number, zero, or a positive number as this value sorts before, the
same as, or after `other`.

- The numbers, `char`, and `String` implement `Comparable` of themselves. `bool` does not.
- The standard library's `float` and `double` compare in a total order: `-0` sorts before `0`, and
  NaN sorts after every other value, infinity included, and the same as any other NaN. The
  operators order them differently: `-0 == 0`, and NaN is unequal to every value, itself included
  ([Comparison](13-expressions.md#expressions.comparison)).
- A nullable type does not implement `Comparable`.
- A type of the program implements it to be used where a `Comparable<T>` bound is required
  ([Bounds](11-generics.md#generics.bounds)).
- The methods of the built-in types are ordinary methods, found by ordinary lookup, so
  `5.compareTo(3)` calls `int`'s ([Methods of primitive types](04-types.md#types.primitives.methods)).

<a id="library.panic"></a>
## A.5 `std::panic`

The module `std::panic` holds the panics the language names:

- `panic(message)` panics with `message`, which is informative
  ([Panics](18-errors.md#errors.panics)). `std` re-exports `panic`, so `import std::panic;` brings
  in both the module and the function ([Imports](03-programs.md#programs.imports)).
- `checkIndex(index, length)` panics unless 0 <= `index` < `length`.
- `checkRange(start, count, length)` panics if `start` or `count` is negative, or `start + count`
  is greater than `length`.

`checkIndex` and `checkRange` report the location of their `@CallerLocation` caller, as the checks
of a built-in array do ([`@CallerLocation`](22-annotations.md#annotations.caller-location)). They
are safe to call from safe code.

<a id="library.memory"></a>
## A.6 `std::memory`

The module `std::memory` holds raw memory and the cycle collector. Its raw-memory functions are
unsafe, since the compiler cannot check how the storage is used
([Unsafe code](20-unsafe.md#unsafe)):

- `allocate<T>(count)` returns storage for `count` values of `T`, uninitialized. It panics if
  `count` is negative, or `count` values are too large to address.
- `free<T>(T* data)` frees storage that `allocate` returned. The storage must hold no value.
- `initialize<T>(T* place, T value)` stores `value` in uninitialized storage.
- `initializeDefault<T>(T* data, int count)` stores `T`'s default value in each of `count` places
  of uninitialized storage.
- `drop<T>(T* place)` destroys the value at `place`, leaving its storage uninitialized.
- `moveTo<T>(T* from, T* to, int count)` moves `count` values into uninitialized storage. The two
  ranges may overlap.
- `collectCycles()` frees every cycle that nothing outside refers to. `collectCycles(int maxVisits)`
  visits at most about `maxVisits` objects and leaves the rest for a later call; a negative number
  panics. Both panic when called from a destructor ([Cycles](17-memory.md#memory.cycles)).
- `cycleStats()` returns what the collector did since the last call: the numbers of objects it
  buffered, visited, and freed, as `buffered`, `visited`, and `freed`.
