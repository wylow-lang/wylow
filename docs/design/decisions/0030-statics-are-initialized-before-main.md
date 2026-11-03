# 0030. Statics are initialized before `main`

- Status: accepted (2026-11-02); replaces "initialized the first time it is read" in 5.5
- Spec: `variables.statics`, `programs.execution`

## Decision

Before `main` runs, the main thread initializes every static of the program: those of its own
package and of every package it depends on, directly or not, `std` included.

- **Order.** A package's statics come after those of every package it depends on; packages neither
  depends on come in order of name. Within a package, files come in order of their path under
  `src/`, and within a file, statics come in the order they are written.
- **On demand.** An initializer that reads a static not yet initialized initializes that one
  first, so a static is never read before its initializer has run.
- **Cycles.** Reading a static whose initializer is still running panics, naming the statics
  involved.
- **No threads.** Starting a thread before `main` begins panics.
- A panic in an initializer ends the program before `main` begins, like any panic.

## Alternatives

- **Initializing a static the first time it is read**, on whichever thread reads it (the rule this
  replaces). Thread 1 can initialize `A`, which reads `B`, while thread 2 initializes `B`, which
  reads `A`; each waits for the other. Panicking on the wait that would close the cycle makes
  which thread panics depend on scheduling, and §5.6 states that the order of output is the only
  thing threads make vary. A read that waits for another thread's initializer is also a wait, which
  the one-lock rule forbids inside a lock's lambda, so whether a `lock` panicked would depend on
  scheduling too.
- **Checking the dependencies between initializers at compile time.** It needs a call graph of the
  whole program, which no language rule may depend on (CLAUDE.md, mandatory passes).
- **Initializing before `main` and allowing threads.** An initializer that spawns a thread and
  joins it brings the cross-thread wait back: the new thread may read a static that the main
  thread hasn't initialized yet. Panicking only when that read happens would depend on scheduling.
- **Restricting initializers to constant expressions.** Tables computed by a function, such as
  `makeSineTable()`, are what statics are for.

## Why

Initializing on one thread before any other thread exists makes the order of initializers, their
output, and any cycle panic the same in every run. After `main` begins, every static has its
value, so reading one never waits and never runs code.

What it costs: an initializer runs even when the program never reads its static, so its output, or
its panic, happens in a program that doesn't use it, and every program initializes `std`'s statics.
Starting a thread is forbidden in initializers, so a table can't be computed in parallel before
`main`; a program that needs that computes it in `main`.

This removes the reason [0029](0029-statics-are-not-destroyed.md) and
[0028](0028-destructors-on-unique-structs.md) gave against destroying statics in reverse order:
the order initializations finish in is now fixed. 0029's decision stands on its other reasons:
threads still running at the end of `main` may read statics, and stopping them first may leave a
lock held or a value half changed.

## Open

- Whether a generic type may declare a static, and if so whether each instantiation has its own.
- Static fields are not implemented (#6), so this is not yet tested.
