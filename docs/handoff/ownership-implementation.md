# Next: implement ownership, step 1

Load the `rigor` skill first. Repo `E:\Repos\Wylow\wylow`, branch `dev`. The design of how a unique
value gets out is decided: the rules are in the spec, and the reasons are in decisions 0049 and 0050.
Read them, then the steps under "Ownership" and "Suggested order" in issue #6.

The order is chosen: concrete types first, then generic code. Check the roadmap's first item (fuzz and
generate on the double-literal and `loop` changes) before starting.

## Step 1: moves on concrete types

- One answer to whether a type is copyable (`types.copyable`).
- `unique struct` and `unique enum`. `unique` is a reserved word today (`Lexer.cs`).
- Moves in the flow analysis, with use after move as a catalog error.
- A temporary error for a unique type argument: generic code still copies until step 3, so
  `List<Buffer>` can't work yet. Step 3 deletes it.

## What the back end has

`IrOwnership.Move` is documented for temporaries, the verifier's "used after it is moved" is an
internal check rather than a catalog error, and `copyable` and `sendable` appear nowhere in `src`.
IR is built once per generic with `T` abstract, and the C++ is a template, so copy or move can be
decided once, in lowering and the flow analysis. Today `T y = x` copies, with or without `--no-opt`.

## Tests

None of 0049 or 0050 has been compiled, and the spec's examples of them are marked `unchecked`, so
expect implementation to find mistakes in the rules. Fix the spec and write a new record when it does.
Each step needs tests for its behavior and each error, its row or column in the matrices, and a fuzzer
mutation where one can cross it. Cases to try in every step:

- A move on one path of an `if`, a `switch`, or a loop, and assigning the variable again afterwards.
- A move in a later operand while an earlier one refers to the value: `mix(buffer, next())`.
- A panic halfway through a call whose arguments have been moved.
- A lambda that runs during the call, escaping and not, capturing the moved value.
- Release order, printed, under `--no-opt` and `--no-nest` as well: the output must not change.
- A class inside the unique value, to show when it is released before destructors exist.
- For by-value receivers (step 2): a copyable receiver stays usable, a receiver on a field is an error,
  `a?.m()` and `a!.m()` move `a`, and an escaping lambda may capture the receiver and move it.

## Things later steps will run into

- `T y = x` becoming a move (step 3) breaks generic code that reads a `T` out of a field, element, or
  reference, or uses one twice: `tests/cases/generics/functions.wyl` (`swap`, `middle`),
  `generics/comparable.wyl` (`T moved = items[j]`), `List.first` and `last`, and `List.removeAt`,
  which needs `memory::moveOut`. The total across the tests wasn't measured.
- How `List.first` and `last` change is undecided. `ref? T`, as `List.get` returns, is the leading
  option; check how callers use them, recommend one, and let the user decide before step 3.
- `Result`'s methods use `case Ok(var value)` on `this` (`Stdlib/result.wyl`), which is an error for a
  moving binding, so they become by-value receivers in step 3.
- `in var` in a case isn't implemented, and the compiler reports `WYL0385` (step 2).
- `ref` and `in` on a concrete class have never reached the back end, because `WYL0409` stops them;
  only the generic path has run. 7 test lines expect the error on a named class type, and those on
  function types stay errors. Update the page of `WYL0409` with the code, since the spec already
  allows them.
- The spec's compiled `maxOf` example (`generics.bounds`) stays valid only once `compareTo` takes
  `in T`, so change `Comparable` and its implementers in the same change as `copyable` checking. The
  implementers are `string.wyl`, `primitives.wyl`, and the tests' `Version` and `Score`.
- Test the order in `consume(buffer, try parse())` when `parse` fails: `buffer` is in a temporary and
  is released by the return, before the locals.
- `docs/spec/13-expressions.md` says only a struct or class has a `this`, but enum methods use it
  (`enums.methods`). Fix the wording when step 2 touches receivers.
