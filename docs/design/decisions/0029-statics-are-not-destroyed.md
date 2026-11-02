# 0029. Statics are not destroyed at the end of the program

- Status: accepted (2026-11-02)
- Spec: `programs.execution`, `variables.statics`

## Decision

When `main` returns, its locals and temporaries are released as at the end of any function, and the
program ends. No static is destroyed, no object that only a static refers to is released, and the
cycle collector doesn't run. A thread still running stops where it is, running no destructor and no
deferred statement; a `Thread` that `main` holds is joined when released, before `main` returns. A
panic runs nothing (`introduction.panics`, unchanged).

A resource that must be closed belongs to a local of `main` or of something `main` owns, not to a
static. A unique value can't be a static (5.5), but a `Mutex<T>` static may hold one, and then its
destructor never runs. A program that needs it closed takes the value out of the `Mutex` and
releases it before `main` returns.

## Alternatives

- **Destroying statics in reverse order of initialization.** A static is initialized the first time
  it is read, possibly by different threads, so the order initializations finish in follows how the
  threads interleaved. The order is fixed only for a program with one thread.
- **Destroying statics after stopping every other thread.** A thread stopped at an arbitrary point
  may hold a lock or leave a value half changed, so a destructor that locks it would deadlock or see
  that state.
- **Destroying statics while other threads run.** A thread reading a static that was just destroyed
  is a use after free in safe code.
- **Waiting for every thread to finish before the program ends.** A thread blocked on a channel or
  lock nobody will release would keep the program from ending.

## Why

The only program-end behavior that is defined without ordering threads or stopping them at
arbitrary points is to leave statics alone. What is lost is cleanup with effects outside the
process, such as flushing a buffer, and the operating system reclaims the process's memory and
descriptors. A program that needs the cleanup has a defined place to do it, the end of `main`.

## Open

- There is no function that ends the program early. If one is added, it should run nothing, as a
  panic does.
- A process-wide hook that runs on a panic (for restoring a terminal, say) is not designed.
