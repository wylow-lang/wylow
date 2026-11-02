# 5. Concurrency

Data races are undefined behavior in C++, so safe Wylow must make them impossible. Deadlocks
between locks are ruled out too, by a cheap runtime check. The language itself adds only a few
rules: which values may reach another thread (§5.1–5.4), what statics may hold (§5.5), and that a
panic on any thread stops the program (§5.7). Threads, channels, locks, atomics, and frozen arrays
are the standard library's, built on `unsafe` code at its lowest layer; §5.6 describes how they
behave. Thread pools, parallel loops, and task systems are libraries built on the same rules.

## 5.1 Thread confinement

**A normal class object belongs to the thread that created it**, and no other thread can ever
reach it. This is what lets reference counts and borrow counters be non-atomic, and the pools
([03-memory-model.md](03-memory-model.md#allocation)) and the cycle collector's buffer belong to
one thread with no synchronization.

A value reaches another thread only in three ways: captured by a function that thread runs
(§5.4), sent over a channel (§5.6), or read from a static (§5.5). Each accepts only sendable values
(§5.2). Code itself is harmless: a function running on another thread may call any function,
create and release objects, and use any type, because the objects it makes stay on its thread and
nothing it can reach is another thread's.

## 5.2 Sendable types

A type is **sendable** if a value of it can safely be used from several threads at once. The
compiler infers this; it is never written.

| Type | Sendable |
|---|---|
| Primitives | ✅ |
| Structs, enums, and `T[N]` whose fields or elements are all sendable | ✅ |
| `String` (an immutable value whose copies share one block, counted atomically) | ✅ |
| References to a `shared class` (§5.3), including the standard library's `Frozen<T>`, `Mutex<T>`, `Atomic<T>`, and channel ends | ✅ |
| Escaping `shared` functions (§5.4) | ✅ |
| `Weak<T>` where `T` is a shared class | ✅ |
| References to normal classes (`List<T>`, `T[]`, user classes), `Weak<T>` of a normal class, and functions that aren't `shared` | ❌ |
| Structs, enums, and fixed arrays with any field or element that isn't sendable | ❌ |

Wherever a sendable type is required, using another is a compile error naming the field that
isn't sendable.

Nothing reachable through a sendable value can change except through a synchronization type: a
copy of a struct is a separate value, a `String` never changes, and a shared object's fields
aren't `mut`. So one fact covers both sending a value to a thread and sharing it between threads
(Rust needs `Send` and `Sync` for the two).

## 5.3 `shared class`

A `shared class` may be referenced from several threads:

```java
shared class Weapon {
    pub String name;
    pub float damage;
    pub virtual float damageAt(float distance) { return damage; }
}
```

- Its reference count is **atomic**, and its objects use the global heap rather than a thread's
  pools, since the last reference may be released on any thread, which runs the destructor.
- No field may be `mut`, and every field must be sendable. State that changes is held in a
  synchronization type (`Mutex<T>`, `Atomic<T>`, a channel end), which is itself a shared class.
- Its base class, if any, and every class extending it are shared too.
- Its objects are never cycle-collected ([03-memory-model.md](03-memory-model.md#cycles-memorycollectcycles)),
  since other threads may refer to them. Their fields can't change, so a cycle among them can only
  pass through a synchronization type, such as a `Mutex` holding a reference back. Break it with a
  `Weak<T>`, which is sendable when `T` is a shared class, or it leaks:

  ```java
  shared class Room {
      pub Mutex<Weak<Room>> previous = new Mutex<Weak<Room>>(null);   // doesn't keep it alive
      pub Mutex<Room?> next = new Mutex<Room?>(null);
  }
  ```

A shared class is for objects that many threads read and that need a class's identity or virtual
methods: definitions of weapons, materials, or sounds. A struct whose fields are references to
synchronization types is already sendable, and needs no class.

> *Open:* whether an interface can require its implementations to be shared classes, so that a
> reference to it is sendable.

## 5.4 Shared functions

`shared` on a function type means its lambda may run on another thread. It is part of the type,
like `escaping` ([06-syntax.md](06-syntax.md#escaping-and-non-escaping)), and is written wherever a
function type is: `shared fn(Span<float>) -> void`, `escaping shared fn() -> R`,
`Channel<shared fn() -> void>`.

| | Escaping `shared fn` | Non-escaping `shared fn` |
|---|---|---|
| Runs | On another thread, possibly after the call that made it returns | On other threads while the function given it waits for them |
| Captures | Copies, which must be sendable | Read-only: sendable locals and parameters, and `in` references and `ReadOnlySpan`s of sendable data |
| Is itself | A shared object with an atomic count, so the function value is sendable | A second-class value, like any non-escaping function |
| Typical use | A thread's body, a task in a queue | A body that a parallel loop splits across threads |

- A `shared` lambda can't assign what it captures, call a `mut` method on it, or pass it by `ref`,
  even when it is non-escaping: other threads may be reading it. It may capture `this` only in a
  shared class's method.
- **Data a non-escaping shared function may change is passed to it as arguments**: the function
  that calls it hands each thread its own part, typically a `Span<T>` of sendable elements. That
  function, not the language, keeps the parts apart (§5.6, Parallel work).
- A non-escaping `shared fn` handed to another thread must have finished before the function it
  was given to returns, since what it captures lives in the caller's frame. Only `unsafe` code can
  hand one to a thread, and it takes on that duty, as in Rust's scoped threads.
- A `shared fn` converts to the function type without `shared`, as an escaping one converts to the
  non-escaping form; nothing converts the other way.

## 5.5 Statics

**Every static is read-only, and its type must be sendable.** There is no `static mut`: one copy
shared by every thread would race, and a copy per thread silently gives each thread its own
version. This differs from Java.

```java
class Audio {
    static int SAMPLE_RATE = 48_000;
    static Atomic<int> VOICES_PLAYING = new Atomic<int>(0);   // changes through atomic operations
    static Mutex<Stats> STATS = new Mutex<Stats>(Stats());     // changes under its lock
    static Frozen<float> SINE_TABLE = makeSineTable();        // computed once, read by every thread
}
```

- A static of a type that isn't sendable, such as `static List<int> X`, is a compile error that
  suggests `Frozen<T>` or a `Mutex<T>`.
- State that one thread changes, such as the game's world or the editor's selection, belongs in an
  object the program passes to what uses it.
- A static is initialized the first time it is read, as in Java, exactly once even if threads read
  it at the same time. One whose initializer reads it again, directly or through the functions it
  calls, panics. A static initialized with a constant expression is constant data, and reading it
  costs nothing.

## 5.6 The standard library

The rules above are all that safety needs; this section describes how the standard library's
threading types behave, not their full API, which belongs to the library.

A program with threads may produce output in a different order from run to run, as threads are
scheduled. That is the only way threads make a program's behavior vary: everything a thread does
by itself is as deterministic as single-threaded code.

### Threads

```java
Thread<Image> loading = Thread.spawn(() -> decodePng(readFile(path)));   // escaping shared fn() -> Image
// ... keep running ...
Image image = loading.join();
```

- `Thread.spawn` takes an `escaping shared fn() -> R`, where `R` is sendable, and runs it on a new
  thread. `join()` waits for it and returns its result; `isFinished` tells whether it has finished.
- A `Thread<R>` is a normal object that belongs to the thread that spawned it. Releasing one that
  hasn't been joined joins it (like C++'s `std::jthread`), so a thread never outlives the object
  that started it unnoticed.
- When a thread ends, the cycle collector frees every cycle left in its buffer, so its objects'
  destructors run before `join` returns.

### Channels

A channel carries values from one or more threads to others. It has two ends, a `Sender<T>` and a
`Receiver<T>`, both shared objects that can be copied to any number of threads; `T` must be
sendable. `Channel<T>()` makes a struct holding the two, `sender` and `receiver`.

```java
var requests = Channel<Path>();
var loaded = Channel<Mesh>();
Receiver<Path> pending = requests.receiver;
Sender<Mesh> done = loaded.sender;

var loader = Thread.spawn(() -> {
    for (Path path : pending) {                  // ends once every Sender<Path> is gone and the channel is drained
        done.send(loadMesh(path));
    }
});

requests.sender.send(Path("models/tree.obj"));
while (loaded.receiver.tryReceive() is Mesh mesh) {   // each frame; never waits
    upload(mesh);
}
```

- **A channel closes when its last `Sender` is released.** There is nothing to close by hand, and
  nothing can be sent to a closed channel.
- `receive()` waits for a value and returns `T?`: null once the channel is closed and empty.
  `tryReceive()` never waits, returning null if nothing is there yet. A for-each loop over a
  receiver receives until the channel is closed and empty.
- Channels have no size limit, so `send` never waits. It returns `false`, dropping the value, once
  every `Receiver` is gone.
- This is Go's model with two of its complaints removed: closing by hand (in Go, sending on a
  closed channel or closing one twice panics) and channels that make every send wait for a receiver
  by default, which deadlock easily.

> *Open:* channels with a limited capacity, for a producer that must wait for a slow consumer, and
> Go's `select` (waiting on several channels at once). Neither is needed yet, and `select`'s random
> choice among ready channels would make runs differ.

### Locks and atomics

`Mutex<T>` gives one thread at a time access to a `T`, through a `ref` that can't escape the lock
([03-memory-model.md](03-memory-model.md#33-references)). `T` must be sendable:

```java
STATS.lock((ref Stats s) -> { s.framesMixed++; });
```

> *Open:* a mutex holding data that isn't sendable, such as a `List` or a map that threads add to.
> Code holding the lock could copy a reference to one of its objects out to its own thread, or
> store an object from its own thread in it, and the cycle collector of one thread could visit
> objects inside it while another holds the lock. Allowing it needs a rule for what the lock's
> lambda may capture and return, an allocator that accepts frees from other threads, and keeping
> the collector away from those objects. Until then, a growable shared collection is a library type
> built on `unsafe`, or a `Frozen<T>` that a thread replaces under a lock.

**A thread holds at most one lock, and waits for nothing while holding it.** Taking a second lock,
or calling `receive`, `join`, or anything else that waits, inside a lock's lambda panics, naming
both places. Every deadlock between locks needs a thread holding one lock while waiting for
another, or waiting for another thread while holding one; this rule makes those impossible, and
the check runs every time, so the mistake fails in the first test instead of freezing the program
once in a while. Data that must change together belongs behind one mutex.

`Atomic<T>` holds a primitive and changes it with atomic operations (`load`, `store`, `add`,
`compareExchange`, ...), all sequentially consistent.

What the rules don't prevent: two threads each waiting to receive from the other wait forever, as
in any language. Like an infinite loop, that is a bug the language doesn't catch.

> *Open:* taking two locks at once in a fixed order (`Mutex.lockBoth`), if a real program needs it.

### Frozen arrays

`Frozen<T>` is an immutable array that every thread may read: a shared object whose elements, of
a sendable type `T`, never change. It reads like a `ReadOnlySpan<T>` (`length`, an indexer, and
`view()`, which returns one).

- `list.freeze()` moves a `List<T>`'s elements into a new `Frozen<T>`, without copying them, and
  leaves the list empty. Like `clear()`, it panics if the list is borrowed.
- `Frozen.copyOf(span)` copies the elements a span views, such as an array's, which can't be
  emptied.
- To change one, copy it into a new list.

Freezing doesn't depend on how many references the list has, so it does the same thing whether or
not the optimizer has removed some count changes.

### Parallel work

Running a loop's iterations or a set of tasks on several threads is a library's job, built on
non-escaping `shared fn`. A parallel function hands each thread the data it may change as an
argument, checks that no part it may change overlaps any other part or anything the lambdas
capture (and panics if one does), and waits until every thread has finished:

```java
parallel::forEach(particles.asSpan(), 1024, (Span<Particle> part) -> {
    for (ref Particle p : part) p.integrate(dt);           // 'dt' is captured, read-only
});
```

The standard library provides such a function; how many there are and what they look like belongs
to it, not to the language.

## 5.7 Panics

A panic on any thread **aborts the process**, after printing its message and stack trace
([01-safety.md](01-safety.md#13-panics)). No thread continues after another panics, so nothing can
observe state that a panicking thread left half-changed, and locks need no poisoning.

## 5.8 Unsafe

Inside `unsafe`, raw pointers may be shared between threads without checks. Races in unsafe code
are undefined behavior and the programmer's responsibility. The standard library's threading
types are built this way, at its lowest layer, and present only the safe behavior above.

## 5.9 Runtime

- Each thread has its own pools and its own cycle collector buffer; a thread a foreign library
  created gets them the first time it runs Wylow code ([07-interop.md](07-interop.md#callbacks)).
- `String`'s count is always atomic. A program that never starts a thread could use non-atomic
  counts instead, which no program can observe; that is an optimization for later.
- The threads themselves are the operating system's.

> Not implemented yet: everything in this chapter. Statics, too, are not implemented.
