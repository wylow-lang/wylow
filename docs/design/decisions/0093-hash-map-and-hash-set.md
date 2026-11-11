# 0093. `HashMap` and `HashSet`

- Status: accepted (2026-11-11)
- Spec: none; the standard library's doc comments state the behavior (decision 0001)

## Decision

- `std::collections` declares `HashMap<K extends Hashable<K>, V>` and `HashSet<T extends Hashable<T>>`
  as concrete `final` classes. No `Map` or `Set` interface exists. Another map with other
  trade-offs, such as one that keeps insertion order, is another concrete class with its own name.
- `HashMap` is a hash table with open addressing and linear probing over a power-of-two number of
  slots. Each slot holds its `MapEntry<K, V>` in place. A separate array holds one control byte for
  each slot: zero for an empty slot, and otherwise the high bit and seven bits of the key's hash, so
  that a probe calls `equals` only when the byte matches.
- `remove` shifts the later entries of the probe sequence back, so the table holds no marker for a
  removed entry. The table doubles when an entry would fill more than seven eighths of its slots.
  The table stores no hashes, so growing and removing hash the keys they move again.
- The order of iteration is not specified. The fixed seed of `Hasher` (decision 0090) makes it the
  same in every run and in both executors.
- `HashMap` implements `Borrowable`. A reference that `get` or `operator []` returns, and each
  iterator, borrow the map, so `put`, `remove`, `clear`, and `reserve` panic while it is borrowed.
  The map also counts a borrow while it calls a key's `equals` or `hash`, so a key whose methods
  change the map panics instead of leaving a lookup reading moved entries.
- `HashMap` manages its slots with `std::memory`, so it joins the unsafe floor, and implements
  `memory::ElementOwner<MapEntry<K, V>>` (decision 0092) so that the cycle collector visits its
  entries.
- `HashSet<T>` is a `HashMap<T, Unit>` with a set's methods, so it needs no unsafe code, and the
  probing code exists once.
- Iteration is by `entries()`, `keys()`, and `values()` on the map and `iterator()` on the set. The
  entries and keys are read-only in a loop, because a changed key would no longer match its slot.

## Alternatives

- **`Map` and `Set` interfaces with `HashMap` and `HashSet` as implementations**, as Java's
  [`Map`](https://docs.oracle.com/en/java/javase/21/docs/api/java.base/java/util/Map.html) has. An
  interface method has one return type, and a struct never converts to an interface, so iteration
  through the interface would allocate a class iterator on every loop. The user chose concrete
  classes only.
- **Insertion order as the default**, with entries in a dense list and an index table of positions,
  as Python's [`dict`](https://docs.python.org/3/library/stdtypes.html#dict) has kept since 3.7.
  It needs no unsafe code, but every lookup follows one more pointer, and the user chose open
  addressing with the entries in the slots as the default.
- **SwissTable's groups of sixteen control bytes**, which Rust's
  [hashbrown](https://github.com/rust-lang/hashbrown) compares at once with SIMD instructions. Wylow
  has no SIMD types. The table can compare in groups later without a change to its API.
- **Tombstones for removed entries.** Removal is cheaper, but a long run of removals slows later
  lookups until the table is rebuilt.
- **Storing each key's hash.** Growing and removing would not call the key's `hash` again, at the
  cost of eight bytes for each slot.
- **A table of `HashSet`'s own.** It would save the byte that each `Unit` value takes, and its
  padding, at the cost of a second copy of the probing code or a shared generic table type.

## Why

The user asked for an open-addressing map as the default, and for no interfaces, so that each kind
of map is a class chosen for its trade-offs. A slot of an open-addressing table is empty until a key
arrives, and a key or value may have no default value and may be unique, so safe Wylow could hold a
slot only as a nullable `MapEntry`. The verifier allows no reference into a nullable value, so
`get` could not return a reference into its slot, and the table lives in the unsafe floor as `List`
does. The borrow counted around a key's methods keeps the floor's promise that safe code cannot
reach undefined behavior, even through a key that changes the map it is in.
