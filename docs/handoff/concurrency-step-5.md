# Next: channels, and an interpreter that interleaves

Repo `E:\Repos\Wylow\wylow`, branch `dev`. `shared class` (0054), `shared fn` (0055), and threads
(0056) are done. Read decisions 0015, 0016, 0036, 0054, 0055, and 0056, the spec sections
`concurrency.confinement`, `concurrency.sendable`, `concurrency.shared-classes`,
`concurrency.panics`, `concurrency.interleaving`, `programs.execution`, `memory.cycles`, and
`introduction.observable`, the "Threads", "Allocation", and "Cycle collector" sections of
`docs/design/runtime.md`, and issues #14 (the sendable floor) and #20 (thread lifetime gaps).

This step adds the first type through which one thread observes another: an unbounded channel. That
changes what a valid schedule is, so the interpreter's thread model changes first. The native
runtime keeps the operating system's threads and imposes no order on them; the spec allows a run's
output order to vary with scheduling (`concurrency.interleaving`). Only the interpreter fixes an
order, because the suite, the fuzzer, and the generator use it as the oracle.

## What is done

- Every thread has its own pools and cycle buffer (`wyl::ThreadState`). A non-main thread collects
  its buffer when it ends.
- `std::thread` declares `Thread<T>`, `spawn`, and `join`. The runtime holds the result, joins when
  the thread is released, and panics if `spawn` runs before `main` begins.
- The interpreter runs a body where `spawn` is called, in a fresh cycle buffer, and collects that
  buffer when the run ends. That is one schedule only while no type lets one thread observe another
  thread's progress. A channel ends that condition.

## Step 1: an interpreter that interleaves

Replace the synchronous `spawn` and record the answer in a decision after 0056. The decision
replaces the interpreter bullet of [0056](../design/decisions/0056-threads.md).

The recommended model is one operating-system thread per logical Wylow thread, with a baton that
only one thread holds at a time:

- A thread runs while it holds the baton. A blocking operation hands the baton to the scheduler,
  and the scheduler picks the next runnable thread in a fixed order, such as the order the threads
  were started. Two runs of one program then interleave identically. This order is the
  interpreter's, not the language's: a compiled program runs on the operating system's threads, and
  the spec lets its output order vary between runs.
- Only one logical thread runs at a time, so the object graph, its counts, the cycle collectors,
  and the output need no synchronization. The interpreter keeps its non-atomic counting.
- Each logical thread gets its own cycle buffer, its own collector stats, and its own destroying
  counter. The output, the statics, the step budget, the creation counter, and the panic that ends
  the run are shared.
- `join` and release wait for the target to finish. A panic on any logical thread ends the run, as
  a panic does today.

The alternative is real operating-system threads that share the interpreter's state. That needs
atomic counts, a collector lock, and an output lock, and its schedules vary between runs. The
interpreter is the meaning oracle, so this handoff recommends against it.

Decide whether a quantum of steps hands the baton on, so that a thread spinning on a value another
thread will set can make progress. The step budget already ends a spin as a budget overrun, which is
an acceptable answer until `Atomic` arrives; if a quantum is chosen, its size must not depend on the
machine.

Rewrite `Interpreter.Threads.cs` and keep every test in `tests/cases/threads/` passing.

## Step 2: the sendable floor (#14)

The synchronization types need storage that the field rules do not call sendable. A `shared class`
may hold only sendable and copyable fields (0054), and a raw pointer is not sendable
(`TypeSendable`). Decide and record how the standard library declares such a type sendable.

The recommended form is one `extern struct` handle, such as `SyncState`, that holds the runtime's
storage and that `StdlibSymbols` marks sendable, as it marks the one `Weak<T>` for its shared
target. A `shared class` may then hold a `SyncState` field, and the runtime owns the storage behind
it.

The alternatives are an annotation that declares a type sendable (a second spelling of a property
the compiler computes, allowed only in `std`), and a global runtime table keyed by an `int` id
(the language stays untouched, but every synchronization type pays a lookup and the table answers
the lifetime question a second time).

## Step 3: `std::channel`

Add a module for the channel (start with `std::channel`; the module that later holds `Mutex`,
`Atomic`, and `Frozen` is a separate decision). The rules already decided:

- A channel has no size limit, so `send` never waits (0016).
- A channel closes when its last `Sender` is released (0016).
- The values it carries are sendable, and a unique value moves through it
  (`concurrency.sendable`).
- A channel is not cycle-collected; a channel that refers back to an object needs a `Weak<T>`, as
  a shared object does (0054).

Decide and record the rest:

- The API. One shape: a `shared class Channel<T extends sendable>` whose `sender()` method returns
  a `Sender<T>` and whose `receiver()` method returns a `Receiver<T>`, both shared classes.
  `Sender.send(T value)` returns nothing, and `Receiver.receive() -> T?` returns null once the
  channel is closed and drained.
- What `receive` does when the channel is open and empty: it waits for a value. State that a
  waiting thread holds no Wylow lock, so the one-lock rule of 0016 does not apply to it.
- What a `send` does when every `Receiver` has been released. Sending never waits, so the value
  stays queued until the last `Sender` closes the channel, and closing drops every queued value.
  Decide whether that rule stands, or whether releasing the last receiver also closes the channel.
- Whether the `Channel` object itself keeps the channel open. Recommended: only `Sender` values
  count, so a program can drop the `Channel` after making its ends.
- What `Receiver.receive` does with a channel that never had a `Sender`: decide whether that waits
  forever (a bug the step budget catches in the interpreter) or treats the channel as closed.

The runtime's side is a new `runtime/wyl/channels.h`: a state on the global heap with a mutex, a
condition variable, a queue of `T`, and counts of the ends. The runtime may use the operating
system's mutex and condition variable; the one-lock rule governs Wylow locks. A `Sender`'s release
closes the channel at zero and wakes every receiver.

## Step 4: tests

- A program sends and receives on one thread.
- A child receives what its parent sends, and a parent receives what its child sends. Each of these
  is a schedule the synchronous interpreter could not run.
- Several senders, and the close when the last one is released: `receive` drains the queued values
  and then returns null.
- A unique value moves through the channel, and its destructor runs on the receiving thread.
- A value queued when the channel closes is dropped, if that is the rule chosen.
- Every test synchronizes before a line either thread could print: join, or a channel hand-off.
  Do not print from a thread without joining or receiving first, since stdout order is the one
  thing the spec lets vary (`introduction.observable`).
- Run each test in the interpreter and the native modes.

## Step 5: docs

Update `docs/design/runtime.md` with the channel's state and the interpreter's schedule, add the
decision records (the interpreter's model, the sendable floor, the channel's API), add the channel
to the roadmap issue, and rewrite the claim in 0056's interpreter bullet through the new decision.
Nothing here belongs in the spec's chapter 19 unless a rule changes what a program does that the
current text does not cover; the channel's API is `std`'s, documented with doc comments.

## Still open

- `Isolated<T>` and its region buffer (0017). `Mutex` holds one, and `lock` is `open` while holding
  the lock, so `Isolated<T>` comes before `Mutex`.
- `Mutex` and the one-lock check, `Atomic`, `Frozen`, and one parallel loop (0016). A spin on an
  `Atomic` makes the interpreter's quantum decision matter.
- A `Thread` argument that moves a unique value inward, and a non-escaping `shared fn` handed to
  another thread, which only `unsafe` code may do.
- The lifetime gaps in #20: a destructor that spawns a thread capturing `this`, a thread blocked
  forever when its `Thread` is released, static initialization across threads, and a channel's end
  lifetime.
- The in-use bit is not atomic (0049); recheck it against a value that another thread releases.
- The remote free list and chunk adoption in the runtime are still unreachable from a program:
  nothing sendable carries an object from one thread's pool into another's. `Isolated<T>` is the
  first type that will.
