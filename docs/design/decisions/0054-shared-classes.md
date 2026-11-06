# 0054. Shared classes: a count for any thread, the global heap, and no collection

- Status: accepted (2026-11-05)
- Spec: `concurrency.shared-classes`, `concurrency.sendable`, `memory.cycles`, `structs.methods.mut`

## Decision

- A class declared `shared` is marked once by the binder (`TypeSymbol.IsShared`), after the binder
  has bound supertypes and rejected inheritance cycles. The mark walks in both directions: a shared
  class's base class and every class extending a shared class are shared too. The binder, the cycle
  analysis, the emitter, and the interpreter read that one mark.
- A shared class's object has a count of its own that any thread may change, and its last release
  destroys it on that thread. A normal object keeps its non-atomic count. `wyl::Object`'s `Retain`
  and `Release` are virtual, so a reference to an interface implemented by a shared class counts the
  object the shared way.
- A shared object comes from the global heap, never from the program's pool, since the thread that
  frees it need not be the thread that allocated it.
- The weak block shared by an object and its weak references counts atomically and comes from the
  global heap. A weak reference to a shared object may be copied, read, and released on any thread.
- `String`'s block count is atomic in every program, because a shared class's field may hold a
  `String`.
- The cycle collector never buffers or visits a shared object. `CyclicClasses` leaves shared classes
  out of the graph whose strongly connected components are cycles. The emitter and the interpreter
  say that a shared class's objects are not cyclic. A shared class stays in the analysis's list of
  classes a release may destroy, since releasing a shared class's last reference runs the
  destructor. `StackSlots` never puts a shared object on the stack, because a shared object's
  release deletes it.
- `Weak<T>` is sendable when `T` is a shared class through a compiler fact: the one type the
  standard library declares as `Weak` is marked when it is registered, and `TypeSendable` reads its
  type argument's class. How `std`'s `unsafe` floor declares any type sendable more generally stays
  open in issue #6.
- A shared class does not declare a `mut` method, because no class method may be `mut`. The fields
  a method could change are the `mut` fields, and a shared class has none.

## Alternatives

- **Make every object's count atomic.** A thread-confined object never needs an atomic count, and
  every copy of a reference would pay for a shared class's synchronization.
- **A separate reference type for shared classes.** A shared class may implement an interface, and a
  reference to that interface may refer to its object, so the count's form must follow the object,
  not the static type of the reference.
- **The program's pool for shared objects.** A pool belongs to the one thread that allocates from
  it, and the last release may run on another thread.
- **Buffering a shared object that is part of a cycle.** Other threads may refer to it, so a
  collection that found it unreachable on one buffer would free an object another thread still uses.
  A cycle among shared objects is broken with `Weak<T>`; otherwise it leaks
  (`concurrency.shared-classes`).
- **A `mut` method on a shared class.** The method would change what another thread reads without a
  synchronization type, which is the data race the threading model exists to prevent.

## Why

The threading model makes data races impossible (0015). A shared object is the one object several
threads reach without a synchronization type, so its lifetime must be safe on every thread, and
nothing about it may depend on the thread that made it. Keeping the normal count non-atomic and the
normal objects in the pool keeps the cost of sharing on the declarations that ask for it.
