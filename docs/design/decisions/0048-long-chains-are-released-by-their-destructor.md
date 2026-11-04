# 0048. A long chain of objects is released by a destructor that loops

- Status: accepted (2026-11-03)
- Spec: `memory.release.destruction`, `introduction.implementation-defined`

## Decision

Releasing a chain of objects nests, one release per object, and a long enough chain exhausts the stack. The runtime adds no mechanism for this. A type that forms long chains declares a destructor that detaches the rest of the chain and releases it in a loop.

## Alternatives

- **A work list in the runtime**: the destruction order is specified and observable, so it would have to keep that order exactly, with destruction broken into steps the emitter generates for every class.
- **Growing the stack on demand**, as Rust's [`stacker`](https://github.com/rust-lang/stacker) crate does: it keeps the order, but needs a stack-switching mechanism per platform, and the chain's nesting still uses that much memory.
- **A larger default stack**: it only moves the length at which a program aborts.

## Why

The same list overflows in C++ with `std::unique_ptr` and `std::shared_ptr` next pointers (g++ `-O0` by 30,000 and 10,000 nodes, `-O2` by 100,000; MSVC not measured). Rust's [`LinkedList`](https://doc.rust-lang.org/src/alloc/collections/linked_list.rs.html) has a `Drop` that pops its nodes in a loop. In Wylow, a `Node { Node? next; }` without one overflowed between 3,109 and 3,250 nodes on an MSVC debug build, and with one released 2,000,000 nodes on MSVC debug, MSVC release, and g++ debug builds.
