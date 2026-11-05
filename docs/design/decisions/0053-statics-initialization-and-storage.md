# 0053. Statics: one order, one initializer, storage never destroyed

- Status: accepted (2026-11-05); settles the open questions of
  [0029](0029-statics-are-not-destroyed.md) and [0030](0030-statics-are-initialized-before-main.md)
  about how the two executors initialize and hold a static
- Spec: `variables.statics`, `programs.execution`, `concurrency.sendable`, `expressions.members`

## Decision

- A static is named through its type, `Settings.LIMIT`, and by its name alone inside the type. A
  value cannot name one (`expressions.members`).
- Every static of the program is one entry in one list, in the order `variables.statics` gives: a
  package's statics after those of the packages it depends on, files by their path under `src/`,
  and statics in the order a file writes them. Lowering builds the list, and the emitter and the
  interpreter both run that list and nothing else before `main`.
- Each entry has one initializer: a lowered function with no parameters that returns the static's
  value. A static an initializer does not mention starts as its type's default value, and a type
  with no default value is a compile-time error.
- Reading a static is a read of its storage, preceded by a call of the static's ensure function.
  The ensure runs the initializer when the static is not initialized, returns when it is, and
  panics, naming the chain of statics, when the initializer is still running. The call is part of
  the lowered program, with the read's location, so the two executors run the same rule and no pass
  moves a read across it. After initialization the ensure tests one state byte and returns.
- A static is never written after its initializer stores its value, and never destroyed.
- The emitted C++ holds each value in `wyl::StaticStorage<T>`: alignas-`T` raw bytes, a state
  byte, and the static's name. The ensure constructs the value in place with placement new. A
  function called before `main` runs every ensure in the list's order. `StaticStorage`'s destructor
  destroys nothing.
- The interpreter holds one cell per static for the run, initializes it in the same order with the
  same ensure rule, and releases no static at the end.

## Alternatives

- **A namespace-scope C++ variable, `T value = init();`.** Its destructor runs at exit, which
  [0029](0029-statics-are-not-destroyed.md) forbids, and a destructor with a side effect would run
  after `main` returned.
- **A function-local C++ `static T value = init();`.** The destructor runs at exit for the same
  reason, and the language's guarded initialization waits for another thread that is already
  initializing the same static, which [0030](0030-statics-are-initialized-before-main.md) replaces
  with a panic.
- **A new place base or operand kind for a static.** The place model, the access and effect
  walkers, the verifier, and the hold rules would each need a second base. A local whose role is
  `static` keeps every place rule unchanged and gives one storage name to both executors.
- **Computing the initialization order in the emitter and again in the interpreter.** The two
  would have to agree without a shared answer, which is the drift this document set exists to
  prevent.
- **Leaving the ensure out of a read once nothing can be initializing.** Finding which functions
  an initializer may run needs a whole-program call graph, which no mandatory pass may use
  (`CLAUDE.md`). An optimization may later drop an ensure that it can prove cannot reach an
  uninitialized static; that changes no program's behavior, so `--no-opt` may keep the call.

## Why

One list gives the emitter and the interpreter the same order, the same initializer functions, and
the same cycle panic, so a test that prints during initialization or panics in a cycle reads the
same in every mode. A state byte per static is what lets an initializer initialize a static it
reads and still detect a cycle without threads. Storage that is constructed in place and never
destroyed is the only representation in which both executors can keep 0029's promise.
