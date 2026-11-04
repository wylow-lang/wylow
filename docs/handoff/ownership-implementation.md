# Next: start implementing ownership

Load the `rigor` skill first. Repo `E:\Repos\Wylow\wylow`, branch `dev`. The design of how a unique
value gets out is decided: the rules are in the spec, and the reasons are in decisions 0049 and 0050.
Read them, then the order in issue #6's "Suggested order".

## Ask first

0049 and 0050 removed two reasons to build extension methods before ownership: `Result`'s consuming
methods take their receiver by value as members, and `List.first` and `last` may return `ref? T`.
Issue #6 still lists extension methods first. Recommend moving them out of the ownership chain, since
nothing in it now needs their lookup or imports, only the `this T name` parameter on a member, and let
the user decide. They remain the path for `String`'s higher-level methods.

## What the back end has

`IrOwnership.Move` is documented for temporaries, the verifier's "used after it is moved" is an
internal check rather than a catalog error, and `copyable` and `sendable` appear nowhere in `src`.
IR is built once per generic with `T` abstract, and the C++ is a template, so copy or move can be
decided once, in lowering and the flow analysis. Today `T y = x` copies, with or without `--no-opt`.

## Things 0049 will run into

- `T y = x` becoming a move breaks generic code that reads a `T` out of a field, element, or
  reference, or uses one twice: `tests/cases/generics/functions.wyl` (`swap`, `middle`),
  `generics/comparable.wyl` (`T moved = items[j]`), `List.first` and `last`, and `List.removeAt`,
  which needs `memory::moveOut`. The total across the tests wasn't measured.
- `in var` in a case isn't implemented, and the compiler reports `WYL0385`.
- `ref` and `in` on a concrete class have never reached the back end, because `WYL0409` stops them;
  only the generic path has run. About 6 test lines expect the error. Update the page of `WYL0409`
  with the code, since the spec already allows them.
- The spec's compiled `maxOf` example (`generics.bounds`) stays valid only once `compareTo` takes
  `in T`, so change `Comparable` and its implementers in the same change as `copyable` checking. The
  implementers are `string.wyl`, `primitives.wyl`, and the tests' `Version` and `Score`.
- Test the order in `consume(buffer, try parse())` when `parse` fails: `buffer` is in a temporary and
  is released by the return, before the locals.
