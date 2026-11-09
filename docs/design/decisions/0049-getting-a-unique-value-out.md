# 0049. Getting a unique value out

- Status: accepted (2026-11-04); extends [0009](0009-unique-values-and-moves.md) and
  [0028](0028-destructors-on-unique-structs.md); `move` methods replaced by by-value receivers in
  [0050](0050-by-value-receivers.md), and `structs.methods.move` is now `structs.methods.by-value`
- Spec: `generics.checking`, `patterns.bindings`, `patterns.bindings.moves`, `patterns.references`,
  `patterns.cases.guards`, `structs.methods.move`, `structs.unique.destructors`, `enums.methods`,
  `expressions.null`, `expressions.try`, `functions.parameters.modes`, `functions.extensions`,
  `functions.lambdas.captures`, `flow.moves`, `memory.moves`, `memory.references`, `memory.holds`,
  `library.comparable`, `library.memory`, `library.replace`

## Decision

Before this record, only `try` could take a unique value out of a `Result`, a method could not
consume `this`, nothing opened a `T?` of a unique `T`, and a unique field could not be taken out of
an object.

- **Generic code.** The bounds in the declaration decide whether a value is copied or moved, never
  the type arguments. `T y = x` moves for every unbounded `T`.
- **Bindings.** `var name` is a by-value use: it copies a copyable part and moves anything else.
  A moving binding is allowed only on a temporary, a whole local or by-value parameter, or `this` in
  a `move` method; elsewhere the program writes `in var`.
- **Whole values.** A moving binding moves the whole value, on that case's path only. The rest leaves
  the variable at the move and is released right after the pattern's names go out of scope.
- **Guards.** In a guard a moving binding is a read-only reference; the move happens when the guard
  is true.
- **Destructors.** A pattern cannot move a field out of a value whose type has a destructor.
- **`move` methods.** A struct or enum method declared `move` takes `this` by value. `Result`'s
  `orElse`, `map`, `mapErr`, and `orPanic` become `move` methods. A lambda never moves what it
  captures.
- **Nullables.** `a!` behaves as `a` does, and panics on null. `??` and `?.` with a `move` method
  move their left operand.
- **`try`.** Its operand is used by value on both paths.
- **Values in an object.** A non-copyable value that may be replaced is bound in place and is in use
  while the binding is in scope, and replacing it panics.
- **`replace`, `swap`, `take`.** These take a value out of a place, and panic on a value in use.
- **Unique enums** have no destructors, as the spec already said; the roadmap was wrong.
- **`Comparable.compareTo` takes `in T`,** and `ref` and `in` are allowed on class and interface
  types. They stay errors on function types.

### Implementation notes

"In use" is one bit per value, saved in a hidden local when a use starts and restored when it ends.
It is usually free: it fits in an enum's tag (`CppEmitter.Types.cs`) and in `Optional`'s `has_`
(`optional.h`). A struct with a destructor needs a byte and its padding. The optimizer may skip the
bit where it proves nothing can replace the value, since the check could never fire there.

`std` also needs `memory::moveOut` for `List.removeAt`, and a check per type that looks through a
value's fields for `replace` and `swap`. `List.first` and `last` copy `T`, and a member can't state
a bound, so the `copyable` work decides how they change; returning `ref? T`, as `List.get` does, is
the leading option.

## Alternatives

- **Generic code decided per instance**, or checked as a move but run as a copy when the type allows,
  as a C++ template's `T y = x` is
  [copy-initialization](https://en.cppreference.com/w/cpp/language/copy_initialization.html) in each
  instance. The first contradicts `generics.checking`, and in the second nothing in the text shows
  which release timing a reader gets. The rule chosen means the same text with `Node` and with an
  unbounded `T` can release at different times. Rust has no such difference because its reference
  count is not `Copy`
  ([moved and copied types](https://doc.rust-lang.org/reference/expressions.html#moved-and-copied-types)).
  Swift's `T: ~Copyable` also treats every argument as non-copyable
  ([SE-0427](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0427-noncopyable-generics.md)).
- **A `consume` operator on the subject**, as in
  [SE-0432](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0432-noncopyable-switch.md).
  Wylow moves implicitly everywhere else, so a switch would be the one place that has to ask.
- **Per-field moves**, as in Rust ([destructors](https://doc.rust-lang.org/reference/destructors.html))
  and Swift ([SE-0429](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0429-partial-consumption.md)).
  They allow more programs, and add "partly moved" as a state every rule must consider. They can be
  added later, keeping when the rest is released.
- **Releasing the rest when the case is chosen.** A field acting as a guard would be released before
  the body it protects.
- **Delaying copies in a guard too**, as Rust does
  ([match guards](https://doc.rust-lang.org/reference/expressions/match-expr.html)). That would change
  what existing programs print when a guard changes the value.
- **Fields taken after `discard`**, a future direction of SE-0429. It changes `discard`, which
  releases the fields, and can be added later. Today a function in the type's file uses `replace` and
  then `discard`, and Rust's [E0509](https://doc.rust-lang.org/error_codes/E0509.html) needs the same.
- **Extensions with a by-value `this`, found without an import when declared in the type's file.**
  The issue proposed the extensions. The exception would make some extensions need imports and others
  not, for a reason invisible at the call. A modifier keeps one rule: what a type declares comes with
  it, and an extension is imported. It is the form of Rust's
  [`self`](https://doc.rust-lang.org/std/option/enum.Option.html#method.unwrap_or) and Swift's
  [`consuming`](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0390-noncopyable-structs-and-enums.md).
  The word `move` is contextual, so no name stops being usable.
- **Bindings banned from anything that isn't a stable local.** This has no runtime cost, but an `in`
  parameter or `this` may refer into an object, so no read-only method could look inside a unique
  enum. Rust tracks this with lifetimes, and [`RefCell`](https://doc.rust-lang.org/std/cell/index.html)
  panics for shared data; Swift enforces exclusivity at run time for class properties
  ([SE-0176](https://github.com/swiftlang/swift-evolution/blob/main/proposals/0176-enforce-exclusive-access-to-memory.md)).
- **In-use marks for every generic enum**, so an instance never decides. `Result<int, E>` would pay
  for a check it needs only in a program that replaces a value while matching it. The spec states the
  exception instead.
- **Whole-value `in` and `ref` making an enum in use**, as they make a struct with a destructor.
  `replace` and a plain `state = State.Idle` through a `ref` would always panic, and a whole-value
  reference stays valid anyway.
- **A counter instead of a bit.** Two bytes can be reached by deep recursion, four cannot in practice,
  and integers wrap in a release build, so it would need an overflow check of its own. A bit relies
  on uses nesting and on no panic recovery, which hold today. It must be revisited if coroutines are
  added.
- **`Comparable` by value with `copyable` required.** A unique type could never be compared through
  it. Rust's [`Ord::cmp`](https://doc.rust-lang.org/std/cmp/trait.Ord.html) takes a reference.
- **`in` allowed on a class only when implementing an interface.** It is one more exception to
  remember. Removing the ban makes the modes the same on structs and classes, as
  [C#](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/method-parameters)
  has them, and `ref Node` can reassign a caller's variable, which is useful in its own right.
- **Implementers dropping `in`** from `compareTo(Node other)`. The signature would no longer match the
  interface, and three layers would bridge the difference.
- **Destructors on unique enums, with moves out of them banned.** An enum whose variants can't be
  taken apart has little use, and cleanup can go on a field's type or a wrapping struct. Rust allows
  this and bans the moves; Swift has no `deinit` on enums and lists it as a future direction of
  SE-0432. It can be added later.

## Why

One rule does nearly all the work: a by-value use copies a copyable value and moves anything else,
from a place that can be emptied. Bindings, `try`, `!`, `??`, receivers of `move` methods, and
generic code all follow it, so there is little new to learn and the flow analysis has one thing to
check. Where a value can't be proven stable, the check is the deterministic panic 0028 already chose.

## Open

- Recheck "in use" against `shared class` and `Mutex` when threads are designed: it assumes a unique
  value has one owner at a time, so the bit is not atomic. Settled: a mutex's lock orders the bit
  ([0067](0067-mutex.md)), and an atomic holds only values that never carry it
  ([0069](0069-atomic.md)).
- A function given `ref Handle` cannot replace it, under 0028: `h = x` panics for a type with a
  destructor, so generic code behaves differently for `Handle` and `Buffer`. Left as it is, until
  there is code using destructors to judge it by.
- `List.borrows` is a plain `int`. In a release build it could wrap, though only with a stack of tens
  of gigabytes.
