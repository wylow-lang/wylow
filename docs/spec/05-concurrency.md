# 5. Concurrency

Data races are undefined behavior in C++, so safe VPL must make them impossible. The model has
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
| References to a `shared class` (including `String`, `Frozen<T>`) | ✅ |
| References to normal classes (`List<T>`, `T[]`, `Map`, user classes), and `Weak<T>` | ❌ |
| Structs/enums containing any non-sendable field | ❌ |

Wherever a sendable type is required, using a non-sendable one is a compile error naming the
offending field.

### `shared class`

A `shared class` may be referenced from multiple threads.

```java
shared class TextureCache {
    final String rootPath;
    Mutex<Map<String, TextureId>> entries;
    Atomic<int> hits;
}
```

- Uses an **atomic** reference count.
- Every field must be either `final` and sendable, or a synchronization type (`Mutex<T>`,
  `RwLock<T>`, `Atomic<T>`, `Channel<T>`, ...). The compiler enforces this.
- `String` is a `shared class`.

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

A struct containing a class reference (for example `struct Enemy { Behavior ai; }`) is not
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

## 5.5 Statics

> ⚠️ **Mutable statics are thread-local.** This differs from Java.

| Declaration | Meaning |
|---|---|
| `static int frameCount;` | **One copy per thread.** No races possible |
| `static final int MAX = 64;` / `const` | Shared constant |
| `static shared Atomic<int> totalFrames;` | One process-wide copy. Type must be sendable, mutation only through sync types |

A job that calls a function which updates a mutable static updates the *worker thread's* copy.

## 5.6 Panics

A panic on any thread, including inside a job, aborts the whole process.

## 5.7 Unsafe

Inside `unsafe`, raw pointers may be shared between threads without checks. Races in unsafe code are
undefined behavior and the programmer's responsibility.

## 5.8 Runtime

The runtime provides a work-stealing thread pool for jobs (implementation *open*: custom or a
library such as enkiTS).
