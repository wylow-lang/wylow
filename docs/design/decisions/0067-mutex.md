# 0067. `Mutex<T>`: an isolated graph behind a lock

- Status: accepted (2026-11-09)
- Spec: `concurrency.mutex`, `concurrency.confinement`, `concurrency.sendable`,
  `concurrency.isolated`, `library.mutex`

## Decision

```java unchecked
pub shared class Mutex<T> {
    pub Mutex(Isolated<T> value);
    pub R lock<R extends sendable>(shared fn(ref T) -> R body);
}
```

- **What a mutex holds.** A mutex holds an isolated graph, so `T` has no bound. The constructor takes
  an `Isolated<T>`, and a sendable value converts to one ([0063](0063-isolated-conversion.md)), so
  `new Mutex<int>(0)` and `new Mutex<Node?>(null)` need no box written out. The sentence in
  `concurrency.confinement` now says that a mutex takes its value as an `Isolated<T>`, and the
  example of a `sendable` bound in `concurrency.sendable` is now `Channel<T extends sendable>`.
- **The module.** `Mutex<T>` is declared in `std::threading` ([0066](0066-threading-module.md)), in
  `mutex.wyl`.
- **The storage.** `Mutex<T>` is a shared class, so a program may pass a reference to it between
  threads and a static may hold one. Its one field is a `package extern struct MutexState<T>`, which
  `StdlibSymbols.Register` marks as a sendable handle, as it marks `SyncState`
  ([0058](0058-sendable-handle.md)). The C++ `wyl::MutexState<T>` holds a pointer to a
  `wyl::threading::MutexData<T>`: the operating system's mutex, the value, and the graph's
  `IsolatedState`.
- **The handle is not counted.** The `Mutex` object owns the data, and its destructor calls
  `releaseMutex`, which frees it. A copy of the handle exists only inside a call on the mutex, and the
  call's receiver keeps the object alive, so a thread that waits for the lock keeps the data alive
  too.
- **Construction.** The constructor moves the box into a `mut` local, moves the value out with
  `takeValue` ([0060](0060-isolated-storage.md)), and passes it by value, with the box's state by
  `ref`, to `createMutex`. `createMutex` panics if something the value owns is borrowed, and moves
  the graph's buffer into the mutex's state. The emptied box then merges an empty buffer.
- **`lock`.** It has the shape of `Isolated.open` ([0062](0062-isolated-api.md)): a non-escaping
  shared lambda that takes a reference to the value, and a sendable result. It is not a `mut` method,
  since the value lives in the runtime's storage, not in a field of the object. `lockMutex` takes the
  lock, swaps the graph's buffer in, runs the lambda, swaps the buffer back, and releases the lock.
- **Release.** `MutexData` declares the graph after the value, so C++ destroys the graph's state first
  and its buffer joins the releasing thread's before the value is released, as an isolated box's
  does. The interpreter's `ReleaseMutex` merges the graph and then releases the value.
- **The in-use bit** ([0049](0049-getting-a-unique-value-out.md)) stays a plain bit. Only the thread
  that holds the lock reaches the value. Releasing the operating system's mutex orders that thread's
  writes before the reads of the next thread that takes it, so the bit needs no atomic access.

## Alternatives

- **`Mutex<T extends sendable>`, with the box only as an implementation.** A `List<int>` is not
  sendable, because a `List` is a normal class, so a mutex could never guard a container. Rust's
  [`Mutex<T>`](https://doc.rust-lang.org/std/sync/struct.Mutex.html) is `Sync` when `T: Send`, so a
  `Mutex<Vec<T>>` may be shared. Swift's `Mutex<Value>`
  ([SE-0433](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0433-mutex.md)) is
  `Sendable` for any `Value`, and its initializer takes a `sending` value, which is a value in a
  disconnected region
  ([SE-0430](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0430-transferring-parameters-and-results.md)).
  A region of that kind is what `Isolated<T>` is.
- **A constructor that starts from `T`'s default value, or one that takes a builder lambda.** Either
  would be a second way to make a mutex, since `Isolated<T>`'s constructor already takes a builder and
  a sendable value already converts.
- **A counted handle, as `SyncState` is.** A channel's state must outlive the `Channel` object,
  because its ends refer to it. A mutex's state has one owner, and an atomic count would be paid on
  every `lock` call that copies the handle.
- **`createMutex` taking the value by `ref`.** A `ref` argument marks a struct with a destructor as
  in use for the call, so the check that the value is not borrowed would see the mark of its own
  argument.

## Why

A mutex exists to let several threads change one value, and most values worth changing that way are
containers and object graphs. An isolated graph is the language's unit of state that may move between
threads, so a mutex that holds one guards anything a box can carry. The lambda's sendable captures and
result keep the graph isolated across every `lock`, which is the argument `Isolated.open` already
makes, so the mutex adds only the lock itself.
