# Next: implement ownership, step 4

Repo `E:\Repos\Wylow\wylow`, branch `dev`. Step 3 is done. Read decisions 0028, 0031, 0044,
0049, and 0052, the spec sections step 4 names, and the steps under "Ownership" in issue #6.

## Step 4: places and destructors

- `replace`, `swap`, and `take` (`library.replace`): they take a value out of a place and panic on
  a value in use. Add the per-type check that looks through a value's fields for them.
- The in-use bit (`memory.holds`): one bit per value, saved in a hidden local when a use starts
  and restored when it ends. It usually fits an enum's tag and `Optional`'s `has_`; a struct with a
  destructor needs a byte and its padding. Replacing a value in use panics; `replace`/`swap`/`take`
  do too. The optimizer may skip a bit where it proves nothing can replace the value.
- Destructors on `unique struct` only (0049: unique enums have none), and `discard`:
  - `WYL0146` changes to allow destructors on unique structs.
  - New codes for `discard` of anything but a by-value parameter of a unique type with a
    destructor, `discard` outside the type's file, and moving out of `this` in a destructor.
- Tests for each behavior and error, its rows in the matrices, and a fuzzer mutation where one can
  cross it.

## What step 3 built

- **The `copyable` bound (0036, 0049).** `T extends copyable` is parsed by `BindBounds` and stored
  on `TypeParameterSymbol.IsCopyableBound`; `TypeCopying.IsCopyableIn(function)` is the one answer.
  An unbounded `T` moves; a copyable one copies. Lowering's `Moves` is `!IsCopyableIn(symbol)`, and
  the verifier checks a copy against the function. A type that does not satisfy the bound is
  `WYL0453`; `WYL0442` is retired.
- **Member bounds (0052).** A method or constructor may write `where T extends Bound & Bound` after
  its parameter list; the word is contextual. `FunctionSymbol.CopyableParameters` and
  `MemberInterfaceBounds` hold it; `ApplyMemberBounds` merges it while the body and defaults are
  bound, `CheckMemberBounds` checks each call and construction. New codes: `WYL0454` (misplaced),
  `WYL0455` (unknown parameter), `WYL0456` (use does not satisfy it).
- **`in T` and references on classes.** `Comparable.compareTo` takes `in T`; `ref`/`in` are
  allowed on class and interface types, and `WYL0409` now reports only function types.
- **`std`.** `Result`'s `orElse`, `map`, `mapErr`, and `orPanic` take their receiver by value;
  `List.first`/`last` return `ref? T`; `List.removeAt` moves out with `memory::moveOut`; `Array`'s
  value constructor is `where T extends copyable`, so `new Array<T>(n, value)` is unchanged.
- **Lambda captures.** A lambda that moves a value it captures is an error (`WYL0457`); this was in
  the spec but not enforced, and generic `T` made it observable at run time.
- **Emitter.** A guard's reference local gets its own C++ name, and a null-conditional's temporary
  no longer takes its target's source text as a name.

## Still open

- `try`'s operand moves a unique value; unique type arguments now exist, so a test can reach it.
- The held value of an `is` in a `while` test is released at the end of the loop, not of each
  iteration as `patterns.bindings.moves` says; nothing observes it until destructors exist.
- A `ref` guard binding into a value that the guard changes through another path is rejected by
  the verifier's dependency check, not by a rule of its own.
- A reference binding into a value that may be replaced, one in an object or reached through a
  `ref` or `in`, refers into it in place. `patterns.references` says a copyable value is copied into
  a local first; the in-use bit of step 4 is where the two meet.
- A function given `ref Handle` cannot replace it (0028), so generic code behaves differently for a
  type with a destructor. `replace`, `swap`, and `take` in step 4 settle what a function may do.
