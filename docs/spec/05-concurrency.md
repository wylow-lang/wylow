# 5. Concurrency

Data races are undefined behavior in C++, so safe Wylow must make them impossible. Deadlocks
between locks are ruled out too, by a cheap runtime check. The language itself adds only a few
rules: which values may reach another thread (§5.1–5.4), what statics may hold (§5.5), and that a
panic on any thread stops the program (§5.7). Threads, channels, locks, atomics, and frozen arrays
are the standard library's, built on `unsafe` code at its lowest layer; §5.6 describes how they
behave. Thread pools, parallel loops, and task systems are libraries built on the same rules.

## 5.1 Thread confinement

**A normal class object belongs to one thread at a time**, and no other thread can reach it
meanwhile. This is what lets reference counts and borrow counters be non-atomic, and the pools
([03-memory-model.md](03-memory-model.md#allocation)) and the cycle collector's buffer belong to
one thread with no synchronization. An object changes threads only inside a unique value that
moves: a whole isolated object graph (§5.6, Isolated object graphs), or what a unique value of
the standard library owns (§5.2).

A value reaches another thread only in a few ways: captured by a function that thread runs
(§5.4), read from a static (§5.5), or moved through the standard library's threading types: sent
over a channel, stored in a mutex, given to a new thread or returned from it (§5.6). Each accepts
only sendable values (§5.2). Code itself is harmless: a function running on another thread may
call any function, create and release objects, and use any type, because the objects it makes stay
on its thread and nothing it can reach is another thread's.

## 5.2 Sendable types

A type is **sendable** if no part of a value of it belongs to one thread, so the value may be used
on another. The compiler infers this; it is never written.

| Type | Sendable |
|---|---|
| Primitives | ✅ |
| Structs, enums, and `T[N]` whose fields or elements are all sendable, unique ones included | ✅ |
| `String` (an immutable value whose copies share one block, counted atomically) | ✅ |
| References to a `shared class` (§5.3), including the standard library's `Frozen<T>`, `Mutex<T>`, `Atomic<T>`, and channel ends | ✅ |
| Escaping `shared` functions (§5.4) | ✅ |
| `Weak<T>` where `T` is a shared class | ✅ |
| `Isolated<T>`, for any `T` (§5.6), and the standard library's owning unique structs (below) when their elements are sendable | ✅ |
| References to normal classes (`List<T>`, `T[]`, user classes), `Weak<T>` of a normal class, and functions that aren't `shared` | ❌ |
| Structs, enums, and fixed arrays with any field or element that isn't sendable | ❌ |

Wherever a sendable type is required, using another is a compile error naming the field that
isn't sendable.

**Moving and sharing.** A sendable value reaches another thread in one of two ways, and which one
follows from whether it can be copied ([02-types.md](02-types.md#unique-structs)):

- A copyable sendable value is **shared**: its copies may be used by several threads at once.
  Nothing reachable through it can change except through a synchronization type: a copy of a
  struct is a separate value, a `String` never changes, and a shared object's fields aren't
  `mut`. Statics, `shared class` fields, and what a `shared fn` captures are read by several
  threads, so they must be sendable **and copyable**.
- A unique sendable value is **moved**: it has one owner, so after it moves, only the receiving
  thread has it. A unique value may change when it is only read, as a borrow counter in what it
  owns does, which is safe because only one thread holds it. Channels, mutexes, and a thread's
  argument and result move values, so they take unique values too.
- **Moving a value to another thread panics if anything it owns is borrowed**
  ([03-memory-model.md](03-memory-model.md#borrow-counters-on-containers)): sending it over a
  channel, storing it in a mutex, or giving it to a new thread. A loop or reference on this thread
  could still be using that storage, as when a loop's body replaces the field it is looping over
  and sends the old value. Borrows are never removed by optimization, so the check gives the same
  answer in every build, as `freeze()`'s does. Moving within a thread needs no check: what the
  loop refers into stays alive, and the new owner can't change its length while it is borrowed.

Rust needs two inferred facts for these, `Send` and `Sync`; Wylow has one, and the difference is
whether the type can be copied.

A unique struct of the standard library that owns class objects, such as storage for its
elements, is not sendable by the rules above; the library's `unsafe` floor may declare it sendable
when its elements are, as Rust's `unsafe impl Send` does, since only its own code could let those
objects out.

> *Open:* how that declaration is written.

> *Proposed, not decided:* such a struct is a growable array that is a unique value (`Vec<T>`,
> [02-types.md](02-types.md#unique-structs)), decided with the standard library.

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
- No field may be `mut`, and every field must be sendable and copyable (§5.2). State that changes is held in a
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
| Captures | Copies, which must be sendable and copyable | Read-only: sendable, copyable locals and parameters, and `in` references and `ReadOnlySpan`s of such data |
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
- **What a shared function returns is isolated when its arguments are sendable.** It can reach no
  normal object that existed before the call: what it captures is sendable, so are its arguments,
  and there are no `mut` statics. So every normal object reachable from its result was made during
  the call, and once the call returns, nothing but the result refers to them. `Isolated<T>` and
  `Thread.spawn` rely on this (§5.6).

## 5.5 Statics

**Every static is read-only, and its type must be sendable and copyable** (§5.2). There is no `static mut`: one copy
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

- `Thread.spawn` takes an `escaping shared fn() -> R` and runs it on a new thread. `join()` waits
  for it and returns its result, which moves to the joining thread; `isFinished` tells whether it
  has finished. `R` may be any type, a graph of normal objects included, since a shared function's
  result is isolated (§5.4): a loader thread can return a whole level.
- A unique value the thread should own, such as a buffer to fill, is moved to it as an argument:
  `Thread.spawn(buffer, (SampleBuffer b) -> ...)`. A shared lambda can't capture it, since captures
  are copies (§5.4).
- A `Thread<R>` is a normal object that belongs to the thread that spawned it. Releasing one that
  hasn't been joined joins it (like C++'s `std::jthread`), so a thread never outlives the object
  that started it unnoticed.
- When a thread ends, the cycle collector frees every cycle left in its buffer, so its objects'
  destructors run before `join` returns.

### Channels

A channel carries values from one or more threads to others. It has two ends, a `Sender<T>` and a
`Receiver<T>`, both shared objects that can be copied to any number of threads; `T` must be
sendable, and may be unique, in which case `send` moves the value. `Channel<T>()` makes a struct
holding the two, `sender` and `receiver`.

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
([03-memory-model.md](03-memory-model.md#33-references)). `T` may be any type: a mutex holds an
isolated value (below), and `lock` opens it while holding the lock. So the lock's lambda is a
non-escaping `shared fn(ref T) -> R`, and `R` must be sendable:

```java
STATS.lock((ref Stats s) -> { s.framesMixed++; });
pending.lock(q -> q.add(Request(path, priority)));          // a Mutex<List<Request>>
int waiting = pending.lock(q -> q.count());
```

The lambda's captures are sendable and copyable, so it can't store one of its thread's objects in
the protected data, and its result is sendable, so it can't hand one of the protected objects out.
To put a value of the thread's in, copy what is needed into sendable locals and capture those, or
move in a unique value through the mutex's methods. A mutex is made from a value that is already
isolated: a sendable value, or an `Isolated<T>`.

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

### Isolated object graphs

`Isolated<T>` holds a `T` and everything reachable from it (an **isolated graph**) such that nothing
outside refers to any normal object in it. It is a unique struct
([02-types.md](02-types.md#unique-structs)), so it can't be copied, and it is sendable for any `T`:
moving it moves the whole graph to whoever receives it, and leaves nothing behind that could reach
those objects.

```java
// on a loading thread: built from sendable inputs only ('levelBytes' is a Frozen<byte>)
Isolated<NavGraph> built = Isolated<NavGraph>(() -> NavGraph.build(levelBytes));
toAi.send(built);                                     // moves the box and the graph

// on the AI thread
mut Isolated<NavGraph> nav = fromLoader.receive()!;
nav.open(g -> g.addEdge(from, to, cost));            // changes it in place, still isolated
int nodes = nav.open(g -> g.nodeCount());
NavGraph graph = nav.take();                          // ends the box; the graph is this thread's now
```

- **Making one** runs a non-escaping `shared fn() -> T` at once, on the current thread. Its result
  is isolated (§5.4). A sendable value is isolated already, and converts to `Isolated<T>` by
  being moved in.
- **`open`** runs a non-escaping `shared fn(ref T) -> R` on the value, where `R` is sendable, and
  returns its result. The lambda's captures are sendable and copyable, so nothing of the
  thread's can get in, and its result is sendable, so nothing inside can get out; the graph is
  still isolated afterwards. `open` needs a mutable place, since it changes what the box holds.
- **`take`** moves the value out and ends the box: the graph joins the current thread's objects.
- A `Mutex<T>` holds one, and `lock` is `open` while holding the lock.

This is Pony's `recover` block, which needs nothing new in Wylow: without `mut` statics, and with
shared functions limited to sendable captures, a function can only reach what it was given.

**The cycle collector.** An isolated graph has its own cycle buffer. While the lambda of
`Isolated`'s constructor or `open` (or a mutex's `lock`) runs, the thread's buffer is the graph's;
that code can reach nothing of the thread's, so everything it buffers belongs to the graph or was
made during the call. `memory::collectCycles()` inside it collects the graph only, and the buffer
moves with the graph. `take` adds the graph's buffer to the thread's. Opening a graph costs two
pointer writes for the switch; a graph of acyclic objects, such as a `List<int>`, never buffers
anything.

What it can't do: code inside `open` can't read the thread's own objects. For data built from the
thread's objects, such as the draw calls of a frame, the proposed unique growable array (§5.2) would
be filled with ordinary code and then moved.

> *Considered, not decided:* Swift's region analysis, which proves that an ordinary local's graph
> is disconnected from everything else by flow analysis, so a `List` filled with ordinary code
> could be sent without `Isolated`. It needs a new analysis with an exactly specified rule for
> when regions merge, and code that mixes the thread's objects with the graph's makes the cycle
> buffer impossible to split cheaply. It could be added later as a conversion from such a local
> to `Isolated<T>`.

### Frozen arrays

`Frozen<T>` is an immutable array that every thread may read: a shared object whose elements, of
a sendable, copyable type `T` (§5.2), never change. It reads like a `ReadOnlySpan<T>` (`length`, an indexer, and
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
  An object freed on another thread than the one whose pool it came from goes back through that
  chunk's remote free list ([03-memory-model.md](03-memory-model.md#allocation)).
- `String`'s count is always atomic. A program that never starts a thread could use non-atomic
  counts instead, which no program can observe; that is an optimization for later.
- The threads themselves are the operating system's.

> Not implemented yet: everything in this chapter. Statics, too, are not implemented.
