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
| `Iterator<T>` | For-each loops over iterators ([`Iterator`](#library.iterator)) |
| `Borrowable` | Borrows ([`Borrowable`](17-memory.md#memory.borrows.borrowable)) |
| `Printable` | String interpolation ([`Printable`](#library.printable)) |
| `Comparable<T>` | Ordering of the built-in types ([`Comparable`](#library.comparable)) |
| `Result<T, E>` | `try` ([`Result`](18-errors.md#errors.result)) |
| `Weak<T>` | Weak references ([Weak references](17-memory.md#memory.weak)) |
| `Isolated<T>`, `List.isolate` | Isolated object graphs ([`Isolated<T>`](#library.isolated)) |
| `std::panic` | Panics ([`std::panic`](#library.panic)) |
| `std::memory` | Raw memory and the cycle collector ([`std::memory`](#library.memory)) |
| `Thread<T>`, `spawn`, `join` | Threads ([Threads](#library.thread)) |
| `Channel<T>`, `Sender<T>`, `Receiver<T>` | Channels ([Channels](#library.channel)) |
| `Mutex<T>` | Mutexes and the lock rule ([`Mutex<T>`](#library.mutex)) |
| `Atomic<T>`, `AtomicBool` | Atomics ([`Atomic<T>` and `AtomicBool`](#library.atomic)) |
| `Frozen<T>`, `List.freeze` | Frozen lists ([`Frozen<T>`](#library.frozen)) |
| `replace`, `swap`, `take` | Taking a value out of a place ([`replace`, `swap`, and `take`](#library.replace)) |

A program may declare its own type with the name of any other declaration of `std`.

The language calls the methods of some of these interfaces: `Printable`'s `toString` in string
interpolation, `ReadOnlySpannable`'s `asReadOnlySpan` and `Spannable`'s `asSpan` in conversions to
spans and in for-each loops, `Iterator`'s `moveNext` and `current` in for-each loops, and
`Borrowable`'s `beginBorrow` and `endBorrow` around borrows. A type takes part by implementing the
interface. Each interface's section says which types may implement it and whether a program may
call its methods.

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

<a id="library.iterator"></a>
## A.3 `Iterator`

`Iterator` is the interface of a value that produces elements one at a time:

```java unchecked
pub interface Iterator<T> {
    mut bool moveNext();
    T current();
}
```

- `moveNext` advances the iterator to its next element. It returns `true` if the iterator has an
  element there, and `false` if it has no more elements.
- `current` returns the element that the last call of `moveNext` advanced to. What `current` does
  before the first call of `moveNext`, or after `moveNext` returns `false`, is in the documentation
  of each type that implements `Iterator`.
- A for-each loop over an iterator calls `moveNext` before each iteration, and `current` once for
  each iteration that `moveNext` allows
  ([Iterators](14-statements.md#statements.for-each.iterators)).
- A type of the program may implement `Iterator`, to be looped over and passed where an
  `Iterator<T>` bound is required ([Bounds](11-generics.md#generics.bounds)). Its methods are
  ordinary methods, which the language calls in for-each loops and a program may call directly.

<a id="library.printable"></a>
## A.4 `Printable`

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
## A.5 `Comparable`

`Comparable` is the interface of a type whose values are in an order:

```java unchecked
pub interface Comparable<T> {
    int compareTo(in T other);
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
## A.6 `std::panic`

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
## A.7 `std::memory`

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
- `moveOut<T>(T* place)` returns the value at `place`, leaving its storage uninitialized.
- `collectCycles()` frees every cycle that nothing outside refers to. `collectCycles(int maxVisits)`
  visits at most about `maxVisits` objects and leaves the rest for a later call; a negative number
  panics. Both panic when called from a destructor ([Cycles](17-memory.md#memory.cycles)).
- `cycleStats()` returns what the collector did since the last call: the numbers of objects it
  buffered, visited, and freed, as `buffered`, `visited`, and `freed`.

<a id="library.replace"></a>
## A.8 `replace`, `swap`, and `take`

These functions of `std` take a value out of a place that cannot be moved from
([Moves](17-memory.md#memory.moves)):

```java unchecked
pub T replace<T>(ref T place, T value);
pub void swap<T>(ref T a, ref T b);
pub T? take<T>(ref T? place);
```

- `replace` stores `value` in `place` and returns what `place` held.
- `swap` exchanges what `a` and `b` hold. When they are the same place, nothing changes.
- `take` returns what `place` held, and leaves null in it.
- No destructor runs, since nothing is released.
- They panic if a value they would take out is in use, or has one in use among its fields, before
  they change anything ([Holds](17-memory.md#memory.holds)). Their own `ref` arguments do not make a
  value in use.

<a id="library.threading"></a>
## A.9 `std::threading`

The module `std::threading` declares the types through which threads start, communicate, and share
data ([Concurrency](19-concurrency.md#concurrency)).

<a id="library.thread"></a>
### A.9.1 Threads

The module declares the type that a running thread is and the functions that start and join one:

```java unchecked
pub unique struct Thread<T> { ... }
pub Thread<T> spawn<T extends sendable>(escaping shared fn() -> T body);
pub T join(this Thread<T> thread);
```

- `spawn` starts a thread that runs `body`, and returns a `Thread<T>` that refers to it.
- `join` waits for the body to return and returns its result. It takes its receiver by value, so a
  thread is joined at most once ([By-value receivers](07-structs.md#structs.methods.by-value)).
- `join` panics if the thread was never started. The default value of `Thread<T>` is such a thread.
- `join` panics, and releasing a `Thread` panics, if the current thread holds a lock
  ([Mutexes and the lock rule](19-concurrency.md#concurrency.mutex)).
- Releasing a `Thread` without calling `join` waits for the body to return, then releases the
  result. A `Thread` that `main` still holds is therefore joined when it is released
  ([Running a program](03-programs.md#programs.execution)). If the body never returns, the release
  waits forever ([Waiting forever](19-concurrency.md#concurrency.waiting)).
- The result's type must be sendable, since the value leaves the thread
  ([Sendable types](19-concurrency.md#concurrency.sendable)).
- A panic in the body ends the program ([Panics on threads](19-concurrency.md#concurrency.panics)).
- `spawn` panics if the program calls it before `main` begins
  ([Statics](06-variables.md#variables.statics)). `spawn` also panics if the system cannot start
  another thread.

<a id="library.channel"></a>
### A.9.2 Channels

The module declares a channel, through which one thread sends values that another receives ([Interleaving](19-concurrency.md#concurrency.interleaving)):

```java unchecked
pub shared class Channel<T extends sendable> {
    pub Channel();
    pub Sender<T> sender();
    pub Receiver<T> receiver();
}
pub shared class Sender<T extends sendable> {
    pub void send(T value);
}
pub shared class Receiver<T extends sendable> {
    pub T? receive();
}
```

- A channel is unbounded, so `send` never waits. The values it carries are sendable, and a unique
  value moves into it ([Moving and sharing](19-concurrency.md#concurrency.sendable.moving)).
- A channel closes when its last `Sender` is released, and a closed channel stays closed. Calling
  `sender()` on a closed channel panics.
- `receive` waits until a value is queued or the channel closes. After the close, `receive` returns
  the values still queued, in the order they were sent, and then null.
- `receive` panics if the current thread holds a lock
  ([Mutexes and the lock rule](19-concurrency.md#concurrency.mutex)).
- If no `Receiver` is left when the channel closes, the close drops the values still queued. If the
  last `Receiver` is released after the channel closes, the release drops the values still queued.
  The channel drops the values in the order they were sent.
- Releasing every `Receiver` does not close the channel.
- Only a `Sender` keeps a channel open. Releasing the `Channel` object does not close the channel,
  which stays open while a `Sender` lives.
- A channel is not cycle-collected: a value that refers back to a shared object holding the
  channel's end needs a `Weak<T>`, as a cycle among shared objects does
  ([Cycles](17-memory.md#memory.cycles)). A `Sender` that such a cycle holds is never released, so
  its channel never closes ([Waiting forever](19-concurrency.md#concurrency.waiting)).
- `send` panics if something the value owns is borrowed
  ([Moving and sharing](19-concurrency.md#concurrency.sendable.moving)).

<a id="library.isolated"></a>
### A.9.3 `Isolated<T>`

The module declares the box an isolated object graph travels in
([Isolated object graphs](19-concurrency.md#concurrency.isolated)):

```java unchecked
pub unique struct Isolated<T> {
    pub Isolated(shared fn() -> T build);
    pub mut R open<R extends sendable>(shared fn(ref T) -> R body);
    pub T take(this Isolated<T> box);
}
// in List<T>
pub Isolated<List<T>> isolate() where T extends sendable;
```

- The constructor runs `build` immediately on the current thread, and the box holds what `build`
  returns and the graph that result is.
- `open` runs `body` on the value and returns its result. The box must be a mutable place, since
  `body` may change the value.
- What `body` returns is sendable. A lambda that only changes the graph returns nothing, and `R` is
  then `Unit`.
- `take` returns the value and ends the box. The graph's objects then belong to the current thread,
  and its cycle buffer joins the current thread's.
- A box is sendable for any `T`, and a sendable value converts to one by moving in.
- Releasing a box without calling `take` releases the graph on the thread that releases the box.
- `isolate` returns a box that holds a new list of the list's elements, and leaves the list with no
  elements and no capacity. It copies no element.
- `isolate` panics if the list is borrowed.

<a id="library.mutex"></a>
### A.9.4 `Mutex<T>`

The module declares a lock that guards an isolated graph
([Mutexes and the lock rule](19-concurrency.md#concurrency.mutex)):

```java unchecked
pub shared class Mutex<T> {
    pub Mutex(Isolated<T> value);
    pub R lock<R extends sendable>(shared fn(ref T) -> R body);
}
```

- The constructor moves the box's value and graph into the mutex. A sendable value converts to an
  `Isolated<T>`.
- The constructor panics if something the value owns is borrowed
  ([Moving and sharing](19-concurrency.md#concurrency.sendable.moving)).
- `lock` waits until no other thread holds the lock, takes it, runs `body` on the value, releases
  it, and returns what `body` returns.
- What `body` returns is sendable. A lambda that only changes the value returns nothing, and `R` is
  then `Unit`.
- `lock` panics if the current thread already holds a lock, before it waits.
- Releasing the last reference to a mutex releases its graph on the thread that releases it.

<a id="library.atomic"></a>
### A.9.5 `Atomic<T>` and `AtomicBool`

The module declares the values that threads read and change without a lock
([Atomics](19-concurrency.md#concurrency.atomic)):

```java unchecked
pub shared class Atomic<T> {
    pub Atomic(T value);
    pub T get();
    pub void set(T value);
    pub T exchange(T value);
    pub T compareExchange(T expected, T value);
    pub T add(T delta);
    pub T subtract(T delta);
}
pub shared class AtomicBool {
    pub AtomicBool(bool value);
    pub bool get();
    pub void set(bool value);
    pub bool exchange(bool value);
    pub bool compareExchange(bool expected, bool value);
}
```

- `T` is an integer type.
- `get` returns the value, and `set` stores `value`.
- `exchange` stores `value` and returns the value it replaced.
- `compareExchange` returns the value the atomic held, and stores `value` only if that value equals
  `expected`. The program tells whether it stored by comparing the result with `expected`.
- `add` and `subtract` return the value before they change it. They panic on overflow in a debug
  build, before they change the value, and wrap in a release build.
- Every operation is sequentially consistent.

<a id="library.frozen"></a>
### A.9.6 `Frozen<T>`

The module declares a list that never changes, which every thread may read
([Frozen lists](19-concurrency.md#concurrency.frozen)), and `List<T>` declares the method that makes
one:

```java unchecked
pub shared class Frozen<T extends sendable & copyable> implements ReadOnlySpannable<T> {
    pub static Frozen<T> copyOf(ReadOnlySpan<T> items);
    pub int length();
    pub in T operator [](int index);
    pub in? T get(int index);
    pub ReadOnlySpan<T> asReadOnlySpan();
}
// in List<T>
pub Frozen<T> freeze() where T extends sendable & copyable;
```

- `freeze` returns a frozen list that holds the list's elements, and leaves the list with no
  elements and no capacity. It copies no element.
- `freeze` panics if the list is borrowed.
- `copyOf` returns a frozen list that holds a copy of each element `items` views.
- The indexer returns a read-only reference to an element, and panics if `index` is out of bounds.
  `get` returns null instead.
- A frozen list converts to a `ReadOnlySpan<T>`, so a for-each loop iterates its elements
  ([`Spannable` and `ReadOnlySpannable`](#library.spannable)).
- Releasing the last reference to a frozen list releases its elements last to first.
