# 0092. Unsafe interfaces, and `ElementOwner` reports values in raw memory to the cycle collector

- Status: accepted (2026-11-11)
- Spec: `interfaces.unsafe`, `unsafe`, `library.memory`, `memory.cycles.collected`,
  `memory.cycles.destroying`

## Decision

- An interface may be declared `unsafe`. A type that implements one writes `unsafe` before its name
  in the `implements` list, which marks the promise that the interface's documentation states. It
  is an error to leave the word out (`WYL0492`), to write it before an interface that is not unsafe
  (`WYL0493`), and for an interface that is not unsafe to extend an unsafe one (`WYL0494`).
- `std::memory` declares `unsafe interface ElementOwner<T>`, with
  `void visitElements(ref ElementVisitor<T> visitor)`, and `ElementVisitor<T>`, a unique extern
  struct whose `visit(ref T element)` reports a value to the collector. Only the collector makes a
  visitor that reports anything.
- The cycle collector calls `visitElements` of each object it visits, after the object's fields,
  and the analysis that decides which classes may be cyclic counts the `T` of each class's
  `ElementOwner<T>`. `StdlibSymbols.OwnedElements` answers both, and replaces
  `StdlibSymbols.ContainerElement`, which counted the span of a class of `std` that implemented
  `ReadOnlySpannable`.
- `List`, `Array`, and `Frozen` implement `ElementOwner`. A program's class may too, and only a
  class may (`WYL0495`).
- The destructor of a class that implements `ElementOwner` runs with its fields in a collection,
  after every other destructor of the cycle and after the collector clears the references among
  the cycle's objects. This replaces the rule that gave only the standard library's classes that
  order.

## Alternatives

- **Keep the span rule and add a second rule for hash tables.** A hash table's live entries lie
  among empty slots, so no span holds exactly them. A second rule would give the question of what a
  class holds two answers, and a program's container would still have no way to take part.
- **Let only `std` implement `ElementOwner`.** It needs no unsafe interface, but a program that
  builds its own container on `std::memory` would leak every cycle through it, as before.
- **A `fn(ref T)` parameter in place of `ElementVisitor<T>`.** A function value could be kept and
  called later, and a call through it costs more than a direct call. A visitor that only the
  collector makes and that code cannot move out of its `ref` parameter states the rule by its type.
- **`unsafe` on the method instead of the interface.** An unsafe method marks a promise that each
  caller makes, while this promise is the implementer's. Rust separates the two in the same way:
  [unsafe traits](https://doc.rust-lang.org/reference/items/traits.html#unsafe-traits) and
  [unsafe implementations](https://doc.rust-lang.org/reference/items/implementations.html#unsafe-implementations).
- **An annotation on the pointer field with its element count.** The compiler could generate the
  visit, but only for contiguous storage, which the span rule already covered.
- **Several `ElementOwner`s on one class.** The language lists an interface once in an `implements`
  list, whatever its type arguments, so a class that keeps values of several types keeps them in
  one struct type.

## Why

The collector uses trial deletion: it subtracts each reference it finds among the objects it
visits, and frees a group whose counts reach zero. A class that reports a value twice can make the
collector free an object in use, and one that reports a slot with no value makes it read
uninitialized memory. A class that reports too little only leaks. So reporting values correctly is
a promise the compiler cannot check, which is what `unsafe` marks, and the promise belongs to the
class that keeps the values. With one interface for every class that keeps values in raw memory,
the standard library's containers and a program's own are visited by the same rule, which also
lets `HashMap` and `HashSet` report entries scattered among empty slots.

An element owner's destructor releases its values, so it cannot run while the rest of the cycle
must stay intact: releasing a value would release objects of the cycle before their destructors
ran. Running it with the fields, after the collector clears the references among the cycle, is
what the standard library's containers did, now stated for every class that owns elements.
