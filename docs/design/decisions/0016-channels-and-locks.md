# 0016. Channels and locks

- Status: accepted (2026-11-01)
- Spec: the standard library's threading types (`concurrency` until they are documented there)

## Decision

A channel closes when its last `Sender` is released and has no size limit, so `send` never waits.
A thread holds at most one lock and waits for nothing while holding it, a check that runs every
time. `freeze()` doesn't depend on reference counts.

## Alternatives

- **Go's channels**: closed by hand, and "sending to or closing a closed channel causes a run-time
  panic" ([Close](https://go.dev/ref/spec#Close)); unbuffered unless given a capacity
  ([Channel types](https://go.dev/ref/spec#Channel_types)), so a send waits for a receiver.
- **Bounded channels and `select`**: left open; `select`'s random choice among ready channels would
  make runs differ.
- **Taking several locks in a fixed order** (`Mutex.lockBoth`): left open until a program needs it.
- **Freezing only an unshared list**: whether the optimizer removed a count change would decide it.

## Why

Every deadlock between locks needs a thread holding one lock while waiting for another, or waiting
for another thread while holding one; the one-lock rule makes those impossible, and since the check
runs every time, the mistake fails in the first test instead of freezing the program once in a
while. Behavior that doesn't depend on reference counts gives the same answer in every build.
