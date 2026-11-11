# Proposal: equality, hashing, and `std::collections`

Status: agreed in full (2026-11-11), and being implemented. Each section's rules go into the spec,
and its reasons into a decision record, in the commit that implements it. The roadmap (issue #6) is
updated when the work is done, and this handoff is then deleted.

Sources: issue #14 (session A lists "equality and hashing, maps and sets"), the roadmap's
`Map<K,V>` entry under "Specified, not implemented", the open question at the end of
`docs/handoff/string-methods.md` about a `std::collections` module, and decisions 0036, 0038, and
0049.

The work has seven sections. Sections 1 to 6 are in the order they would be implemented, and
section 7 comes before section 6's classes:

1. `Equatable<T>`, and the built-in types' conformances.
2. `Hashable<T>` and `Hasher`.
3. `Comparable<T>` extending `Equatable<T>`.
4. Equality and hashing that the compiler writes for a struct or enum that declares the interfaces.
5. The module `std::collections`, with `List<T>` moved into it.
6. `HashMap` and `HashSet` in `std::collections`.
7. How a class that keeps values in raw memory gives the cycle collector its elements.

Each point below is written as **what the spec says now / what I propose / alternatives**.

---

## 1. `Equatable<T>`

**What the spec says now.** `==` and `!=` are built-in operators (`expressions.comparison`). A
struct's `==` compares every field, an enum's compares the variant and its fields, a class's
compares identity, and a `String`'s compares contents. A program cannot redefine `==`
(`structs.equality`). `==` on a value of a type parameter is a compile-time error, so generic code
has no way to ask whether two values are equal. `classes.equality` says that "equality of the
contents of two objects is a method the class declares", but no interface names that method.

**What I propose.**

```java unchecked
/// A type whose values can be compared for equality.
pub interface Equatable<T> {
    /// Returns whether this value equals `other`.
    bool equals(in T other);
}
```

- `equals` takes `in T` for the reason `compareTo` does (decision 0049): a unique `T` can then be
  compared without being moved.
- Every primitive type and `String` implement `Equatable` of themselves. Their `extern struct`
  declarations in `primitives.wyl` and `string.wyl` state the conformance, as they state
  `Comparable` (decision 0038). Their `equals` methods are written in Wylow (`return this == other;`),
  so the interpreter runs the same code as a native build.
- `float` and `double` define `equals` by their total order: `a.equals(b)` is true exactly when
  `a.compareTo(b) == 0`. So NaN equals NaN and `-0` does not equal `0`, while the operators keep
  IEEE 754 behavior. A map keyed by a `double` would otherwise lose every NaN key, because a NaN
  key would never equal itself. Java's
  [`Double.equals`](https://docs.oracle.com/en/java/javase/21/docs/api/java.base/java/lang/Double.html#equals(java.lang.Object))
  makes the same two exceptions to `==`.
- `==` stays an error on a value of a type parameter, even one bounded by `Equatable`. Generic code
  calls `equals`. As a result, `==` keeps one meaning for each type.
- A nullable type does not implement `Equatable`, as it implements no other interface (decision 0038).

**Alternatives.**

- **Make `==` on `T extends Equatable<T>` call `equals`.** The syntax is shorter in generic code,
  but `==` would then mean "identity" on a class value and "`equals`" on the same value reached
  through a type parameter.
- **A lowercase type property `equatable`**, like `copyable` (decision 0036), meaning "`==` works on
  this type". It needs no method, but a class could then only be compared by identity, and a key
  type could never define its own equality.

## 2. `Hashable<T>` and `Hasher`

**What the spec says now.** Nothing: no hashing exists in `std` or the runtime.

**What I propose.** A type feeds its parts to a `Hasher`, and the collection decides the algorithm.

```java unchecked
/// A type whose values can be hashed, for use as a key of a `Map` or an element of a `Set`.
/// Two values that are equal by `equals` must give the hasher the same input.
pub interface Hashable<T> extends Equatable<T> {
    /// Gives `hasher` the parts of this value that `equals` compares.
    void hash(ref Hasher hasher);
}

/// The state of a hash being computed.
pub struct Hasher {
    /// Adds `value` to the hash.
    pub mut void add(long value);
    /// Adds `bytes` to the hash.
    pub mut void addBytes(ReadOnlySpan<byte> bytes);
    /// Returns the hash of everything added so far.
    pub long finish();
}
```

- `Hashable<T>` extends `Equatable<T>`, so a `Map` needs one bound: `K extends Hashable<K>`.
- The primitives and `String` implement `Hashable` of themselves. `String.hash` adds its bytes.
  `float` and `double` add their bits after replacing every NaN with one NaN, so that hashing agrees
  with their `equals`.
- `Hasher` is written in Wylow with `wrappingMul` and shifts, not in C++. Decision 0087 says that
  extern calls are not interpreted, so a C++ hasher would leave the interpreter without one.
- The spec does not specify hash values. The implementation starts with a fixed seed, so that every
  run and both executors give the same values, and so that a `HashMap` iterates in the same order in
  every run (section 6). A later change may add a random seed without changing the spec.
- Rust's [`Hash`](https://doc.rust-lang.org/std/hash/trait.Hash.html) and Swift's
  [`Hashable.hash(into:)`](https://developer.apple.com/documentation/swift/hashable/hash(into:))
  have the same shape: the type feeds a hasher, and the collection owns the algorithm.

**Alternatives.**

- **Java's `int hashCode()`**
  ([`Object.hashCode`](https://docs.oracle.com/en/java/javase/21/docs/api/java.base/java/lang/Object.html#hashCode())).
  It is familiar, but each type then combines its fields' hashes by hand (`31 * h + x`), and the
  quality of every map depends on that arithmetic. The compiler-written version in section 4 would
  also have to pick a combining formula and freeze it.
- **A random seed per process from the start**, as Rust's
  [`RandomState`](https://doc.rust-lang.org/std/collections/hash_map/struct.RandomState.html)
  does, against inputs crafted to collide. It makes a program that prints a hash value give a
  different output on each run, which the test harness cannot check. I propose it as a later change.

## 3. `Comparable<T>` extends `Equatable<T>`

**What the spec says now.** `Comparable<T>` stands alone (`library.comparable`).

**What I propose.** `pub interface Comparable<T> extends Equatable<T>`. The doc comment states that
`compareTo` returns zero exactly when `equals` returns true. The compiler does not check that rule.

- Every built-in type that implements `Comparable` also implements `Equatable` under section 1, so no
  built-in type changes. A program's struct or enum gets `equals` written for it (section 4). A
  program's class that implements `Comparable` has to add `equals`. Four test files implement
  `Comparable` today.
- Rust's [`Ord`](https://doc.rust-lang.org/std/cmp/trait.Ord.html) requires `Eq`, and Swift's
  [`Comparable`](https://developer.apple.com/documentation/swift/comparable) inherits from
  `Equatable`.

**Alternative.** Keep the two interfaces apart, as Java does. A sorted collection would then need
the bound `T extends Comparable<T> & Equatable<T>`, and nothing would tie the two methods together.

## 4. Equality and hashing written by the compiler

**What the spec says now.** A struct or enum implements an interface by writing its methods
(`interfaces.implementing`).

**What I propose.** A struct or enum that declares `Equatable` or `Hashable` without writing the
methods gets them written by the compiler from its fields:

```java unchecked
struct Point implements Hashable<Point> {
    int x;
    int y;
}
// The compiler writes: equals compares x, then y, with each field's equals;
// hash adds x, then y, to the hasher.
```

- The written `equals` is true when every field's `equals` is true. For an enum, it is true when
  both values are the same variant and the variant's fields are equal.
- The written `hash` adds each field in declaration order. For an enum, it adds the variant's index
  first.
- It is a compile-time error if a field's type does not implement the interface. A field of a type
  parameter qualifies only if the parameter's bound requires the interface:
  `struct Pair<A extends Hashable<A>, B extends Hashable<B>> implements Hashable<Pair<A, B>>`.
- A struct or enum writes both `equals` and `hash`, or neither. If it wrote only `equals`, the
  written `hash` could disagree with it. Writing exactly one of them is a compile-time error.
- A class never gets them written. A class's `==` compares identity, and comparing fields could
  follow a cycle of objects forever. A class that declares `Equatable` writes `equals`, as for any
  interface.
- The binder declares the written methods, so lookup, bounds, and `Comparable`'s requirement
  (section 3) see them as ordinary methods. Lowering produces their bodies from the fields. The
  emitter and the interpreter then run them as any other method, and need no special case.
- A struct's `equals` can disagree with its `==`, and only because of its fields: a `double` field
  compares by its total order, and a class field compares by the class's `equals`.
- Swift writes these methods for a struct or enum that declares the protocol
  ([SE-0185](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0185-synthesize-equatable-hashable.md)),
  and it does not write them for a class. Rust writes them only on request, with
  [`#[derive(PartialEq, Eq, Hash)]`](https://doc.rust-lang.org/reference/attributes/derive.html).

**Alternatives.**

- **No compiler-written methods.** Every key struct writes `equals` and `hash` by hand. The two
  methods can then drift asection from each other and from the fields.
- **A struct or enum may never write its own `equals`**, so its equality is always the written one.
  It removes the case where `equals` and `==` differ by choice, but a key that ignores case or a
  cached field could not be expressed.
- **Conditional conformance**, where `Pair<A, B>` implements `Hashable` only when `A` and `B` do,
  as Swift's [conditional conformance](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0143-conditional-conformances.md)
  allows. It is a new language feature with its own design. I propose deciding it separately, and
  until then requiring the bound on the struct's own type parameter.

## 5. The module `std::collections`, with `List<T>` moved into it

**What the spec says now.** `List<T>` and `ListIterator<T>` are in `std`'s root (`Stdlib/list.wyl`),
and programs write `import std::List;`. `docs/design/working.md` says that a declaration that is one
of a family, such as a collection, belongs in a module named for the family's subject.

**What I propose.**

- The module `std::collections` (`Stdlib/collections/`) holds `List<T>`, `ListIterator<T>`,
  `HashMap<K, V>`, `HashSet<T>`, `MapEntry<K, V>`, and their iterators. Programs write `import std::collections::{List, HashMap};`.
- `Array<T>`, `Span<T>`, and `ReadOnlySpan<T>` stay in the root. The language names them for `T[]`
  and the span types, and a program never imports them.
- `Equatable`, `Hashable`, and `Hasher` go in the root beside `Comparable`, because most code builds
  on them (working.md, "`std`'s root holds the foundation").
- `Equatable` and `Hashable` are not names every file sees. The user leans towards no prelude, and
  the roadmap already asks whether `Comparable` and `Printable` should stay in it. Appendix A's table
  lists them anyway, because the compiler writes their methods (section 4).
- The move is mechanical. The compiler does not name `List` anywhere: `StdlibSymbols.Register` has
  no case for it, and `ContainerElement` asks whether a class is in package `std`, not which module.
  The changes are 107 test files that import `List`, two examples, `docs/spec/03-programs.md`,
  appendix A's `List.isolate` and `List.freeze` rows, and twelve diagnostic pages. A member of
  `List` that another module of `std` uses has to be `package`, not `module`.

**Alternatives.**

- **Keep `List` in the root and put only `Map` and `Set` in `std::collections`.** Nothing moves, but
  the family is split, which is what working.md's rule exists to prevent.
- **`std::collections` re-exports `List` from the root** (`pub import`). Two paths would name one
  type.

## 6. `HashMap` and `HashSet`

**What the spec says now.** Nothing. The roadmap lists `Map<K,V>`.

**Agreed so far.** The default map uses open addressing. `std` declares concrete classes only, named
`HashMap` and `HashSet`, and no `Map` or `Set` interface. A later map with other trade-offs is another
concrete class with its own name. `List` stays a single class, because a growable array has one
design.

**What I propose.**

### The API

```java unchecked
pub final class HashMap<K extends Hashable<K>, V> implements Borrowable {
    pub HashMap();
    pub HashMap(int capacity);
    pub int count();
    pub bool isEmpty();
    pub bool containsKey(in K key);
    pub ref? V get(in K key);                 // null if no entry has the key
    pub ref V operator [](in K key);          // panics if no entry has the key
    pub V? put(K key, V value);               // adds or replaces; returns the value it replaced
    pub V? remove(in K key);
    pub void clear();
    pub void reserve(int minimum);
    pub HashMapEntries<K, V> entries();       // InIterator<MapEntry<K, V>>
    pub HashMapKeys<K, V> keys();             // InIterator<K>
    pub HashMapValues<K, V> values();         // RefIterator<V>
}

pub struct MapEntry<K, V> {
    pub K key;
    pub V value;
}

pub final class HashSet<T extends Hashable<T>> implements Borrowable {
    pub HashSet();
    pub HashSet(int capacity);
    pub int count();
    pub bool isEmpty();
    pub bool contains(in T value);
    pub bool add(T value);                    // false if the set already had an equal value
    pub bool remove(in T value);
    pub void clear();
    pub void reserve(int minimum);
    pub HashSetIterator<T> iterator();        // InIterator<T>
}
```

- The names of the methods follow Java's
  [`HashMap`](https://docs.oracle.com/en/java/javase/21/docs/api/java.base/java/util/HashMap.html)
  and [`HashSet`](https://docs.oracle.com/en/java/javase/21/docs/api/java.base/java/util/HashSet.html).
- Both classes implement `Borrowable`, as `List` does. A reference that `get` returns borrows the
  map, and so does an iterator. If code adds or removes an entry while the map is borrowed, the
  borrow counter panics, so the reference never dangles.
- A loop over a map's entries is written `for (in MapEntry<String, int> entry : map.entries())`. A
  for-each loop takes a span or an iterator (`statements.for-each`), and a `HashMap` is neither.
- The entries and keys are read-only in a loop, because a changed key would no longer match its
  slot. A loop changes values through `values()`.
- `map[key] = value` replaces the value of an existing entry and panics if the key is absent. Adding
  an entry is `put`. As a result, one way exists to add an entry.

### The table

- The table uses open addressing with linear probing over a power-of-two number of slots. Each slot
  holds its `MapEntry` in place. A separate array holds one control byte for each slot: zero for an
  empty slot, and otherwise seven bits of the key's hash with the high bit set. A lookup calls
  `equals` only when a slot's control byte matches the key's, so most probes that miss never call it.
- `remove` shifts the later entries of the probe sequence back by one slot. As a result, the table
  never holds tombstones, and a long run of removals does not slow later lookups.
- The table doubles its size when an `add` or `put` would fill more than seven eighths of its slots.
  Growing hashes every key again, because the table stores no hashes.
- Iteration visits the slots in order, so the order depends on the hash values. The spec says that
  the order is unspecified. The fixed seed of section 2 makes the order the same in every run and in
  both executors. A later random seed would change the order between runs, and the spec allows that
  change.
- `HashSet` and `HashMap` share one module-private table type, so that the probing code exists once.
- Rust's `HashMap` is also an open-addressing table
  ([hashbrown](https://github.com/rust-lang/hashbrown), a port of Google's SwissTable). SwissTable
  compares sixteen control bytes at once with SIMD instructions. Wylow has no SIMD types, so I
  propose comparing one byte at a time first. The table can later compare in groups without a change
  to its API.

### `HashMap` and `HashSet` join the unsafe floor

A slot of an open-addressing table is empty until a key arrives, and a key or value type may have no
default value and may be unique. Safe Wylow could hold such a slot only as a nullable
`MapEntry<K, V>?`. The verifier allows no reference into a nullable value
(`PlacePath.NullableValueRoot`), so `get` could not return a reference into the slot. `HashMap` and
`HashSet` therefore manage their slots with `std::memory`, as `List` manages its elements. The design
principle "unsafe code lives only in the standard library's floor" would then list `Array`, `List`,
`HashMap`, `HashSet`, `Span`, and native `String`, and `docs/design/working.md` changes with it. The
cycle collector reaches the slots through section 7.

### Not in this step

- More maps and sets, such as one that keeps insertion order and a sorted one bounded by
  `Comparable`. Each one is a concrete class with its own name.
- `Printable`, `Equatable`, and `Hashable` for collections. Each one needs conditional conformance
  (section 4).
- Lookup by a different type than the key, such as a `ReadOnlySpan<byte>` for a `String` key. Rust
  does it with [`Borrow`](https://doc.rust-lang.org/std/borrow/trait.Borrow.html).
- `getOrPut(key, fn() -> V)`, set operations (`union`, `intersect`), and `isolate` and `freeze` as
  `List` has them.
- `List.contains` and `List.indexOf` as extension methods bounded by `Equatable`, which section 1
  makes possible.
- Keys compared by identity for any class. A class that wants identity keys writes `equals` with
  `==`, but it has no stable value to hash. A later `std::memory` function could provide one.

**Alternative.** **A safe `HashMap` built on `List`**, with entries in a dense list and open
addressing on an index table of positions. It needs no `unsafe`, but every lookup follows one more
pointer, and the user asked for open addressing in the slots as the default.

## 7. How a raw-memory class gives the cycle collector its elements

**What the spec says now.** The collector looks at a class through its fields. The elements of
`List` and `Array` also count as references they hold, and a class of the program is looked at
through its fields only, even if it is `Spannable` (`memory.cycles.collected`). In the compiler, one
predicate answers which classes have elements, `StdlibSymbols.ContainerElement`: a class of package
`std` that implements `ReadOnlySpannable<T>` holds `T` elements. Three layers use the predicate.
`CyclicClasses` counts the element type when it decides which classes may be cyclic. The emitter's
generated `Visit` calls `asReadOnlySpan` and visits each element of the span. The interpreter calls
the same method and visits the same elements. `Array`, `List`, and `Frozen` take part.

**The problem.** The span rule cannot describe a `HashMap`, whose live entries are scattered among
empty slots, so no span holds exactly them. The rule also gives a program's own raw-memory container
no way to take part, so a cycle through such a container is never freed. The roadmap lists that gap
under "Built-in interfaces, follow-ups".

The collector uses trial deletion: it subtracts every reference it finds among the objects it
visits, and it frees a group whose counts reach zero. If a class reports a reference twice, the
collector subtracts it twice and can free an object that is still in use. If a class reports a slot
that holds no value, the collector reads uninitialized memory. Both mistakes are undefined behavior.
A class that misses a reference only leaks. Reporting elements correctly is therefore a promise that
the compiler cannot check, which is what `unsafe` marks.

**What I propose.**

```java unchecked
// In std::memory.

/// A class that keeps values in raw memory, and reports them to the cycle collector.
///
/// # Safety
///
/// `visitElements` passes each value that the class keeps in raw memory to `visitor.visit` exactly
/// once, and passes nothing else. It changes no value, creates or releases no reference, does not
/// panic, and does not call `collectCycles`.
pub unsafe interface ElementOwner<T> {
    void visitElements(ref ElementVisitor<T> visitor);
}

/// The cycle collector's view of the values that an `ElementOwner` reports.
pub unique extern struct ElementVisitor<T> {
    /// Reports `element` to the cycle collector.
    pub mut void visit(ref T element);
}
```

```java unchecked
pub final class List<T> implements Borrowable, Spannable<T>, unsafe memory::ElementOwner<T> {
    // ...
    pub void visitElements(ref memory::ElementVisitor<T> visitor) {
        unsafe {
            for (int i : 0..size) {
                visitor.visit(ref data[i]);
            }
        }
    }
}
```

- **The new form: an unsafe interface.** An interface declared `unsafe` is one whose implementations
  make a promise that the compiler cannot check. A class implements it by writing `unsafe` before the
  interface's name in its `implements` list. It is a compile-time error to implement an unsafe
  interface without `unsafe`, and to write `unsafe` before an interface that is not unsafe. The
  word `unsafe` in the list marks the class's promise, and a reviewer searches for it as for an
  `unsafe` block. Rust has the same pair of forms:
  [unsafe traits](https://doc.rust-lang.org/reference/items/traits.html#unsafe-traits) and
  [unsafe implementations](https://doc.rust-lang.org/reference/items/implementations.html#unsafe-implementations).
  The existing `unsafe` on a method marks a promise that each caller makes, so it cannot express a
  promise that the implementer makes.
- **Who may implement it.** Only a class may implement `ElementOwner`, because only a class's objects
  are visited. Any class may, in `std` or in a program. The package check in `ContainerElement` goes
  away.
- **The visitor.** `ElementVisitor<T>` is a unique struct with no public constructor, so code cannot
  create one. The method receives it by `ref`, and a function that receives a `ref` cannot move the
  value out (decision 0028), so code cannot keep one either. `visit` takes the element by `ref`,
  because the collector clears a reference in place when it breaks a cycle. The visitor accepts only
  `T`, so the type that the analysis counts and the values that the collector visits always agree.
- **The analysis.** A class holds the `T` of every `ElementOwner<T>` it implements, through structs,
  enums, and nullables, as it holds its fields' types. `CyclicClasses` reads that type in place of
  `ContainerElement`. `HashMap<K, V>` implements `ElementOwner<MapEntry<K, V>>`, so the analysis sees
  both `K` and `V`. A class may implement `ElementOwner` for more than one element type, and the
  collector visits each.
- **The emitter.** A class's generated `Visit` visits the base class first, then the fields in
  declaration order, then calls `visitElements` once for each `ElementOwner` that the class itself
  implements. The `ElementVisitor` in C++ wraps the collector's `wyl::Visitor&`, and its `visit` is
  `wyl::Visit(visitor, element)`. A subclass inherits the elements through its base class's `Visit`.
- **The interpreter.** The interpreter runs `visitElements` as the Wylow method it is, and implements
  `ElementVisitor.visit` as the cell visit that it already uses for fields. `VisitContainerElements`
  and its calls of `asReadOnlySpan` go away.
- **Which classes change.** `Array`, `List`, `Frozen`, `HashMap`, and `HashSet` implement
  `ElementOwner`. `Spannable` and `ReadOnlySpannable` stop mattering to the collector, so a class
  that implements them no longer risks having its elements counted twice.
- **The spec.** `memory.cycles.collected` says that a class's elements are the values that its
  `visitElements` reports, in place of naming `List` and `Array`. Step 3 of `memory.cycles.destroying`
  says "a class that implements `ElementOwner`" in place of "the standard library's containers".
  Appendix A's table lists `ElementOwner` and `ElementVisitor`, because the collector calls them.
  Chapter 10 gains the unsafe interface, and chapter 20 lists implementing one as unsafe code.

**Alternatives.**

- **Keep the span rule and add a second rule for tables.** It would add a second answer to "what a
  class holds", which the rule "each question has one answer" forbids, and a program's container
  would still have no way to take part.
- **Let only `std` implement `ElementOwner`**, as only `std` takes part today. It needs no unsafe
  interface. It leaves the roadmap's gap open: a program that builds its own container on
  `std::memory` leaks every cycle through it.
- **A `fn(ref T)` parameter in place of `ElementVisitor<T>`.** A function value could be kept or
  called later, and calling one through a lambda costs more than a direct call. An opaque visitor
  that code can neither create nor keep states the rule by its type.
- **Annotate the pointer field with its element count** (`@Elements(size) T* data`). The compiler
  would then generate the visit itself, but the annotation fits only contiguous storage, which is the
  case the span rule already covers.

## New diagnostics

- A struct or enum declares `Equatable` or `Hashable` without writing the methods, and a field's type
  does not implement the interface.
- A struct or enum writes one of `equals` and `hash` and not the other.
- A class declares `Equatable` or `Hashable` and omits a method. The existing missing-method
  diagnostic reports it.
- A key type that does not implement `Hashable`. The existing bound diagnostic reports it.
- A class implements an unsafe interface without `unsafe` before its name.
- A type writes `unsafe` before an interface that is not unsafe.
- A struct or enum implements `memory::ElementOwner`.
- Code outside the standard library creates an `ElementVisitor`. The existing diagnostic for a
  constructor that is not visible reports it.

## Tests

- `tests/cases/equality/`: the built-in types' `equals` and `hash`, NaN and `-0` keys, compiler-written
  methods on structs, enums, and a generic struct with bounds, a struct field of a class type, and
  every error above.
- `tests/cases/containers/`: `HashMap` and `HashSet` with `String`, struct, and enum keys and unique
  values; lookups after removals that shift entries back; `put` and `remove` panicking while a `get`
  reference or a loop holds a borrow; growing past several table sizes; a cycle through a map's
  values, and one through a set's elements, that the collector releases.
- `tests/cases/cycles/`: a program's own raw-memory container that implements `ElementOwner`, with a
  cycle through its elements that the collector releases; a container with two element types; a
  subclass of an element owner; and every error above. The existing cycle tests through `List`,
  `Array`, and `Frozen` pin that the span rule's replacement visits the same references.
- Each feature's row or column in the matrices, and a fuzzer mutation where one can cross it.

## Decision records

The records are numbered in the order the work is committed:

- 0089: the module `std::collections` (section 5).
- 0090: `Equatable`, `Hashable`, and `Hasher`, and `Comparable` extending `Equatable` (sections 1–3).
- 0091: equality and hashing written by the compiler (section 4).
- 0092: unsafe interfaces, and `memory::ElementOwner` replacing the span rule (section 7).
- 0093: `HashMap` and `HashSet`, and their place in the unsafe floor (section 6).

## Questions for the user

The user agreed to sections 1 to 5. In those sections, `equals` is a method and `==` stays an error
on a type parameter, types feed a `Hasher`, and `Comparable` extends `Equatable`. The compiler writes
`equals` and `hash` for a struct or enum that declares the interface without writing them, and
`Equatable`, `Hashable`, and `Hasher` go in `std`'s root. For section 6, the user chose concrete
classes named `HashMap` and `HashSet` with no `Map` or `Set` interface, and asked for the cycle
collector's hook to be designed in this step. The questions that remain:

1. `HashMap` and `HashSet` join the unsafe floor, because a safe open-addressing table could not
   return references into its slots. Agree?
2. Linear probing with one control byte per slot, removal by shifting back, and growth at seven
   eighths full. Agree, as the first version?
3. Section 7 adds unsafe interfaces to the language (`unsafe interface`, and `unsafe` in an
   `implements` list). Agree, or should only `std` implement `ElementOwner` for now?
4. The names `ElementOwner`, `ElementVisitor`, and `visitElements`, in `std::memory`. Agree?
