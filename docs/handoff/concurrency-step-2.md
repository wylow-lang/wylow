# Next: shared classes

Repo `E:\Repos\Wylow\wylow`, branch `dev`. The `sendable` property and statics are done (0036, 0053).
Read decisions 0015, 0016, 0036, and 0053, the spec sections `concurrency.sendable`,
`concurrency.shared-classes`, `memory.cycles`, and `types.copyable`, and the steps under
"Concurrency" in issue #6.

A `shared class` is the first type whose objects several threads may refer to. Threads, `Mutex`,
`Atomic`, and the standard library's other synchronization types are not implemented, so this step
settles the type, its sendability, its reference count, and its exclusion from the cycle collector.
The runtime still runs one thread.

## What step 1 built

- `TypeSendable.IsSendableIn(function)` is the one answer to whether a type is sendable. It reads
  the declaration, fields, elements, and type arguments. A class reference, a function value, a
  pointer, an extern struct, and a type parameter without a `sendable` bound are not sendable.
- `BindBounds` and `BindWhere` accept `sendable`; `FunctionSymbol.SendableParameters` and
  `TypeParameterSymbol.IsSendableBound` hold the bounds.
- A static is an `IrLocal` with the `static` role in `IrProgram.Statics`, in the spec's order, with
  one initializer function and one ensure call per static. `wyl::StaticStorage<T>` in
  `runtime/wyl/statics.h` holds a static's bytes and state, and the interpreter keeps one cell per
  static. WYL0463–WYL0468 are the new codes, and WYL0094 is retired.

## Step 1: the modifier and the sendable row

- Lex `shared` as a keyword: move it from `Lexer.ReservedKeywords` to the keyword table, add the
  token to `TokenKind`, and add `Modifiers.Shared` to the parser's modifier list.
- The parser accepts `shared` only on a class declaration. `shared` on a struct, enum, interface,
  or anything else is a compile-time error with a new code.
- The binder marks the class shared (`TypeSymbol.IsShared`). A class extending a shared class is
  shared too, and a shared class's base class is shared. Decide where that propagation runs, next
  to `BindSupertypes` and `CheckInheritanceCycles`, so both executors and the emitter read one
  answer.
- A shared class's field must not be `mut` and must have a type that is sendable and copyable. The
  error names the field that is not sendable, as WYL0464 does for a static. A new code, with its
  page and test, is the first likely need; do not widen WYL0464's message, which names a static.
- `TypeSendable` gains the rows the spec states: a reference to a shared class is sendable,
  nullable or not, and `Weak<T>` is sendable when `T` is a shared class. Decide with decision
  0036's open item whether `Weak` becomes sendable through a compiler fact (`StdlibSymbols.Weak`
  and the element's class) or through a way for `std`'s `unsafe` floor to declare a type sendable.
  Write the answer in a decision record and put the rules in the spec.
- A shared class's methods: the spec says its fields cannot change. Decide whether it may declare
  a `mut` method at all, and record the answer. Nothing in the standard library uses one yet.

## Step 2: one count that any thread may change

- `runtime/wyl/object.h` states today's rule: "The count is non-atomic: objects are thread-confined."
  A shared class's objects need a count that any thread may drop, and their last release may run the
  destructor on that thread. Add a shared form of the count and the release rather than making every
  object's count atomic.
- Allocate a shared object on the global heap rather than through the program's pool, and destroy
  it where its last reference goes. The emitter emits the shared form for a shared class and every
  class the binder marked shared.
- `String::Block` in `runtime/wyl/core.h` has a plain count, and its comment already says the count
  must become atomic "once threads exist". A shared class's field may hold a `String`, so the count
  becomes atomic in every program. `String`'s copies and slices in `core.h` and `strings.h` all read
  it.
- The interpreter's `ObjectRef` count and `Released` are the second executor's copy of the same
  rule. Its run is one thread, so the atomicity is unobservable, but the release point, the
  destructor's order, and the objects it may destroy must match the C++.

## Step 3: the cycle collector never sees a shared object

- The spec says a shared class's objects are never cycle-collected, because other threads may refer
  to them. `CyclicClasses` must exclude them, `wyl::is_cyclic` and the emitted `wyl_set_cyclic` must
  say no for them, and a release that lowers a shared object's count to zero must destroy it at
  once rather than buffer it.
- Check each place that asks whether a type may be part of a cycle, in both the emitter and the
  interpreter, and add the test that pins it.

## Step 4: tests

- A shared class's object is released when its last reference goes, and its destructor runs then.
- A shared class in a static: the static is sendable and copyable, and nothing releases it when
  `main` returns (the first type a static can hold that reaches an object).
- A field that is `mut`, or whose type is not sendable and copyable, is a compile-time error, and
  the message names the field.
- A class extending a shared class is shared, and a shared class's base is shared.
- `sendable` accepts a reference to a shared class, a nullable one, and `Weak<T>` of one, and
  refuses `Weak<T>` of a normal class.
- The rows in `MatrixTypes` (sendability is a type property) and a fuzzer mutation that adds
  `shared` to a class declaration.
- Run each in the interpreter and in the native modes; the cycle tests use `panic-here`.

## Still open

- `shared fn`, its capture rules, and its conversion to the function type without `shared`
  (`concurrency.shared-functions`).
- Threads, `Mutex`, `Atomic`, `Frozen`, channels, `Isolated`, and the per-thread pools with remote
  free lists. The runtime's `Object` header still says the collector has one counter for the
  program.
- The `sendable` rows for `std`'s `unsafe` floor types (0036), which `Weak` and the containers need.
- A static's value is never released at the end of the program, which becomes observable only once
  a static can hold a class object.
