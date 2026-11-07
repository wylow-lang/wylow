# Next: `Mutex` and the one-lock check

Do `before-mutex.md` first. This work starts when that handoff is done and deleted.

Repo `E:\Repos\Wylow\wylow`, branch `dev`. Threads (0056), an interpreter that interleaves (0057),
the sendable floor (0058), channels (0059), and `Isolated<T>` (0060–0063) are done, and so is the
constructor-start rule (0064). Read decisions 0016, 0049, 0054, 0057, 0059, 0060, 0061, 0062, and
0064, the spec sections `concurrency.confinement`, `concurrency.sendable`,
`concurrency.sendable.moving`, `concurrency.shared-classes`, `concurrency.isolated`, `library.isolated`,
`variables.statics`, and `memory.cycles`, the "Threads" section of `docs/design/runtime.md`,
`docs/design/testing.md` (the modes and the `// tsan` marker), and issue #14.

`Mutex` is the type through which the one-lock rule (0016) becomes observable: a thread holds at most
one lock and waits for nothing while holding it.

## What is done

- `shared class` (0054) and `shared fn` (0055): a shared class's fields are sendable and copyable
  and cannot be `mut`; a shared lambda captures only sendable, copyable values.
- The interpreter runs each logical thread on an operating-system thread, with no schedule of its
  own (0057). Each thread's cycle buffer and collector figures are in `Interpreter.ThreadState`.
- `std::channel` (0059) and `std::isolated` (0060–0063): `Isolated<T>` is a unique struct holding
  the value and a runtime-shaped `IsolatedState`, sendable for any `T`, with `open`, `take`, and a
  graph buffer that is the thread's while the lambda runs.

## Step 1: what a mutex holds

The spec pulls two ways, so settle this first and record it. `concurrency.confinement` says a value
reaches another thread through a mutex only if it is sendable. `concurrency.isolated` says a mutex
holds an isolated graph, and that `lock` is `open` while the mutex holds the lock.

- **An isolated box.** `Mutex<T>` holds an `Isolated<T>`, so `T` needs no `sendable` bound: the box
  is why the graph is safe between threads, and `lock`'s result is sendable, so nothing inside
  escapes except sendable data. This reading makes `concurrency.confinement`'s sentence about a
  mutex wrong, and that sentence changes.
- **A sendable value.** `Mutex<T extends sendable>` guards a value that is sendable already, and the
  isolated box is only how `lock` gets `open`'s buffer. This reading makes `concurrency.isolated`'s
  sentence a statement about the implementation rather than a rule, and the spec says why the bound
  is there.

Decide which is the rule, and edit the other section in the same change.

## Step 2: the module and the storage

Decide where the lock types live and record it. `Isolated<T>` is in `std::isolated`; the module that
holds `Mutex`, `Atomic`, and `Frozen` is still open, and whether these three share one module is the
choice to argue about. This step adds a module rather than widening `std::isolated`.

Decide how `Mutex<T>` holds its graph, and record it:

- `Mutex<T>` must be a `shared class`, since a program passes a reference to it between threads and a
  static may hold one (`variables.statics` requires sendable and copyable). A shared class's fields
  cannot be `mut` (`concurrency.shared-classes`), so `lock` cannot call `Isolated.open` on a field:
  `open` is a `mut` method and a shared class's field is not a mutable place.
- The working shape is that the mutex holds a package `extern struct` handle, as `Channel` holds
  `SyncState`, and the runtime owns the isolated graph and the operating system's mutex inside it.
  `lock` then calls an extern function that takes the lock, swaps the graph's buffer in, runs the
  lambda, and swaps it back, as `wyl::isolated::openIsolated` does. The interpreter does the same
  with its own state.
- Decide whether a `Mutex` is created empty, from a default value of `T`, or by a constructor that
  takes the value or an `Isolated<T>`. A mutex whose value has no default cannot be created without
  one, and a mutex created empty for an `Isolated` reading needs a graph built inside it.

## Step 3: `lock` and the one-lock check

The shape: `mutex.lock(value -> value.add(7));` and `int n = mutex.lock(value -> value.count());`,
with the mutex's value as the lambda's argument. Decide and record:

- The signature: `pub R lock<R extends sendable>(shared fn(ref T) -> R body)`. `R` must be sendable,
  so nothing inside escapes except a sendable value, and a lambda that only changes the value
  returns `Unit`, as `open`'s does (0062). The lambda is non-escaping and shared, so it captures
  only sendable, copyable values.
- The one-lock check runs every time. A per-thread flag answers whether this thread holds a lock;
  the runtime keeps it in `wyl::ThreadState` and the interpreter in `Interpreter.ThreadState`. The
  check panics when `lock` is called while the flag is set.
- "Waits for nothing while holding it" is the same flag read by every wait. `receive` waits for a
  value or the close, `join` and a `Thread`'s release wait for the body to return, and `lock` waits
  for another thread's lock; each panics when the flag is set. The check gives the same answer in
  every build mode. Decide the message and where the panic is reported, and whether `receive` and
  `join` become `@CallerLocation` so the message names the program's call.
- The operating system's mutex guards the graph. The body runs on the locking thread with the mutex
  held, and the graph's cycle buffer is the thread's while it runs. A panic ends the program, so no
  lock needs poisoning (`concurrency.panics`).
- A `Mutex` reference keeps its object alive while a thread waits on it, as any shared class's
  reference does. Decide whether the runtime's handle counts the threads that hold or wait for the
  lock, as `SyncState`'s count keeps a channel's state alive.

## Step 4: tests

- Two threads alternate through one mutex and print a value each holds; synchronize with `join`, and
  put `// tsan` on every test that starts a thread.
- A second `lock` on the same thread panics, and a `receive` or a `join` while holding a lock
  panics, in both executors.
- A graph behind a mutex runs `memory::collectCycles` inside the body and collects the graph alone,
  as `isolated/collect_inside_open` does for `open`.
- A unique value moves into and out of a mutex, and its destructor runs on the thread that releases
  it.
- The binder errors: a lambda whose result is not sendable, and a shared lambda that captures a
  value it may not capture.

## Step 5: docs

- The spec sections: the lock rule (one lock per thread, and no wait while holding it), `Mutex`'s
  API and its value, and how a lock and an isolated graph's buffer meet. Add the appendix section
  beside `library.isolated`.
- Decision records for the module, the storage, the API, and the check.
- The "Threads" section of `docs/design/runtime.md`: the runtime's mutex and the flag.
- The enforcement table rows for the one-lock check and for the runtime's handle.
- Mark `Mutex` implemented in the roadmap and the suggested order.

## Still open

- `Atomic<T>`, `Frozen<T>`, and one parallel loop (0016). `Atomic`'s memory ordering is the
  decision to make: every operation sequentially consistent is one answer, and any weaker model
  states which operations pair. `Frozen<T>` is an immutable value computed once; decide whether it
  wraps an `Isolated<T>` or the value itself, and what `get` returns.
- `Mutex.lockBoth` (two locks in a fixed order) is open until a program needs it.
- The in-use bit is not atomic (0049). Recheck it against `Mutex` and `Atomic`, and against a value
  another thread releases.
- The lifetime gaps: a destructor that spawns a thread capturing `this`; releasing a `Thread` whose
  body waits forever; static initialization across threads; a `Sender` that stays inside a cycle.
- The cross-thread free coverage: a graph that moves whole now runs its destructor on the receiver
  (`isolated/channel_move`); extend it to a value that leaves a mutex.
