# Next: implement ownership, step 3

Repo `E:\Repos\Wylow\wylow`, branch `dev`. Step 2 is done. Read decisions 0049 and 0050, the spec
sections step 3 names, and the steps under "Ownership" and "Suggested order" in issue #6.

## Step 3: generic code

- Unbounded `T` moves, and the `copyable` bound (0036): `T y = x` moves for every unbounded `T`,
  and a parameter or type argument that is copied says `copyable`. Lowering's `Moves`
  (`Lowering.cs`) is `IsUniqueType` until this lands; it becomes "not copyable", and generic code
  that copies a `T` stops binding where it copies one. Measure the total first: the handoff of
  step 1 named `tests/cases/generics/functions.wyl` (`swap`, `middle`), `generics/comparable.wyl`
  (`T moved = items[j]`), and `List.first`, `last`, and `removeAt`.
- `Comparable.compareTo` takes `in T`, and `ref`/`in` on class and interface types (`WYL0409`).
  The spec's compiled `maxOf` example (`generics.bounds`) stays valid only once `compareTo` takes
  `in T`, so change `Comparable` and its implementers in the same change as `copyable` checking:
  `string.wyl`, `primitives.wyl`, and the tests' `Version` and `Score`.
- Migrate `std` and the tests: `Result`'s `orElse`, `map`, `mapErr`, and `orPanic` become by-value
  receivers (`this Result<T, E> result`), because `case Ok(var value)` on `this` becomes an error
  for a moving binding; `List.first` and `last`; `List.removeAt` with `memory::moveOut`.
- Delete `WYL0442` (a unique type argument), its page, and its test, with the work. Remove the
  `type-argument` skip from the types matrix's unique row.
- Test the order in `consume(buffer, try parse())` when `parse` fails: `buffer` is in a temporary
  and is released by the return, before the locals.

## Decide first: `List.first` and `last`

They copy `T`, and a member can't state a bound, so they cannot say `copyable`. Returning `ref? T`,
as `List.get` does, is the leading option; check how callers use them, recommend one, and let the
user decide before starting the migration.

## What step 2 built

- **Moving bindings.** A by-value binding of a part whose type is `IsUniqueType` moves the whole
  matched local, by-value parameter, or temporary, where its case is chosen or its `is` is true; in
  a guard it is a read-only reference and the move happens when the guard is true. Lowering's
  `Binding` is `(Local, Func<IrPlace> Place, bool Moves, RefKind Reference)`; `helds` holds the
  moved value for an `is`, and `Cases`/`Guarded` declare theirs in the case's block. What the
  pattern leaves is held until the names' scope ends.
- **The flow analysis keeps moves per outcome.** `Known` has `Moved`, and `State.TakeMoved` sets a
  branch's moved set from its condition's outcome, so `if (x is P(var b)) ... else use(x)` accepts
  the else. `State.ForgetMoved` clears a move when a store gives the value back.
- **Reference patterns** in cases (`in var`, `ref var`) bind a field or the whole matched value:
  `BoundReferencePattern` with a `NullableReferenceType` local flagged `IsPatternBinding`, lowered
  to an `IrAddr`. A variant's field is `in` only (`WYL0445`), and `ref` needs a changeable place
  (`WYL0408`/`WYL0407`). `WYL0385` and `WYL0444` are retired; the copy of a non-copyable value is a
  compiler-internal error now.
- **By-value receivers** (`this T name`): `FunctionSymbol.ReceiverParameter` is kept out of
  `Parameters`, the lowered function holds it first, an `IrCall` passes it as argument 0, and the
  C++ method is `static`. `MethodReference.ReceiverCount`/`ArgumentType` line arguments up with
  parameters. `WYL0446`–`WYL0452` are new. Extension methods still do not exist: a free function's
  receiver is an error.
- **The null operators.** `a!` used as a value and `??` move a unique operand; `?.` uses it in
  place, or moves it when the member takes its receiver by value. `a!` on a nullable value is now a
  place, so `a!.field` is reached in place.
- The types matrix's unique row skips only `type-argument` now.

## Still open

- `try`'s operand moves a unique value, but no program can test that until a unique type argument
  is allowed (step 3).
- The held value of an `is` in a `while` test is released at the end of the loop, not of each
  iteration as `patterns.bindings.moves` says; nothing observes it until destructors exist (step 4).
- A `ref` guard binding into a value that the guard changes through another path is rejected by the
  verifier's dependency check, not by a rule of its own.
- A reference binding into a value that may be replaced, one in an object or reached through a
  `ref` or `in`, refers into it in place. `patterns.references` says a copyable value is copied into
  a local first, so a program that writes the value while the binding is in use is rejected here
  rather than accepted. The in-use bit of step 4 is where the two meet.
