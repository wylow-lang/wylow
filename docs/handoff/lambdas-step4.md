# Task: generic inference from lambda bodies (lambdas step 4)

You're continuing work on VPL, a personal language for 3D games that compiles to C++. Read
`CLAUDE.md` and `docs/spec/README.md` first, then spec §6.7 (`docs/spec/06-syntax.md`, lambdas
and function types) and `docs/handoff/lambdas.md`, which is the plan for the lambdas milestone.
`git log` shows how earlier steps were built and tested.

## Where things stand

Steps 1–3 are done: function types, lambdas, and non-escaping functions (templates, or
`vpl::FunctionRef` where C++ can't use one). After step 3, `?:` and switch expressions over
functions were added (`vpl::Choice`, `CppEmitter.Choice`), and lambdas were stress-tested. No
known bugs are open. The user wants each feature finished with no loose ends before the next one
starts, so step 4 includes the one item carried over to it (part 2 below).

## What to build

1. **Infer type arguments from a lambda's body** (decision 8 in `lambdas.md`). Today
   `InferTypeArguments` (`Binder.Overloads.cs`) skips `BoundPendingLambda` arguments, so a type
   parameter that appears only in a lambda's return type can't be inferred:

   ```java
   Result<U, E> map<U>((T) -> U f)
   result.map(x -> x.name)        // error today: cannot infer 'U'
   ```

   The design has two phases: infer what the non-lambda arguments give, then bind each lambda with
   its parameter types substituted, and infer the rest from its body's type. Each lambda must
   still be bound once, never once per overload candidate (decision 7). Decide what happens when
   several candidates are generic and all need a lambda body to finish inferring, and write the
   decision in the spec.

2. **Let a `?:` or switch expression of lambdas wait for its target type the way a lambda does.**
   Today a lambda argument becomes a `BoundPendingLambda` until overload resolution picks its
   type (`BindArguments` in `Binder.Calls.cs`, `LambdaFits` and `BindLambda` in
   `Binder.Lambdas.cs`, the pending case in `Binder.Convert`). A `?:` or switch expression whose
   branches are lambdas gets no such delay, so it is an error when passed to overloads that
   disagree on the function type, or to a generic parameter. Make it pending too, so it works
   wherever a lambda alone would. Its non-lambda branches (function-typed locals) can be bound
   early and constrain the type. Keep the existing rules: a chosen lambda can't use a name its
   switch case declares (`Binder.CheckCaseCaptures`), and conditions are evaluated lazily.

3. **`Result.map` and `Result.mapErr`** in `src/Vpl.Compiler/Stdlib/Result.vpl`, written in VPL, as
   the real use that proves part 1. Read the stdlib section of the spec README first: the standard
   library is ordinary VPL, and compiler special cases are a last resort.

Out of scope: escaping functions (step 5), function references (step 6), and the rest of the
standard library (step 7).

## How to work

- Read the code before designing: `Binder.Overloads.cs` (`Resolve`, `InferTypeArguments`),
  `Binder.Calls.cs` (`BindArguments`, `ExpectedArgumentType`), `Binder.Lambdas.cs`, and
  `BindConditional` / `BindSwitchExpression`. Write down the decisions the plan leaves open, and
  settle them before coding. Prefer the simplest rule that is sound.
- Tests: behavior tests in `tests/cases/lambdas/`, and an error test for every new or changed
  error, listed exactly. Include inference that fails (an ambiguous result, a lambda body that
  doesn't type-check, a type parameter nothing determines) with clear messages.
- Run the full suite on MSVC and g++ (commands in `CLAUDE.md`). Check that the generated C++ for
  new tests compiles warning-free under the strict g++ flags, and run new programs under ASan as
  `CLAUDE.md` describes.
- Try to break it. Write tricky programs beyond the tests: overloads mixing generic and
  non-generic candidates, nested generic calls whose lambdas depend on each other, lambdas whose
  body type needs a conversion (`int` returned where `long` is inferred elsewhere). Stress-testing
  found three real bugs in steps 3 and after, including a use-after-free from milestone 6.
- Update spec §6.7 and `lambdas.md` (mark step 4 done, update decision 8 and review item 1).
- Commit each coherent part once it passes on both compilers. Don't push.

## Working with the user

- Explain decisions plainly and briefly, with a recommendation rather than a survey of options.
- When something is deferred, say whether it is a known gap with a design (queue it) or an open
  question that needs real use to answer. Don't park obvious gaps as "wait and see".
- Report honestly: what was built, what was decided and why, any bugs found, and what is left.
