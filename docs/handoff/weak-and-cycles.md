# Weak references and the cycle collector

Goal: replace the `weak` field modifier with the standard library value type `Weak<T>`, and add a
cycle collector that runs only when the program calls `Memory.collectCycles`. The design is in
[03-memory-model.md](../spec/03-memory-model.md#weak-references-weakt) (§3.2, "Weak references" and
"Cycles"); this file is the plan for building it.

## 1. `Weak<T>`

- **Runtime** (`runtime/vpl.h`): replace `Object`'s weak list head with a pointer to a weak block
  `{ Object* target; uintptr_t count; }`, allocated the first time the object is referred to weakly.
  The object holds one count on its block. When the object's count reaches zero, set
  `block->target = nullptr` before the destructor runs (as `clear_weak` does now), then drop the
  object's count on the block. `vpl::Weak<T>` becomes one pointer to the block, with copy and move
  adjusting the block's count. Moving no longer needs relinking, so weak references can live in
  `List` storage.
- **Standard library**: `Weak.vpl` declares `struct Weak<T>` with `get()` returning `T?`, built on
  `extern` functions implemented in `vpl.h`. Add `Weak` to `StdlibSymbols`.
- **Compiler hooks**: an implicit conversion from `T` and `T?` to `Weak<T>` (next to the span
  conversions in `Binder.Spans.cs`), and a check that `T` is a non-null class or interface type.
  `Weak<T>` is defaultable.
- **Remove `weak`**: the `Modifiers.Weak` modifier, `FieldSymbol.IsWeak` and the checks built on it
  (weak fields must be nullable, can't be passed by `ref`, reads give `T?`), and the emitter's weak
  field handling (`StoreText`, field declarations). Rewrite the existing `weak` tests with `Weak<T>`,
  and add tests for weak locals, parameters, struct fields, and `List<Weak<T>>` across growth.

## 2. Which classes may be cyclic

A new `Semantics/CyclicClasses.cs`, run on the whole program like `ElementAliasing`: build a graph
from each class to every class its strong references may point to (through struct, enum, and fixed
array fields; through `Spannable` containers' element types; counting subclasses and interface
implementations), and mark the classes on a cycle. `@Acyclic` (the second annotation after
`@CallerLocation`) removes a class. Generic classes are decided per instantiation, since that is
what the emitter generates (`List<Node>` is cyclic, `List<int>` isn't).

Tests: emitted code isn't checked by tests, so test the analysis through its effect on
`Memory.cycleStats()` (cyclic objects are buffered, acyclic ones never are).

## 3. Buffering and collecting

- **Emitter**: each cyclic class sets a header flag in its constructor and gets a generated
  `vpl_visit` override that calls a visitor on each strong reference it holds, including inside
  struct, enum, and fixed array fields. A class implementing `Spannable` visits its elements through
  `asReadOnlySpan()`, which covers `List` and `Array`. References the visitor misses, such as ones a
  user's unsafe code keeps behind raw pointers, count as references from outside, so the collector
  only frees less, never something still in use.
- **Runtime**: in `Object::release`, when the count stays above zero and the object's cyclic flag is
  set and its buffered flag isn't, append it to the thread's buffer. Flags go in the high bits of
  the count. `collectCycles` runs trial deletion (Bacon and Rajan's synchronous algorithm) from the
  buffer, then destroys each cycle found as the spec describes: clear weak references, run every
  destructor, check none was resurrected (panic otherwise), release fields, free.
- **Budget**: `collectCycles(maxVisits)` counts visited objects. A buffered object whose group would
  exceed the remaining budget has its marks undone and goes back in the buffer.
- **Standard library**: `Memory.collectCycles()`, `Memory.collectCycles(int)`, `Memory.cycleStats()`
  (a struct of counts; per-class counts and timing in debug builds).

Tests: a two-object cycle freed by `collectCycles` (destructor output shows when), a cycle through
a `List`, a cycle still referenced from a local (not freed), destructors seeing intact neighbors,
the resurrection panic, a budget leaving work for the next call, weak references to a collected
object reading null, and a program that never calls the collector (nothing freed, as before).

## 4. Diagnostics

Debug builds only: warn when the buffer grows across consecutive calls, when one class is freed by
the collector repeatedly, and at exit list uncollected cycles by class (trial deletion without
destroying anything). These are the first runtime performance warnings
([README](../spec/README.md#not-yet-designed)).

## Order of work

1, then 2, 3, 4. Each step lands with tests, passes on MSVC and g++, and updates the spec's
"Implemented so far" notes.
