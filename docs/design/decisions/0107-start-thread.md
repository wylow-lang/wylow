# 0107. A thread starts with `threading::startThread`, not a free `spawn`

- Status: accepted (2026-11-13)
- Spec: `library.thread`

## Decision

- `std::threading` declares
  `startThread<T extends sendable>(escaping shared fn() -> T body) -> Thread<T>` in place of
  `spawn`.
- `Thread<T>` and `join` do not change.
- A program writes `threading::startThread(() -> 42)`. The module qualifier says which module starts
  the work, and the name says what it starts.
- A job system that `std::threading` gains later names its own entry point, such as `startJob`, so
  no call says only `spawn`.

## Alternatives

- **`Thread.spawn`.** The name would say that it starts a thread. A static method of a generic type
  needs the type's arguments written, so the call is `Thread<int>.spawn(...)`, and the result type
  appears at every call. Making `Thread.spawn(...)` infer `T` needs a new rule that a static call on
  a generic type infers the type's arguments; that rule would change how every static call reads and
  would touch the binder's inference. A naming change does not justify that rule.
- **`Threads.spawn`.** A non-generic type whose static method declares `T` infers the result type
  today. The type would exist only to hold the method, and its name reads like a collection of
  threads.
- **`threading::spawnThread`.** It would keep the word `spawn`. The call would still read as
  spawning something whose kind is in the suffix, while `startThread` reads as the action.
- **Keeping `spawn`.** A later `spawn` for another kind of work, such as a job, would make
  `threading::spawn` say nothing about what it starts.

## Why

A module holds more than one kind of work as the standard library grows: a thread now, a job or a
task later. The function a program calls should name the kind. `startThread` names it and keeps the
result type inferred from the body. The alternative names either write the result type at every
call or add a type that exists only as a namespace.
