# Next: implement ownership, step 2

Repo `E:\Repos\Wylow\wylow`, branch `dev`. Step 1 is done: `unique struct` and `unique enum`,
one answer to whether a type is copyable, and moves on whole locals and by-value parameters in
lowering and the flow analysis. Read decisions 0049 and 0050, the spec sections step 2 names,
and the steps under "Ownership" and "Suggested order" in issue #6.

## Step 2: getting a value out of a concrete type

- Moving bindings (`patterns.bindings.moves`): a by-value binding of a part whose type is not
  copyable moves the whole matched local or by-value parameter, on the path where the case is
  chosen or the `is` test is true, and on no other path. What the pattern does not move leaves
  the value when the move happens and is released right after the pattern's names go out of
  scope. In a guard a moving binding is a read-only reference; the move happens when the guard
  is true.
- `in var` and `ref var` in cases (`WYL0385` today: the parser allows them only in an `is`),
  with `in` on a variant field and `ref` on a struct pattern's field.
- By-value receivers (0050): `this T name` as the first parameter of a struct or enum method.
  It is an instance method, has no `this`, is not `mut` or `static`, cannot implement an
  interface method, and is not one of the parameter types for signatures. A copyable receiver
  stays usable; a receiver on a field is an error; `a?.m()` and `a!.m()` move `a`; an escaping
  lambda may capture and move the receiver. Fix `docs/spec/13-expressions.md`, which says only a
  struct or class has a `this`, while enum methods use it.
- `a!`, `??`, `?.`, and `try` use their operand by value, so each moves a unique one
  (`expressions.null`, `expressions.try`).

## What step 1 built

- `TypeCopying.cs`: `IsCopyable` (a unique struct or enum, anything holding one inline, or a type
  parameter, is not copyable) and `IsUniqueType` (a unique declaration, or a fixed array,
  nullable value, or struct or enum holding one). The binder's field rule asks `IsUniqueType`;
  the verifier's copy check asks `IsCopyable`.
- Lowering's `Moves` (`Lowering.cs`) is `IsUniqueType`, not `!IsCopyable`: an unbounded `T` is
  not copyable, but generic code still copies it until step 3. `Read` returns a borrow for a
  unique place (it is used in place), and the copy sites transfer it to a move: `OperandOf`,
  `Kept`, `ReadWhole`, `Transferred` (an array's element, a variant's field), and an escaping
  lambda's captures.
- The verifier tracks where each local was moved (`State.Moved`), and reports `WYL0440` (use
  after move), `WYL0441` (moving out of a field, element, or reference), `WYL0443` (moving while
  an earlier operand refers to it), and `WYL0444` (copying a unique value). A moved local is not
  released, and assigning it a value again clears the move.
- Two temporary errors go when their step lands: `WYL0442` (a unique type argument, step 3) and
  `WYL0444` (a by-value pattern binding of a unique value; moving bindings replace it). Delete
  each code, its page, and its test with the work.
- The types matrix has a `unique` row that skips four contexts (`Skips` in `MatrixTypes.cs`)
  until moving bindings and generic moves reach them; add them back. The fuzzer has the
  `type-unique` mutation.

## Things later steps will run into

- `T y = x` becoming a move (step 3) breaks generic code that reads a `T` out of a field,
  element, or reference, or uses one twice: `tests/cases/generics/functions.wyl` (`swap`,
  `middle`), `generics/comparable.wyl` (`T moved = items[j]`), `List.first` and `last`, and
  `List.removeAt`, which needs `memory::moveOut`. The total across the tests wasn't measured.
- How `List.first` and `last` change is undecided. `ref? T`, as `List.get` returns, is the
  leading option; check how callers use them, recommend one, and let the user decide before
  step 3.
- `Result`'s methods use `case Ok(var value)` on `this` (`Stdlib/result.wyl`), which is an error
  for a moving binding, so they become by-value receivers in step 3.
- `ref` and `in` on a concrete class have never reached the back end, because `WYL0409` stops
  them; only the generic path has run. 7 test lines expect the error on a named class type, and
  those on function types stay errors. Update the page of `WYL0409` with the code, since the
  spec already allows them.
- The spec's compiled `maxOf` example (`generics.bounds`) stays valid only once `compareTo`
  takes `in T`, so change `Comparable` and its implementers in the same change as `copyable`
  checking. The implementers are `string.wyl`, `primitives.wyl`, and the tests' `Version` and
  `Score`.
- Test the order in `consume(buffer, try parse())` when `parse` fails: `buffer` is in a
  temporary and is released by the return, before the locals.
