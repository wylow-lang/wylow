# 0056. Threads: a unique handle, a free `spawn`, and a by-value `join`

- Status: accepted (2026-11-05); its interpreter bullet is superseded by [0057](0057-interpreter-interleaving.md) (2026-11-06); superseded by [0107](0107-start-thread.md) (2026-11-13)
- Spec: `programs.execution`, `concurrency.confinement`, `concurrency.sendable`,
  `concurrency.shared-functions`, `concurrency.panics`, `memory.cycles`, `library.thread`

## Decision

`std::thread` declares `Thread<T>`, `spawn`, and `join`.

- `Thread<T>` is a `unique struct` that holds one file-private `byte*` handle. Its destructor calls
  the runtime's release, which waits for the body to return and then releases the result. A thread
  that `main` still holds is therefore joined before `main` returns.
- `spawn<T extends sendable>(escaping shared fn() -> T body) -> Thread<T>` is a free function. A
  static method of `Thread<T>` cannot infer the type's type argument from a call's arguments, so
  `Thread.spawn(body)` would need its result type written at every call.
- `join(this Thread<T> thread) -> T` is a by-value receiver. A by-value receiver moves the thread,
  so a second `join` uses a moved value, which the flow analysis rejects at compile time rather than
  the runtime checking it. `join` waits until the body returns. It panics for a thread that was
  never started, which is what the default value of `Thread<T>` is, since a struct has a default
  value and the default handle is null.
- Releasing a `Thread` without calling `join` waits for the body to finish and then releases the
  result on the releasing thread. A panic in the body ends the program from the body's thread, so
  no join path can swallow it.
- The runtime allocates the handle on the global heap and gives `spawn` its address as the `byte*`
  handle. The three extern functions that create, join, and release it are `unsafe` and
  file-private, so the handle's shape stays inside `std`. The set of `extern struct`s stays closed:
  no language rule needs a type for the handle.
- The body is a `wyl::Function<T()>` whose closure is shared, so the body may run after `spawn`
  returns. The runtime starts an operating-system thread that makes that thread's state, runs the
  body, stores its result in the handle, and collects the thread's cycle buffer when the thread
  ends.
- A unique value reaches a thread only as its result in this step. A shared lambda copies what it
  captures and every capture is sendable and copyable ([0055](0055-shared-functions.md)), so a
  body's captures cannot hold a unique value. Moving a unique value into a thread needs an
  overload that takes it, or `Isolated<T>` ([0017](0017-isolated-graphs.md)).
- `spawn` panics when the program calls it before `main` begins
  ([0030](0030-statics-are-initialized-before-main.md)). `wyl::Init` marks the main thread's
  state, and the emitted `main` tells the runtime that `main` has begun after every static is
  initialized.
- The interpreter runs the body where `spawn` is called, in a fresh cycle buffer, and collects that
  buffer when the run ends. It shares the output, the statics, the step budget, and the creation
  counter with the caller. This is one schedule: this step has no type through which one thread
  observes another's progress, and interleaving may change only the order of output
  (`introduction.observable`).

## Alternatives

- **`Thread<T>` as a class.** Class references are copyable, so two copies could both call `join`,
  which would need a runtime "already joined" check, and `join` could not take its receiver by
  value ([0050](0050-by-value-receivers.md)). The unique struct makes "joined at most once" a
  compile-time property and lets the value own a unique result directly.
- **`Thread.spawn` as a static method.** It would need the result type written: `Thread<int>.spawn`.
  A free function infers `T` from the body, which is what a call site reads.
- **`join` by reference, or a nullable thread.** A second join then needs a runtime check and a
  state flag. Taking the receiver by value makes the second join impossible to write.
- **A language type for the handle.** It would add an `extern struct` or a language rule for one
  standard library type's storage, while a `byte*` and three `unsafe` functions keep both closed.
- **A `join` that does not wait.** The thread's result is not available until the body returns, and
  the rules give no other way to observe completion. Waiting is what `join` means.
- **Running the child concurrently in the interpreter.** Threads would then interleave, so output
  order would differ between runs and from the native modes. Running the body to completion at
  `spawn` is one schedule that the observable-behavior rule admits, and it runs the same code with
  the same cycle rules.
- **An argument parameter now, so a unique value moves inward.** It needs a second type parameter
  inferred from the argument and a lambda whose parameter type comes from it, and `Isolated<T>` is
  the type that will move a graph anyway. It can be added later as an overload without changing
  this one.

## Why

The spec fixes four rules for threads: releasing a `Thread` that `main` holds joins it, the result
and argument are sendable, the body is an escaping `shared fn`, and a panic on any thread ends the
program. A unique struct with a destructor satisfies the first rule: the value has one owner, and
its destructor blocks and then releases what it owns. Taking the receiver by value satisfies "a
thread is joined at most once" with the language's existing move rules, as `Result`'s methods take
their receiver. A free `spawn` keeps type inference working.

The handle is runtime storage, not a Wylow value: no program may inspect it, and the library is the
only code that names it. A `byte*` is the type that says so without adding a type or a rule.

A shared lambda copies what it captures, which is why the previous decision makes every capture
sendable and copyable. That decision is what limits this step to moving a unique value outward: a
unique value cannot be copied into a body. The interpreter's synchronous run is the schedule in
which the child runs before its parent continues; no type in this step exposes which schedule ran,
and the spec allows any interleaving.
