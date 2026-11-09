# Next: close the gaps the concurrency work left open

Repo `E:\Repos\Wylow\wylow`, branch `dev`. The synchronization types are done: threads (0056),
channels (0059), `Isolated<T>` (0060–0063), the module `std::threading` (0066), `Mutex<T>` with the
one-lock check (0067, 0068), `Atomic<T>` and `AtomicBool` (0069), and `Frozen<T>` (0070). Read those
decisions, the spec chapter `concurrency`, the section `library.threading`, and the "Threads"
section of `docs/design/runtime.md`.

## Before you start

- Check that CI passed on the three commits `afc6262`, `8fd9cd8`, and `c625bcf`. The commits ran
  the interpreter suite and clang on Windows. `atomic/two_threads` and `frozen/static_table` carry
  `// tsan` and have not run under TSan yet. MSVC and GCC built the atomic tests, but not the frozen
  ones.
- Run the mutation target `interpreter-atomic` on CI (`gh workflow run mutation.yml -f
  target=interpreter-atomic`) and triage what survives.

## What changed in the last session

- `Atomic<T>` accepts only the ten integer types, which the binder checks through
  `TypeSymbol.IsAtomic` (`WYL0479`). Every operation is sequentially consistent, and `add` and
  `subtract` panic on overflow in a debug build.
- `Frozen<T>` is plain `unsafe` Wylow over `FrozenStorage<T>`, a struct of a pointer and a length
  that `StdlibSymbols` marks as a sendable handle. `TypeSendable.IsSendableIn` now answers for a
  marked type before it walks the type's fields.
- The agreed API named the element reader `get`. `Frozen` has an indexer that panics and a `get`
  that returns null, both returning `in T`, because `List` and `ReadOnlySpan` already use `get` for
  the version that returns null.
- A `where` clause now covers its member's signature as well as its body (`CheckSignatureIn`),
  since `List.freeze` returns `Frozen<T>`, which only its clause makes valid.
- The fuzzer has a `statement-lock` mutation, which runs a statement inside a mutex's lock.

## The work

- **The lifetime gaps.** Decide, test, and specify each one: a destructor that spawns a thread that
  captures `this`; releasing a `Thread` whose body waits forever; static initialization across
  threads; a `Sender` that stays inside a cycle.
- **`createMutex`'s borrow check.** No test reaches the check for a value that something refers
  into. The constructor takes a box, and the flow analysis refuses to move a value while something
  refers into it, so the check may be unreachable. If it is, say so in the spec, and keep the check
  only as a runtime guard.
- **Mutation targets.** `Interpreter.Channels.cs` and `Interpreter.Isolated.cs` have none. Add them,
  as `interpreter-mutex` and `interpreter-atomic` were added.
- **The interpreter's state types.** `Interpreter.DefaultValue` and `Values.Zeroed` each test
  `SyncState`, `MutexState`, and `AtomicState` in turn. Give the question one answer in one place.

## Still open

- The parallel loop is deferred until a program needs it. A shared lambda may capture a
  `ReadOnlySpan` of the data the loop's threads write, so its design must rule out that race.
- `Mutex.lockBoth` (two locks in a fixed order) stays open until a program needs it.
- A thread that a foreign library started ends its state through a `thread_local` guard. On MinGW
  that guard runs after thread-local storage is torn down, so the state leaks there. Callbacks from
  foreign threads arrive with interop.
