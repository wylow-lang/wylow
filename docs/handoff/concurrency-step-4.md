# Next: threads

Repo `E:\Repos\Wylow\wylow`, branch `dev`. Shared classes (0054) and shared functions (0055) are
done. Read decisions 0015, 0016, 0017, 0030, 0036, 0054, and 0055, the spec sections
`concurrency.confinement`, `concurrency.sendable`, `concurrency.shared-functions`,
`concurrency.panics`, `programs.execution`, `memory.cycles` (the rule for a thread's buffer), and
`introduction.observable`, the "Threads" and "Allocation" sections of `docs/design/runtime.md`, and
issues #31 (the collector's state is not thread safe) and #20 (thread lifetime gaps).

A `shared fn` may run on another thread, and nothing carries one there yet. This step adds the
threads themselves: the runtime's per-thread state and `std`'s `Thread`. Channels, `Mutex`,
`Atomic`, `Frozen`, the parallel loop, and `Isolated<T>` come after this step.

## What is done

- A shared class's object has an atomic count, comes from the global heap, and never enters the
  cycle collector (0054).
- A function type has four forms, and an escaping shared lambda's closure is a
  `wyl::SharedClosure`, which is a shared object (0055).
- Statics are read-only, sendable, and initialized before `main` on one thread (0029–0032, 0053).
- The C++ runtime has one global `wyl::s_Pools` and one global `wyl::s_Cycles`, and
  `Object::s_ObjectsCreated` is a non-atomic inline static. The interpreter is one `Interpreter`
  with one cycle buffer.

## Step 1: settle `Thread`'s shape

The spec already fixes these rules:

- A `Thread` that `main` still holds is joined when it is released, before `main` returns
  (`programs.execution`).
- A thread's argument and result carry sendable values, and move unique ones
  (`concurrency.sendable`).
- A thread's body is an escaping `shared fn` (`concurrency.shared-functions`).
- A panic on any thread ends the program (`concurrency.panics`).

Decide the rest and record the answer in a decision after 0055:

- Whether `Thread<T>` is a class or a `unique struct`. A unique result needs the unique struct,
  since the struct can own the result and its destructor can join.
- How a body is given and how its result comes back. One shape: the free function
  `spawn<T extends sendable>(escaping shared fn() -> T body) -> Thread<T>`, and
  `join(this Thread<T> thread) -> T` as a by-value receiver. The body's captures are the thread's
  argument.
- Why `spawn` is a free function and not `Thread.spawn`: a static method of a generic type cannot
  infer the type's type argument from its arguments.
- What a second `join` does, and what releasing a `Thread` whose body has not finished does. The
  destructor joins a thread nobody joined, releases its result, and must not let the body's panic
  pass unnoticed.
- How the runtime handle is carried without extending the closed set of `extern struct`s. A `byte*`
  that the runtime allocates, and the join frees, keeps the set closed; the handle's functions are
  `unsafe` and live in `std`.

## Step 2: the per-thread runtime state (#31)

Make `runtime/wyl/pools.h` and `runtime/wyl/cycles.h` hold one state per thread:

- `s_Pools` and `s_Cycles` become `thread_local`, made on first use in that thread.
- `Object::s_ObjectsCreated` becomes atomic with relaxed increments, or per thread. The collector
  orders a freed cycle's destructors by age, and the order only has to be fixed.
- `Object::operator new`, `operator delete`, `Object::Release`, `Object::Destroy`, and
  `SharedObject::Release` use the current thread's state.
- A normal object may be allocated on one thread and released on another after it moves inside a
  unique value. Each chunk records the pool that owns it, and a free from another thread pushes the
  slot onto the chunk's atomic remote free list. The owner drains that list when its own list runs
  out. `runtime.md` under "Allocation" describes this.
- A non-main thread frees the cycles in its buffer when the thread ends (`memory.cycles`). The main
  thread does not collect at exit (`programs.execution`).
- A chunk that still has live slots when its thread ends is adopted by the thread that frees into
  it, so the dead thread's `Pools` outlives the thread while a chunk refers to it.
- Check every other runtime global for a race, starting with the list in #31.

## Step 3: `std::thread`

- Declare `Thread`, `spawn`, and `join` in a new `src/Wyl.Compiler.Semantics/Stdlib/thread/thread.wyl`,
  which is module `std::thread`. A `.wyl` file under `Stdlib/` is embedded and compiled
  automatically.
- The body is a `wyl::Function<T()>`, which is a shared closure. The runtime starts an OS thread
  that calls the closure and stores its result; `join` joins that thread and moves the result out.
  The runtime's side lives in a new `runtime/wyl/threads.h` beside `object.h`, or in
  `runtime/wyl/intrinsics.h` if it is small.
- A thread's state is initialized before its body runs and its buffer is collected after the body
  returns. `main`'s thread state already exists from its first allocation.
- A panic on any thread ends the program; the runtime already ends at `RaisePanic`.
- If the compiler needs a hook to the type, add it to `StdlibSymbols`. If the language's rules name
  `Thread`, add it to the spec's Appendix A. Otherwise keep it an ordinary library type.

## Step 4: the interpreter

- Choose how the interpreter runs a thread and record that answer with the `Thread` decision. A
  deterministic model is enough for this step: `spawn` runs the body to completion where it is
  called, in a fresh cycle buffer. Every way the parent and child can observe each other is output
  order or a sendable value, and this step has no type through which one sees the other's progress,
  so running the child at `spawn` is one valid schedule.
- Give the nested run its own cycle buffer and free the cycles left in that buffer when the run
  ends. Share the output writer and the creation counter with the parent.
- Add the `spawn` and `join` handlers to `Interpreter.Intrinsics`, keyed by the C++ name the
  compiler gives the extern functions, so `interpreter/registry` finds them.

## Step 5: tests

- A thread captures a sendable value and returns an integer; `join` returns the integer.
- A unique capture moves into the thread, and its destructor runs on that thread.
- A unique result is made on the thread and released on the caller after `join`, which frees it
  through the pool of a thread that has ended.
- `main` releases a `Thread` it still holds while its body has finished, and the join happens before
  `main` returns.
- A panic on a thread ends the program, with `// panic:`.
- A thread leaves a cycle in its buffer, and that cycle is freed when the thread ends: the
  destructor prints from the thread that ends.
- The tests synchronize before every line either thread could print: `join` is this step's only
  synchronization. Do not print from both threads without joining first, because stdout order is
  the one thing the spec lets vary (`introduction.observable`).
- Run each test in the interpreter and in the native modes.

## Still open

- Channels and `Mutex` with the one-lock check, `Atomic`, `Frozen`, and one parallel loop (0016).
- `Isolated<T>` and its region buffer (0017).
- The `sendable` rows for `std`'s `unsafe` floor types (0036, #14).
- A non-escaping `shared fn` handed to another thread, which only `unsafe` code may do, and which
  the thread library checks when threads exist.
- The lifetime gaps in #20: a destructor that spawns a thread capturing `this`, a thread blocked
  forever when its `Thread` is released, and static initialization across threads.
- The in-use bit is not atomic (0049); recheck it against a value that another thread releases.
