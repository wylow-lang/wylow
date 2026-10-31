# 5. Concurrency

Data races are undefined behavior in C++, so safe Wylow must make them impossible. The model has
three parts:

1. **Thread confinement:** normal objects belong to one thread.
2. **Jobs:** structured, short-lived parallelism over plain data (similar to Unity's Job System).
3. **Threads:** long-lived workers that communicate only by sending values.

## 5.1 Thread confinement

**A normal class object belongs to the thread that created it.** It can never be accessed from
another thread. This is what allows normal refcounts and borrow counters to be non-atomic.

### `Sendable`

A type is **sendable** if values of it can safely cross threads. The compiler infers this; it is
never written on structs.

| Type | Sendable |
|---|---|
| Primitives | ✅ |
| `Handle<T>` | ✅ |
| Enums whose variants hold only sendable data | ✅ |
| Structs whose fields are all sendable ("plain data") | ✅ |
| `T[N]` where `T` is sendable | ✅ |
| `String` (an immutable built-in value whose copies share one counted block) | ✅ |
| References to a `shared class` (including `Frozen<T>`) | ✅ |
| References to normal classes (`List<T>`, `T[]`, `Map`, user classes), and `Weak<T>` | ❌ |
| Structs/enums containing any non-sendable field | ❌ |

Wherever a sendable type is required, using a non-sendable one is a compile error naming the
offending field.

### `shared class`

A `shared class` may be referenced from multiple threads.

```java
shared class TextureCache {
    public {
        String rootPath;
        Mutex<Map<String, TextureId>> entries;
        Atomic<int> hits;
    }
}
```

- Uses an **atomic** reference count.
- No field may be `mut` ([02-types.md](02-types.md#27-mutability-and-constants)), and every field
  must be sendable or a synchronization type (`Mutex<T>`, `RwLock<T>`, `Atomic<T>`,
  `Channel<T>`, ...), through which the shared state changes. The compiler enforces this.
- `String` is an immutable built-in value: its bytes are shared and never change, so it may cross
  threads. Its block's count becomes atomic when threads exist.

## 5.2 Jobs

Jobs are for short, parallel work within a frame: physics, animation, culling, skinning.

Jobs are **structured**: the call that launches them does not return until every job has finished,
and the calling thread helps execute them. Because the caller outlives the jobs, jobs may safely use
`ref`s and `Span`s borrowed by the caller.

```java
// Parallel for: the span is split into disjoint chunks
Jobs.parallelFor(particles, 1024, (Span<Particle> chunk) -> {
    for (ref Particle p : chunk) p.integrate(dt);
});

// Several independent tasks
Jobs.run(
    () -> integrate(bodies, dt),
    () -> animate(skeletons, poses, dt),
);
```

### Rules

1. **Captures:** a job may capture only sendable values, `Span<T>`, `ReadOnlySpan<T>`, and `ref T`,
   where `T` is itself sendable.
2. **No overlapping mutable access:** within one launch, no `Span<T>` or `ref T` may overlap any other
   span or ref given to another job. `parallelFor` guarantees this by construction. For `Jobs.run`,
   the runtime compares the memory ranges at launch and panics on overlap. `ReadOnlySpan`s may
   overlap each other freely.
3. The launching thread holds all borrows (pins and borrow counters) for the duration of the
   launch. Jobs never touch containers or refcounts, only raw views.

### Design consequence

A struct containing a class reference (for example `struct Enemy { public { Behavior ai; } }`) is not
sendable, so it cannot be processed by jobs. Data that needs parallel processing should be
**plain data**, with class-heavy logic kept on the owning thread. This is intentional: it pushes
hot data toward a data-oriented layout.

## 5.3 Threads

Threads are for long-running background work: asset streaming, audio, networking.

```java
var requests = new Channel<String>();
var results  = new Channel<LoadedMesh>();

Thread.spawn(() -> {
    for (String path : requests) {
        results.send(loadMeshData(path));
    }
});

// main thread, each frame
while (results.tryReceive() is LoadedMesh m) {
    uploadToGpu(m);
}
```

- A thread's closure may capture **only sendable values**. (No refs or spans, since a thread can
  outlive its creator.)
- Threads communicate through sendable values, typically over a `Channel<T>`.
- A thread is an isolate (§5.6): a panic ends the thread, and joining it returns `Err(Panic)`.

### Freezing

Data built on one thread (for example vertex arrays) is usually in normal, non-sendable containers.
`freeze()` converts it to an immutable, shared form without copying:

```java
float[] verts = new float[n];
// ... fill ...
Frozen<float[]> data = verts.freeze();
```

- `freeze()` panics unless the caller holds the **only** reference (refcount is 1). After freezing,
  nobody can mutate the data, so it is safe to share.
- Only available when the element type is sendable.
- Available on `T[]` and `List<T>`. `Frozen<T>` is a `shared class` offering read-only access
  (including `ReadOnlySpan<T>`).
- What isn't copied is the elements: `freeze()` makes a new `Frozen<T>` object that takes over the
  container's element storage, and the emptied container stays on its thread. The container itself
  came from its thread's pools ([03-memory-model.md](03-memory-model.md#allocation)), which only that
  thread may free into, while the element storage is on the global heap, which any thread may free.

## 5.4 Synchronization types

All are `shared class`es in the standard library. Their type parameters must be sendable.

| Type | Purpose |
|---|---|
| `Mutex<T>` | Exclusive access to a `T` |
| `RwLock<T>` | Many readers or one writer |
| `Atomic<T>` | Atomic operations on a primitive |
| `Channel<T>` | Multi-producer, multi-consumer message queue |
| `ConcurrentQueue<T>` | Lock-free queue |

Locks give access through a scoped `ref`:

```java
stats.lock((ref Stats s) -> { s.frames++; });
```

Because `ref` cannot escape (see [03-memory-model.md](03-memory-model.md#34-references)), protected
data cannot be accessed after the lock is released.
A lock held while a panic unwinds is poisoned (§5.6).

## 5.5 Statics

> ⚠️ **Mutable statics are thread-local.** This differs from Java. More precisely, each isolate has
> its own copy, and each thread is an isolate (§5.6).

| Declaration | Meaning |
|---|---|
| `static mut int frameCount;` | **One copy per thread.** No races possible |
| `static int MAX = 64;` / `const` | Shared constant |
| `static shared Atomic<int> totalFrames;` | One process-wide copy. Type must be sendable, mutation only through sync types |

A job that calls a function which updates a mutable static updates the *worker thread's* copy.

## 5.6 Isolates and panics

A panic stops the **isolate** it happens in ([01-safety.md](01-safety.md#13-panics)). An isolate is
a set of objects and statics that nothing outside it can refer to. Each thread is one, and an
`Isolate` is one that runs inside a call on the current thread: a way to run code that may panic,
such as a mod, a script, or a game session in an editor, without stopping the program.

```java
class ModHost {
    public {
        void tick(FrameInput input) {                                   // FrameInput: plain data
            switch (mod.call(state -> state.update(input))) {           // Result<ModOutput, Panic>
                case Ok(ModOutput out) -> apply(out);
                case Err(Panic p) -> {
                    log(p.message);
                    mod = Isolate.start(() -> new ModState(config));    // start over
                }
            }
        }
    }
    private {
        mut Isolate<ModState> mod = Isolate.start(() -> new ModState(config));   // config: sendable
    }
}
```

### How it works

`Isolate.call` is an ordinary call on the current thread; nothing is scheduled or switched. What
makes it an isolate are the rules for what crosses its boundary, the same as for a thread:

- **In:** `start`'s and `call`'s lambdas may capture only sendable values, and `ReadOnlySpan<T>`s
  of sendable `T`, which the isolate can read but not change. Nothing the isolate does can change
  the caller's objects.
- **Out:** `call` returns a sendable value, so no object created inside escapes.
- **State:** `start` creates the state, an object of class `S`, inside the isolate. Only `call`'s
  lambda receives it, and only for the call.

So when code inside panics, the compiler has already ruled out two things: anything outside being
half-changed, since the isolate couldn't change it, and anything outside referring to the
isolate's half-changed objects, since none could leave it. The runtime needs no record of which
objects belong to which isolate.

### Using an isolate

| Member | Does |
|---|---|
| `Isolate.start(() -> S)` | Creates an isolate whose state is the lambda's result, made inside it |
| `call((S) -> R)` | Runs the lambda on the state: `Ok(result)`, or `Err(Panic)` if it panicked |
| `isAlive` | Whether the isolate has not panicked |

- After a panic, the isolate is **dead**: its stack has unwound (running destructors and ending
  borrows), and its state is released. Every later `call` returns `Err` with the same `Panic`
  without running anything. To recover, start a new isolate. If `start`'s lambda panics, the
  isolate starts out dead.
- `Panic` is a sendable struct: the message, the source location, and the stack trace.
- An `Isolate` is a normal object that belongs to the thread that created it.
- Isolates nest. A panic stops the innermost isolate it is in.
- Cycles among a dead isolate's objects are freed by the next `Memory.collectCycles` on the thread,
  like any others ([03-memory-model.md](03-memory-model.md#cycles-memorycollectcycles)).

### Statics

Each isolate has its own copy of the mutable statics (§5.5), initialized when it starts, so a panic
can't leave the caller's statics half-changed either. Code reaches the current isolate's copy
through one pointer, which entering and leaving `call` switches.

### Threads, jobs, and locks

- A thread is an isolate running on its own OS thread. A panic ends the thread, and joining it
  returns `Err(Panic)`. The main thread is the outermost isolate: a panic there, outside any
  `Isolate.call`, aborts the process.
- A job is part of the isolate that launched it, since it changes the launcher's data through spans
  and `ref`s. A panic in a job is raised in the launching code once every job of the launch has
  finished, and stops the launcher's isolate.
- A lock (`Mutex<T>`, `RwLock<T>`) held while a panic unwinds is **poisoned**: whatever it
  protects may be half-changed. Locking a poisoned lock panics; `isPoisoned` tests for it.
  `Atomic<T>` and `Channel<T>` are never left half-changed, so they are not poisoned.

### Implementation

A panic inside an isolate is a C++ `throw` of a runtime type that only the boundary of `Isolate.call`
or of a thread catches, after the runtime prints the message and the stack trace. Outside any
isolate, a panic aborts directly. Code that doesn't panic pays nothing at run time, since C++
unwinding is table-based; the tables make the program larger, and `--panic=abort` leaves them out.

> Not implemented yet. Isolates need sendable types and lambdas, and come with threads.

## 5.7 Unsafe

Inside `unsafe`, raw pointers may be shared between threads without checks. Races in unsafe code are
undefined behavior and the programmer's responsibility.

## 5.8 Runtime

The runtime provides a work-stealing thread pool for jobs (implementation *open*: custom or a
library such as enkiTS).
