# 0061. An isolated graph's cycle buffer

- Status: accepted (2026-11-06)
- Spec: `concurrency.isolated`, `memory.cycles`

## Decision

- `wyl::CycleBuffer` in `runtime/wyl/cycles.h` holds the buffered objects and the holes they left.
  `wyl::CycleCollector::SwapBuffer` exchanges the collector's buffer and hole count with a graph's,
  and `MergeBuffer` adds a graph's objects to the thread's buffer and renumbers each object's slot.
- `wyl::IsolatedState` owns a `CycleBuffer` and merges it in its destructor.
- The interpreter's `GraphBuffer` holds a buffer list, a capacity, and a hole count.
  `Interpreter.SwapGraph` exchanges those three with the thread's `Interpreter.ThreadState`, and
  `ReleasingIsolated` merges the graph's objects into the thread's buffer.
- `std::isolated`'s constructor and `open` run their lambda between two swaps, so the graph's buffer
  is the thread's while the lambda runs. `take` does not merge: the box's destruction does, so a box
  dropped without `take` returns its buffer to the thread as well.
- The collector's figures, its destroying counter, and its scratch lists stay with the thread. A
  collection inside `open` therefore collects the graph alone, and `cycleStats()` there reports the
  thread's figures, which include what the graph's collection did. The spec makes the collector's
  figures per thread, and the graph's code runs on the thread.

## Alternatives

- **A collector for each graph.** The graph's objects come from the thread's pools and the graph's
  code runs on the thread, so a second collector would need its own allocation policy and its own
  end-of-thread rule, for a buffer that is used by one thread at a time anyway.
- **Merging in `take` alone.** A box dropped without `take` would then lose its buffer's objects,
  and the collector would never see them.
- **Merging when the box moves to another thread.** A move already carries the buffer inside the
  box, and the moving thread has nothing of the graph to release, so a merge there would renumber
  slots for no object.
- **Reporting the graph's figures separately.** The spec gives the collector one set of figures per
  thread, and a second set would make `cycleStats()` answer two questions.

## Why

A graph's buffer must move with the graph, since each object of the graph numbers a slot in it. It
must join the releasing thread's buffer at the moment the box ends, because only that thread can
collect the graph's leftovers. Making the state's destruction the one merge covers a box that
leaves scope and a box whose value was taken, and no code in `take` has to remember either case.
