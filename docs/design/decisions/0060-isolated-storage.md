# 0060. `Isolated<T>`: a value beside a runtime buffer, and `take` through an extern

- Status: accepted (2026-11-06)
- Spec: `concurrency.isolated`, `library.isolated`

## Decision

`std::isolated` declares `pub unique struct Isolated<T>` with two package fields, `T value` and
`IsolatedState state`. `IsolatedState` is a `package extern struct`, so its C++ type is
`wyl::IsolatedState` and the runtime owns its shape, as the runtime owns `SyncState`'s.

- The box has a `package` constructor `Isolated(T content)` beside the public lambda constructor. A
  conversion builds a box through this constructor (0063).
- `take` moves the value out through an extern function, `takeValue<T>(ref T value)`, whose C++ body
  returns `std::move(value)` and leaves the source moved from. The language has no way to move a
  field out of a struct ([0049](0049-getting-a-unique-value-out.md)), and a by-value receiver is not
  a mutable place, so `take` moves its receiver into a `mut` local and passes that local's field by
  `ref`.
- The box declares no destructor. `IsolatedState`'s destruction merges the graph's buffer into the
  releasing thread's, and the `state` field is destroyed before the `value` field, since C++ destroys
  members in reverse declaration order and the interpreter releases a struct's fields last to first.
  The graph's value is therefore released after its buffer has joined the thread.

## Alternatives

- **The value behind a counted handle, as `Thread<T>` keeps its result in runtime storage.** `take`
  would read a package field by value and move the value out of what the handle holds. The handle
  must then be counted, so that the box's release and the call's copy both find a live state, which
  costs an allocation and an atomic count for every box.
- **The whole box passed to a runtime template that moves the field out.** The C++ function would
  name the field the emitter makes, `f_value`, and so tie the runtime to the layout of a type the
  runtime cannot check.
- **A destructor on the box.** The destructor would merge the buffer, which `IsolatedState`'s own
  destruction already does. A destructor would also give the box the state byte that a unique struct
  with a destructor carries, for nothing.
- **A `byte*` handle, as `Thread<T>` holds.** The graph's buffer is more than a pointer, and an
  extern struct keeps its shape inside `std` where `byte*` would expose it.
- **The value as a `pub` field.** A program could then read the graph without `open`, and the graph
  would stop being isolated.

## Why

A box must be a unique struct that owns both the value and the buffer, and the language decides
everything else. A field of a struct cannot be moved out, so `take` asks the runtime to move the
value and leaves the field moved from, which every value of the language survives. The state's
destruction is the one place a box ends, whether `take` ended it or its scope did, so the merge
happens there and `take` needs no code of its own.
