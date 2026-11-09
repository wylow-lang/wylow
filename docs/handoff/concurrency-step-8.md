# Next: `Atomic<T>`, `Frozen<T>`, and one parallel loop

Repo `E:\Repos\Wylow\wylow`, branch `dev`. Threads (0056), an interpreter that interleaves (0057),
the sendable floor (0058), channels (0059), `Isolated<T>` (0060–0063), the `std::threading` module
(0066), and `Mutex<T>` with the one-lock check (0067, 0068) are done. Read decisions 0015, 0016,
0049, 0054, 0055, 0062, 0066, 0067, and 0068, the spec sections `concurrency.sendable`,
`concurrency.shared-classes`, `concurrency.shared-functions`, `concurrency.isolated`,
`concurrency.mutex`, `library.threading`, and `variables.statics`, the "Threads" section of
`docs/design/runtime.md`, and `docs/design/testing.md` (the modes and the `// tsan` marker).

## Before you start

- Ask the user whether CI passed on the commits from `8496b4e` to the handoff's commit, and fix any
  failure first. The TSan mode, GCC, and MSVC did not run locally. The TSan mode runs
  `mutex/two_threads`, `mutex/join_while_locked`, `mutex/release_while_locked`, and
  `mutex/unique_value` for the first time.
- `Mutex` has no fuzzer mutation. Decide whether one can cross it, for example a mutation that moves
  a `receive` or a `join` into a `lock` lambda, and add it or say why none can.

## What is done

- Every threading type is in `std::threading`, one file per type, and the runtime declares their
  extern functions in `wyl::threading` (0066).
- `Mutex<T>` holds an isolated graph, so `T` has no bound (0067). `lock` has `Isolated.open`'s shape.
  The `Mutex` object owns its `wyl::threading::MutexData<T>` and frees it in its destructor.
- A per-thread flag, `HoldsLock`, records a held lock in `wyl::ThreadState` and in
  `Interpreter.ThreadState`. `lock`, `receive`, `join`, and a `Thread`'s release panic when it is set
  (0068).
- A sendable value now converts to the box's content type before it moves into an `Isolated<T>`, so
  `null` and a widened constant box correctly. `takeValue` no longer leaves the in-use mark on the
  value it moves out.

## The work

- **`Atomic<T>`.** The memory ordering is the decision to make. One answer is that every operation is
  sequentially consistent. Any weaker model must state which operations pair. Decide which `T` an
  atomic accepts (the primitive types, or any sendable copyable type of a size the hardware supports),
  and its API (`get`, `set`, `exchange`, `compareExchange`, and the arithmetic operations). `Atomic`
  is a shared class, as `Mutex` is, so its storage is a runtime handle again. Recheck the in-use bit
  (0049) against it.
- **`Frozen<T>`.** An immutable value computed once. Decide whether it wraps an `Isolated<T>` or the
  value itself, what `get` returns, and how its computation relates to the one-lock rule if a second
  thread asks while the first computes.
- **One parallel loop** built on a non-escaping `shared fn` (0016, `concurrency.shared-functions`):
  the function that runs it hands each thread its own part, and returns only when every thread is
  done. It is library code, and it is a wait, so it must read `HoldsLock` as every other wait does.

## Still open

- `Mutex.lockBoth` (two locks in a fixed order) stays open until a program needs it.
- The lifetime gaps: a destructor that spawns a thread capturing `this`; releasing a `Thread` whose
  body waits forever; static initialization across threads; a `Sender` that stays inside a cycle.
- A thread that a foreign library started ends its state through a `thread_local` guard. On MinGW
  that guard runs after thread-local storage is torn down, so the state leaks there. Callbacks from
  foreign threads arrive with interop.
- No test reaches `createMutex`'s check for a value that something refers into. The constructor
  takes a box, and the flow analysis refuses to move a value while something refers into it, so the
  check may be unreachable. If it is, say so in the spec, and keep the check only as a runtime
  guard.
- `Interpreter.Channels.cs` and `Interpreter.Isolated.cs` have no mutation target.
  `Interpreter.Mutex.cs` has one, `interpreter-mutex`.
