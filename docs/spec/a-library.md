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
| `std::iteration`: `Iterator<T>`, `RefIterator<T>`, `InIterator<T>` | For-each loops over iterators ([`std::iteration`](#library.iterator)) |
| `Borrowable` | Borrows ([`Borrowable`](17-memory.md#memory.borrows.borrowable)) |
| `Printable` | String interpolation ([`Printable`](#library.printable)) |
| `Comparable<T>` | Ordering of the built-in types ([`Comparable`](#library.comparable)) |
| `Equatable<T>`, `Hashable<T>`, `Hasher` | The equality and hashing that the compiler writes for a struct or enum ([`Equatable` and `Hashable`](#library.equatable)) |
| `Result<T, E>` | `try` ([`Result`](18-errors.md#errors.result)) |
| `Weak<T>` | Weak references ([Weak references](17-memory.md#memory.weak)) |
| `Isolated<T>`, `collections::List.isolate` | Isolated object graphs ([`Isolated<T>`](#library.isolated)) |
| `std::panic` | Panics ([`std::panic`](#library.panic)) |
| `std::memory` | Raw memory and the cycle collector, and `ElementOwner<T>` and `ElementVisitor<T>`, through which the collector visits values in raw memory ([`std::memory`](#library.memory)) |
| `Thread<T>`, `spawn`, `join` | Threads ([Threads](#library.thread)) |
| `Channel<T>`, `Sender<T>`, `Receiver<T>` | Channels ([Channels](#library.channel)) |
| `Mutex<T>` | Mutexes and the lock rule ([`Mutex<T>`](#library.mutex)) |
| `Atomic<T>`, `AtomicBool` | Atomics ([`Atomic<T>` and `AtomicBool`](#library.atomic)) |
| `Frozen<T>`, `collections::List.freeze` | Frozen lists ([`Frozen<T>`](#library.frozen)) |
| `replace`, `swap`, `take` | Taking a value out of a place ([`replace`, `swap`, and `take`](#library.replace)) |

A program may declare its own type with the name of any other declaration of `std`.

The language calls the methods of some of these interfaces: `Printable`'s `toString` in string
interpolation, `ReadOnlySpannable`'s `asReadOnlySpan` and `Spannable`'s `asSpan` in conversions to
spans and in for-each loops, the iterators' `moveNext` and `current` in for-each loops, and
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
## A.3 `std::iteration`

The module `std::iteration` declares the interfaces of a value that produces elements one at a
time:

```java unchecked
pub interface Iterator<T> {
    mut bool moveNext();
    mut T current();
}

pub interface RefIterator<T> {
    mut bool moveNext();
    mut ref T current();
}

pub interface InIterator<T> {
    mut bool moveNext();
    mut in T current();
}
```

- An `Iterator<T>` produces values. A `RefIterator<T>` produces references to elements that the
  loop may change, and an `InIterator<T>` produces read-only references to elements.
- `moveNext` advances the iterator to its next element. It returns `true` if the iterator has an
  element there, and `false` if it has no more elements.
- `current` returns the element that the last call of `moveNext` advanced to. What `current` does
  before the first call of `moveNext`, or after `moveNext` returns `false`, is in the documentation
  of each type that implements the interface.
- A reference that `current` returns is a returned reference
  ([Returning references](12-functions.md#functions.ref-returns)). If the element is reached
  through the iterator, the reference holds and borrows what the element is in for as long as the
  caller uses it.
- A for-each loop over an iterator calls `moveNext` before each iteration, and `current` once for
  each iteration that `moveNext` allows
  ([Iterators](14-statements.md#statements.for-each.iterators)).
- A type of the program may implement any of the three, to be looped over and passed where a bound
  on one of them is required ([Bounds](11-generics.md#generics.bounds)). Its methods are ordinary
  methods, which the language calls in for-each loops and a program may call directly.
- The module also declares extension methods on the three interfaces and on `ReadOnlySpan<T>`
  ([Extension methods](12-functions.md#functions.extensions)). The language does not call them. Their
  doc comments state what each one does.

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
pub interface Comparable<T> extends Equatable<T> {
    int compareTo(in T other);
}
```

`compareTo` returns a negative number, zero, or a positive number as this value sorts before, the
same as, or after `other`. `Comparable<T>` extends `Equatable<T>`
([`Equatable` and `Hashable`](#library.equatable)), so a type that implements `Comparable` also has
`bool equals(in T other)`. A type that implements `Comparable` returns `true` from `equals` exactly
when `compareTo` returns zero. The compiler does not check that the two methods agree.

- The numbers, `char`, and `String` implement `Comparable` of themselves. `bool` does not.
- Every primitive type and `String` implement `Hashable` of themselves, which extends `Equatable`.
  A `float` or `double` is equal to another by `equals` when `compareTo` returns zero, so every NaN
  equals every other NaN, and `-0` does not equal `0`.
- The standard library's `float` and `double` compare in a total order: `-0` sorts before `0`, and
  NaN sorts after every other value, infinity included, and the same as any other NaN. The
  operators order them differently: `-0 == 0`, and NaN is unequal to every value, itself included
  ([Comparison](13-expressions.md#expressions.comparison)).
- A nullable type does not implement `Comparable`.
- A type of the program implements it to be used where a `Comparable<T>` bound is required
  ([Bounds](11-generics.md#generics.bounds)).
- The methods of the built-in types are ordinary methods, found by ordinary lookup, so
  `5.compareTo(3)` calls `int`'s ([Methods of primitive types](04-types.md#types.primitives.methods)).

<a id="library.equatable"></a>
## A.6 `Equatable` and `Hashable`

`Equatable` is the interface of a type whose values compare for equality, and `Hashable` is the
interface of a type whose values also hash:

```java unchecked
pub interface Equatable<T> {
    bool equals(in T other);
}

pub interface Hashable<T> extends Equatable<T> {
    void hash(ref Hasher hasher);
}
```

- `equals` returns whether this value equals `other`. A type that implements `Equatable` makes
  `equals` an equivalence: every value equals itself, `a.equals(b)` is `b.equals(a)`, and two values
  that equal a third equal each other.
- `hash` adds the parts of the value that `equals` compares to `hasher`, a value of the standard
  library's struct `Hasher`. Two values that `equals` finds equal give a hasher the same input. The
  numbers that a hasher computes are not specified.
- Every primitive type and `String` implement `Hashable` of themselves
  ([`Comparable`](#library.comparable)). A nullable type implements neither interface.
- `==` does not apply to a value of a type parameter, even one whose bound is `Equatable`. Generic
  code calls `equals` ([Comparison](13-expressions.md#expressions.comparison)).

A struct or enum that implements `Equatable` of itself, directly or through an interface that
extends it, such as `Comparable` or `Hashable`, and that declares no method named `equals`, has an
`equals` that the compiler writes. If it implements `Hashable` of itself and declares neither
`equals` nor `hash`, it has a `hash` that the compiler writes as well:

- The written `equals` of a struct returns `true` when the `equals` of each field returns `true`
  for the field of the same name of `other`. The written `equals` of an enum returns `true` when
  both values are the same variant and each field of the variant equals the field of `other`.
- The written `hash` of a struct gives the hasher each field in declaration order, through the
  field's own `hash`. The written `hash` of an enum first adds the index of the variant, counting
  from 0, and then gives the hasher the variant's fields in order.
- The written methods are `pub`, and they are ordinary methods of the type.
- It is a compile-time error if the type of a field does not implement the interface of itself, as
  a nullable type, a fixed array, a function type, and a type parameter whose bounds do not require
  the interface do not.
- It is a compile-time error if a struct or enum that implements `Hashable` of itself declares
  exactly one of `equals` and `hash`.
- The compiler never writes the methods of a class. A class that implements either interface
  declares its methods.

**Example:**

```java
import std::Hashable;

struct Point implements Hashable<Point> {
    int x;
    int y;
}

struct Label implements Hashable<Label> {
    String? text;                       // error[WYL0490]
}

void main() {
    std::print(Point(1, 2).equals(Point(1, 2)));
}
```

<a id="library.panic"></a>
## A.7 `std::panic`

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
## A.8 `std::memory`

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

A class that keeps values in raw memory reports them to the cycle collector through an unsafe
interface ([Unsafe interfaces](10-interfaces.md#interfaces.unsafe)):

```java unchecked
pub unsafe interface ElementOwner<T> {
    void visitElements(ref ElementVisitor<T> visitor);
}

pub unique extern struct ElementVisitor<T> {
    pub mut extern void visit(ref T element);
}
```

- The collector calls `visitElements` of each object it visits, after it visits the object's
  fields, and the collector looks at what each value given to `visit` holds
  ([What the collector looks at](17-memory.md#memory.cycles.collected)).
- A class that implements `ElementOwner` promises that `visitElements` gives `visit` each value
  that the class keeps in raw memory exactly once, and nothing else. The method changes no value,
  creates or releases no reference, does not panic, and does not call `collectCycles`. It is
  undefined behavior to break the promise.
- It is a compile-time error for a struct or enum to implement `ElementOwner`.
- A class implements `ElementOwner` of one type at most. A class that keeps values of several types
  keeps them in one struct type.
- Only the collector makes an `ElementVisitor` that reports anything. A default value reports
  nothing.
- `List<T>`, `Array<T>`, and `Frozen<T>` implement `ElementOwner<T>`.

Two functions of `std::memory` compare bytes. They are safe, since a span carries its length:

- `compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)` returns -1, 0, or 1 as `a` sorts before,
  the same as, or after `b`. The two spans are compared byte by byte, each byte as an unsigned
  number. If one span is a prefix of the other, the shorter span sorts first.
- `equal(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)` returns whether `a` and `b` have the same
  length and the same bytes.

<a id="library.replace"></a>
## A.9 `replace`, `swap`, and `take`

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
## A.10 `std::threading`

The module `std::threading` declares the types through which threads start, communicate, and share
data ([Concurrency](19-concurrency.md#concurrency)).

<a id="library.thread"></a>
### A.10.1 Threads

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
### A.10.2 Channels

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
### A.10.3 `Isolated<T>`

The module declares the box an isolated object graph travels in
([Isolated object graphs](19-concurrency.md#concurrency.isolated)):

```java unchecked
pub unique struct Isolated<T> {
    pub Isolated(shared fn() -> T build);
    pub mut R open<R extends sendable>(shared fn(ref T) -> R body);
    pub T take(this Isolated<T> box);
}
// in std::collections::List<T>
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
### A.10.4 `Mutex<T>`

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
### A.10.5 `Atomic<T>` and `AtomicBool`

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
### A.10.6 `Frozen<T>`

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
// in std::collections::List<T>
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
