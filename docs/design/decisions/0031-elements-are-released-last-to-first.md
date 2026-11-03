# 0031. Elements are released last to first

- Status: accepted (2026-11-02); answers the first open question of [0028](0028-destructors-on-unique-structs.md)
- Spec: `memory.release.destruction`; `List`'s and the channel's doc comments

## Decision

Releasing an array, fixed (`T[N]`) or on the heap (`T[]`), or a `List<T>`, releases its elements
from the last to the first. So does clearing a `List<T>`. A channel released while it holds values
nobody received releases them oldest first, in the order they would have been received.

## Alternatives

- **First to last**, which `List<T>` and `T[]` did until this change, and which Rust specifies for
  arrays and slices ([Rust reference: destructors](https://doc.rust-lang.org/reference/destructors.html):
  "The elements of an array or owned slice are dropped from the first element to the last").
  The same page drops a struct's fields in declaration order, so the rule is consistent within
  Rust. Here fields, locals, and a statement's temporaries are released in reverse
  (`memory.release.destruction`, `memory.release.temporaries`), and fixed arrays, emitted as
  `std::array`, already were ([C++ draft, \[class.dtor\]/13](https://eel.is/c++draft/class.dtor#13):
  "Destructors for elements of an array are called in reverse order of their construction").
- **Leaving the order to each container.** The order is observable whenever an element has a
  destructor, so it would be one more thing to look up per type.

## Why

One rule covers every kind of storage: what was created last is released first. A container whose
elements were added in order then tears down like a sequence of locals would.

A channel is the exception because it is a queue: its values are released in the order its
receiver would have seen them.

Changing `List<T>` and `T[]` reversed the expected output of five tests
(`classes/weak.wyl`, `containers/list.wyl`, `containers/loop_elements.wyl`,
`containers/loop_elements_destructor.wyl`, `lambdas/element_writes.wyl`) and one of
`containers/array.wyl`; `containers/release_order.wyl` pins the order for every kind.
