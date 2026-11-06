# 0058. A standard library type the compiler knows is sendable

- Status: accepted (2026-11-06)
- Spec: `concurrency.sendable`, `concurrency.shared-classes`, `library.channel`

## Decision

`std::channel` declares `package extern struct SyncState<T extends sendable>`. A `SyncState` is a
handle to the runtime's channel state: a copy shares that state, and the runtime frees the state
when the last copy goes.

- `StdlibSymbols.Register` marks the type when it registers the standard library's declarations
  (`TypeSymbol.IsSendableHandle`).
- `TypeSendable` answers that a value of a marked type is sendable, before its rule that an `extern
  struct` is not.
- `SyncState` is package-visible in `std::channel`, so a program cannot name it, declare a field of
  it, or turn another `extern struct` into a sendable one.
- `SyncState<T>` requires `T extends sendable`, so the binder checks the type argument at every
  instantiation, and a marked `SyncState` cannot carry a normal object.
- A `shared class` may hold a `SyncState` field, since the runtime owns what the handle holds. That
  is what lets `Channel<T>`, `Sender<T>`, and `Receiver<T>` be shared classes.

## Alternatives

- **An annotation that declares a type sendable.** The annotation would be a second spelling of a
  property the compiler computes from the fields, and a program could apply it to any `extern
  struct`, which is the one kind of type the field rules cannot inspect.
- **A global runtime table keyed by an `int` id.** The language would stay untouched, but every
  synchronization type would pay a lookup, and the table would answer the lifetime question a second
  time.
- **A `byte*` handle, as `Thread<T>` uses.** A pointer is not sendable, and a `shared class` must
  hold only sendable and copyable fields ([0054](0054-shared-classes.md)).
- **A `shared class` for the state.** The state is not a Wylow value and has no fields a program
  reads; sharing a Wylow object would make the runtime's state answer to the language's object
  rules, which it does not.

## Why

A channel's ends hold storage that belongs to no thread: the queue, a mutex, and counts of the ends.
The types that hold that storage must be `shared class`es, because one thread sends and another
receives through them, and a shared class's fields must be sendable and copyable. The compiler
cannot see inside an `extern struct`, so it must learn sendability for that one type from the
standard library that declares it.

Marking one type by name keeps the exception as narrow as `Weak<T>`'s sendability
([0054](0054-shared-classes.md)). The type is package-visible, so the exception cannot spread to a
program's own type, and the sendable bound on its type argument keeps the marked type from carrying
a normal object into a shared class.
