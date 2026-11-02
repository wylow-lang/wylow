# 0015. Threading model: confinement, one sendable fact, no `static mut`

- Status: accepted (2026-11-01)
- Spec: `concurrency`, `variables.statics`

## Decision

A normal object belongs to one thread. Whether a type may reach another thread is one inferred
fact, *sendable*; a copyable sendable value is shared, a unique one is moved. Statics are read-only
and sendable, and there is no `static mut`. `shared class` and `shared fn` are the only ways objects
and functions are used by several threads. A panic on any thread ends the program. Threads,
channels, locks, and atomics are the standard library's, not the language's.

## Alternatives

- **Two inferred facts** (Rust's `Send` and `Sync`).
- **`static mut`** (Java's mutable statics): one copy shared by every thread races. **Thread-local
  statics**: a copy per thread silently gives each thread its own version.
- **Isolates with recoverable panics**, jobs as a language feature: considered in the first threading
  design and cut.

## Why

Data races are undefined behavior in C++, so safe Wylow must make them impossible. With unique
types, whether a sendable value is shared or moved follows from whether it can be copied, so one
fact is enough. Without mutable statics, a function can reach only what it is given, which is what
makes isolated object graphs sound ([0017](0017-isolated-graphs.md)).
