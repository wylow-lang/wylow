# 0055. Shared functions: four forms, shared closures, and no collection

- Status: accepted (2026-11-05)
- Spec: `concurrency.shared-functions`, `concurrency.sendable`, `functions.lambdas.captures`,
  `conversions.functions`

## Decision

- A function type has four forms, interned by reference: escaping or not, and `shared` or not. A
  function type written anywhere is escaping; a parameter or local is non-escaping unless it is
  declared `escaping`, and `shared` changes neither of those rules.
- A function value converts to a function type that drops `escaping`, drops `shared`, or drops
  both. No conversion adds either. The forms' parameters must be the same types.
- Only an escaping `shared fn` is sendable. A non-escaping `shared fn` is a second-class value,
  as any non-escaping function is.
- A shared lambda copies its captures only when it is escaping; a non-escaping shared lambda refers
  to them in place. Every capture of a shared lambda is sendable and copyable. A capture of a
  non-escaping shared lambda is also read-only: the lambda cannot capture a `mut` local, a `ref`,
  or a view other than a `ReadOnlySpan`. A shared lambda may capture `this` only in a shared
  class's method.
- The closure of an escaping shared lambda is `wyl::SharedClosure`, which derives from
  `wyl::SharedObject`: its atomic count, the global heap, and destruction at its last release on
  whichever thread drops it. A shared lambda that captures nothing owns no closure.
- The cycle collector never buffers, visits, or frees a shared closure: `CyclicClasses` leaves it
  out of the cycle graph, and the interpreter marks its `ClosureValue` not cyclic. A cycle through
  a shared closure leaks, as a cycle among shared objects leaks
  ([0054](0054-shared-classes.md)).
- `StackSlots` never puts a shared closure in a slot, because a slot's end destroys what it holds
  and a shared object's last release may be on another thread.
- The code a shared closure calls reaches its captures through the most-derived pointer, which it
  takes with `dynamic_cast`, because `SharedObject` derives virtually from `Object` and a static
  cast cannot go down a virtual base.

## Alternatives

- **A separate reference type for shared function values.** A function value is already a
  reference to an object that holds its captures, and a `Ref<Object>` calls the object's own
  `Retain` and `Release`, so a shared closure needs no second value representation.
- **Collecting a shared closure on the thread that drops it.** Another thread may still hold the
  closure, and the collector sees only the buffers of the thread that runs it, so it would free an
  object another thread uses.
- **A stack slot for a shared closure.** The slot's end destroys its object, and a shared object's
  last release may be on another thread, after the frame is gone.
- **Copying a shared lambda's captures into its own copy for each thread.** The lambda is made
  once, before any thread runs it, and the threads that run it share the copies.

## Why

The threading model makes data races impossible ([0015](0015-threading-model.md)), and a value
that reaches another thread must belong to no thread. A shared closure is the one closure several
threads may release, so its lifetime must work on every thread, which is what `SharedObject`
provides for a shared class's object. Escaping and `shared` are independent questions: a thread's
body outlives the call that makes it and so is escaping, while a parallel loop's body must finish
before the call it was given to returns and so may be non-escaping. The captures are the values
another thread reads, so they follow the rule of a static and of a shared class's field: sendable
and copyable. A reference to a non-escaping shared lambda lives in the caller's frame, so it is a
second-class value and is not sendable.
