# 0057. The interpreter runs the operating system's threads

- Status: accepted (2026-11-06)
- Spec: `concurrency.interleaving`, `concurrency.panics`, `concurrency.confinement`,
  `programs.execution`, `memory.cycles`

## Decision

The interpreter runs each logical Wylow thread on an operating-system thread, and those threads run
concurrently. The interpreter has no schedule of its own: two runs of one program may print in
different orders, which is what `concurrency.interleaving` allows a compiled program to do.

- Each thread owns its cycle buffer, the collector's figures, its destroying counter, and its call
  depth (`Interpreter.ThreadState`). An object released on a thread enters that thread's buffer.
- The parts several threads reach are safe to reach at once. Reference counts and the creation
  counter are atomic, and the weak block changes under one lock. Standard output and standard error
  are written under one lock. The step budget is one atomic count for the whole run.
- `join` and the release of a `Thread` wait for the target thread to return. `receive` waits for a
  value or for the channel's close. A wait also ends when the run ends.
- A panic, or any other exception, on any thread ends the run. The interpreter records the first
  exception, wakes every wait, and rethrows the exception on `main`'s thread, which reports it as
  `wyl::RaisePanic` reports one.
- A thread collects its cycle buffer when its body returns.
- Returning from `main` ends the run. A thread still running stops at its next instruction or wait,
  and the interpreter joins it before `Run` returns.
- The tests of threads and channels synchronize before each line they print: with a `join`, with a
  `receive` of a value, or with a `receive` of null after the channel closes. A test never assumes
  an order the language does not promise.

## Alternatives

- **One thread at a time, with a baton that only the running thread holds.** Two runs of one program
  would interleave identically, which makes the interpreter a convenient oracle. The baton fixes a
  schedule that no compiled program has, and a test can come to depend on it. Such a test passes in
  the suite and fails on the operating system, which is the failure this interpreter exists to
  find.
- **Running a body to completion at `spawn`, as 0056 did.** The interpreter then has one schedule
  too, and no program with a channel can run: a parent that receives a value its child sends waits
  for a thread that never starts.
- **A quantum of steps that hands the baton on.** The quantum would make the schedule depend on how
  many instructions the interpreter charges, which is an implementation detail. It would also stop
  a spin only at a point no language rule names.
- **Using the compiler's pools and cycle buffers for the interpreter's objects.** The interpreter
  has its own object model, so it has its own counts, locks, and buffers.

## Why

The interpreter answers what a program does, and a threaded program does not promise an order for
its output. An oracle that invents one answers a question the language leaves open. When the suite
compares a run to expected output, the program must synchronize first, and what it then prints is a
property of the program rather than of a schedule: the value that arrived, the null after the close,
the destructor that ran on the receiving thread. Those properties hold under every schedule, so
they are the ones a test states.

Concurrency also changes what the interpreter must protect. Reference counting is atomic because a
shared object's last release may run on any thread. The cycle buffers stay per thread because a
normal object belongs to one thread, and a shared object is never buffered
([0054](0054-shared-classes.md)). The output lock keeps one `print` from interleaving with another,
so a run that synchronizes still writes each whole line.
