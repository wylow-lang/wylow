# 0028. Destructors on unique structs

- Status: accepted (2026-11-02); adds destructors to the unique structs of [0009](0009-unique-values-and-moves.md)
- Spec: `structs.unique`, `memory.release.destruction`, `memory.holds`

## Decision

A `unique struct` may declare a destructor. A struct that is not unique may not.

- **When it runs.** When a value is released and was not moved: scope end, overwriting, removal from
  a container, release of what contains it. Timing is scope end, as for classes.
- **Order.** The destructor's body, then the fields in reverse declaration order.
- **Body.** `this` is a `ref`, as in a `mut` method, and the value is in use for the body. A panic in
  it ends the program like any panic.
- **`discard name;`** ends a by-value parameter's lifetime without running the body; its fields are
  still released. It is allowed only in a function declared in the type's own file. Paths that
  don't discard release the value at the function's end, so a function that closes a resource may
  return an error before discarding.
- **Values in use.** A value with a destructor is in use while a method, a `ref` or `in` argument,
  or a `ref`/`in` variable refers to its place. Releasing a value that is in use panics.
- **Statics** can't hold unique values (5.5), so no destructor runs at the end of the program.
- A panic runs no destructor (`introduction.panics`).
- A unique enum has no destructor of its own; releasing it releases its fields.

Compile-time errors this adds (each needs a catalog entry, a test, and a page): a destructor on a
struct that is not unique; a destructor with parameters or modifiers; `discard` of anything but a by-value
parameter of a unique type with a destructor; `discard` outside the type's file; moving out of
`this` in a destructor.

## Alternatives

- **An `IDisposable`-style interface and a `using` statement**, as in C#
  ([`using` statement](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/using)).
  The caller must remember to call it, the value needs a disposed state, and a unique value already
  has exactly one owner that knows when it ends.
- **Destroying a value after its last use**, as Mojo does
  ([Mojo: value destruction](https://mojolang.org/docs/manual/lifecycle/death)). A destructor's
  timing could not be read from the source, which the temporaries rule is written to allow
  (`memory.release.temporaries`). Mojo's own page gives `_ = value` to hold one longer.
- **Partial moves out of a value**, as Rust allows
  ([Rust reference: destructors](https://doc.rust-lang.org/reference/destructors.html)). Fields
  can't be moved out of a value (`memory.moves`), so a destructor always sees a whole value, as in
  Swift's [SE-0390](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0390-noncopyable-structs-and-enums.md),
  which lists partial consumption as a future direction.
- **Read-only `this` in the body.** SE-0390 keeps `self` borrowed in `deinit` and notes the concern
  that a helper could replace or destroy `self` and re-enter it. Here the value is in use during
  the body, so the in-use panic covers that, and the body can assign fields and call `mut` methods
  on struct fields, which a destructor needs to flush or close what it owns.
- **A function `std::discard(value)`.** SE-0390 considered a general `forget` and narrowed it to the
  type's own file, since forgetting arbitrary values breaks types such as lock guards.
- **Requiring every path to discard or consume**, as SE-0390 does. Here a release at a path's end
  is defined, and the useful pattern is an early error return that still closes the resource.
- **No in-use check, or a static rule.** Nothing prevents a call from replacing the place a method
  is running on: through another name for a class's field, or a lambda that assigns a captured
  local. The destructor would run in the middle of the method. Forbidding destructor-bearing values
  in `mut` class fields would remove the common case, and flow analysis can't see through calls.
- **Destroying statics at the end of the program.** Statics are initialized lazily and read by
  every thread, so their teardown order and races have no cheap definition.

## Why

A unique value has one owner, so the compiler knows exactly when it ends; that makes it the right
home for a resource that must be closed. Scope-end timing and no partial destruction keep the order of destructors, which is observable, readable from the source.

The in-use rule is a panic rather than a compile-time error because a call can reach the place by
another route that only run time sees. It is deterministic: the same program panics in the same
place in every build. Nothing about memory safety depends on a destructor running
([Beingessner, "Pre-Pooping Your Pants With Rust"](https://cglab.ca/~abeinges/blah/everyone-poops)
on why Rust's `thread::scoped` was removed).

## Open

- The order in which a container releases its elements, needed before containers hold values with
  destructors; the container's doc comment will state it (`0004`).
- Whether a thread receiving a moved value runs its destructor on that thread (assumed).
