# Next: `Isolated<T>`, a graph that carries its cycle buffer

Repo `E:\Repos\Wylow\wylow`, branch `dev`. Threads (0056), an interpreter that interleaves (0057),
the sendable floor (0058), and channels (0059) are done. Read decisions 0016, 0017, 0036, 0054,
0055, 0056, 0057, 0058, and 0059, the spec sections `concurrency.isolated`,
`concurrency.sendable`, `concurrency.sendable.moving`, `concurrency.shared-functions`,
`memory.cycles`, and `library`, the "Threads" section of `docs/design/runtime.md`,
`docs/design/testing.md` (the modes, the `// tsan` marker, and why the fuzzer's corpus leaves
threaded files out), and issue #14.

`Isolated<T>` is the first value that carries a cycle buffer with it, and it is what `Mutex` needs:
`lock` is `open` while it holds the lock, so this step comes before the synchronization types.

## What is done

- `shared class` (0054), `shared fn` (0055), and `std::thread` (0056).
- The interpreter runs each logical thread on an operating-system thread, with no schedule of its
  own (0057). Each thread's cycle buffer, collector figures, destroying counter, and call depth are
  in an `Interpreter.ThreadState` held in a `ThreadLocal`.
- `std::channel` (0059): `Channel<T>`, `Sender<T>`, and `Receiver<T>` over a counted
  `SyncState<T>`. `SyncState` is the sendable floor: `StdlibSymbols` marks it, `TypeSendable`
  accepts it, and `T extends sendable` checks its argument.
- `// tsan` marks a test that starts a thread, and `--cc tsan` runs only those, on a Linux runner.

## Step 1: the type and its storage

Add the module `std::isolated`. The module that later holds `Mutex`, `Atomic`, and `Frozen` is a
separate decision. Decide and record:

- How `Isolated<T>` holds its value and its buffer. One shape is a `pub unique struct Isolated<T>`
  with the value as a field and a handle to the runtime's buffer, as `Thread<T>` holds a `byte*`.
  The handle may be an `extern struct`, as `SyncState` is, which keeps the buffer's shape inside
  `std`.
- How the compiler answers that `Isolated<T>` is sendable for any `T` (spec
  `concurrency.sendable`). This is not `SyncState`'s rule: `SyncState` is sendable because the
  runtime owns what it holds, while `Isolated<T>` is sendable because nothing outside the box refers
  to what it holds. A second mark that `TypeSendable` reads (`TypeSymbol.IsIsolated`, say) keeps the
  exception one name the standard library declares.
- The conversion of a sendable value to `Isolated<T>`. The spec says a sendable value is isolated
  already and converts to `Isolated<T>` by being moved in. Decide whether the binder accepts a
  conversion rule in `Conversions` with a lowering that builds the box, or a constructor that
  counts as one, and test that a value whose type is not sendable is refused with the field named.
- The destructor. A box dropped without `take` releases its graph on the thread that releases it.
  The buffer holds objects the graph's own references still own, so releasing the buffer must not
  release them a second time.

## Step 2: the buffer swap

The spec says an isolated graph has a cycle buffer of its own, and that while the constructor's or
`open`'s lambda runs, the thread's buffer is the graph's.

- The interpreter: `Interpreter.ThreadState` holds `Buffer`, `Capacity`, and `Holes`, plus the
  scratch lists a collection reuses. Swap the three fields around the call and restore them after.
  Keep the scratch lists, since they are used by one collection at a time. The graph's three fields
  live in the box.
- The runtime: the buffer is `CycleCollector::m_Buffer` with `m_Capacity` and `m_Holes` in
  `runtime/wyl/cycles.h`. Give the collector a buffer record and a swap, or move the three members
  into a `Buffer` the collector holds by value. The scratch vectors stay where they are.
- The buffer must survive the graph's move to another thread. It travels inside the box, and each
  object's `Slot` still numbers a place in that buffer. `take` appends the graph's buffer to the
  thread's and renumbers every object's slot; the interpreter merges the lists the same way.
- `memory::collectCycles()` inside the constructor or `open` then collects the graph alone. Decide
  what `cycleStats()` reports there: the spec says the collector's figures are per thread, and the
  graph's collection runs on the thread.

## Step 3: the API

The spec's shape is `Isolated<Index> built = Isolated<Index>(() -> Index.build(entries));`,
`index.open(i -> i.addEntry(7, "alpha"));`, and `index.take()`. Decide:

- The constructor's parameter, a non-escaping `shared fn() -> T`. A shared lambda's captures are
  sendable and copyable, so nothing of the thread's comes in (0055).
- `open`: a non-escaping `shared fn(ref T) -> R` where `R` is sendable or `void`. One generic
  `open<R>` cannot return `void`; decide between `Unit` and an overload. Decide whether the
  receiver is `mut`, since `open` changes the field the lambda refers to.
- `take(this Isolated<T> box) -> T`, a by-value receiver, as `join` and `Result`'s methods take
  theirs. It moves the value out and ends the box, and the buffer joins the thread's.
- What dropping a box without `take` does, and whether the compiler or the type's destructor is what
  performs it.

## Step 4: tests

- Constructing on one thread and reading the graph through `open`; `open` changes it in place and
  returns a sendable value.
- A graph moves through a channel: one thread constructs it, another opens it, and the objects'
  destructors run on the second thread.
- `take` moves the graph out and ends the box; the buffer is the thread's afterwards, and a later
  collection frees a cycle that was in the graph.
- `collectCycles` inside `open` frees a cycle among the graph's objects and leaves a cycle among
  the thread's own objects alone.
- The binder errors: a shared lambda that captures a normal object, and a value that is not
  sendable where the isolation requires one, with the error naming the field.
- Every test that starts a thread carries `// tsan`.

## Step 5: docs

Add the decision records (the storage, the buffer format, the API, and whether the conversion is a
rule), the module's doc comments, an appendix section beside `std::channel`, the runtime's buffer
paragraph, and the testing notes. Mark `Isolated<T>` implemented in the roadmap and the suggested
order.

## Still open

- `Mutex` and the one-lock check, `Atomic`, `Frozen`, and one parallel loop (0016). A spin on an
  `Atomic` is the first program whose progress depends on another thread's steps, so the
  interpreter's schedule matters then.
- The lifetime gaps: a destructor that spawns a thread capturing `this`; releasing a `Thread` whose
  body waits forever; static initialization across threads; a `Sender` that stays inside a cycle.
- The in-use bit is not atomic (0049). Recheck it against `Mutex` and `Atomic`, and against a value
  another thread releases.
- A unique value already crosses threads through a channel, and its destructor runs on the receiver
  (`channels/unique_value`). Extend the cross-thread free coverage to a graph that moves whole.
