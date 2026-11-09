# 0068. The lock rule is one flag per thread, which every wait reads

- Status: accepted (2026-11-09); implements the check of [0016](0016-channels-and-locks.md)
- Spec: `concurrency.mutex`, `library.thread`, `library.channel`, `library.mutex`

## Decision

- Each thread has one flag that says whether it holds a lock: `HoldsLock` in `wyl::ThreadState` and
  in the interpreter's `Interpreter.ThreadState`. `lock` sets the flag once it has the operating
  system's mutex and clears it after the lambda returns.
- `lock` panics with "cannot lock a mutex while this thread holds a lock" when the flag is set. The
  check runs before the wait, so locking the same mutex twice panics instead of waiting forever.
- Every wait reads the flag. `receive` panics with "cannot receive from a channel while this thread
  holds a lock". `join` and the release of a `Thread` panic with "cannot wait for a thread while this
  thread holds a lock".
- `lock`, `receive`, and `join` are `@CallerLocation`, so each panic names the program's call. The
  release of a `Thread` runs in its destructor, which has no call site, so its panic reports
  `<runtime>`. `join`'s panic for a thread that was never started now names the call as well.
- The checks run in every build mode.
- The interpreter's lock is a `Semaphore`, which a thread waits for together with the run's end, as a
  `receive` waits for a value together with the run's end.

## Alternatives

- **A count of the locks a thread holds.** The rule allows one lock, so a count would only ever be 0
  or 1.
- **Checking only in debug builds.** A program that deadlocks once in a while in release builds is the
  failure 0016 exists to prevent, and the check is one load and one branch per wait.
- **A lock that the same thread may take again.** Java's
  [`ReentrantLock`](https://docs.oracle.com/en/java/javase/21/docs/api/java.base/java/util/concurrent/locks/ReentrantLock.html)
  allows this. A thread that locks a second mutex inside the first still breaks the rule, and the
  two cases would need two checks.
- **A message that names the wait as a deadlock.** The program has not deadlocked when the check
  fires, and another schedule might never have deadlocked. The message states the rule the call
  broke.

## Why

0016 makes a deadlock between locks impossible by ruling out its two shapes: a thread that holds one
lock and waits for another, and a thread that holds a lock and waits for another thread. Both shapes
are a wait while holding a lock, so one flag read at every wait enforces the whole rule. Because the
flag is per thread and a lock is released before `lock` returns, the check never depends on another
thread, and it gives the same answer under every schedule.
