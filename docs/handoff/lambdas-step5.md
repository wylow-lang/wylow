# Task: escaping functions (lambdas step 5)

You're continuing work on VPL, a personal language for 3D games that compiles to C++. Read
`CLAUDE.md` and `docs/spec/README.md` first, then spec §6.7 (`docs/spec/06-syntax.md`, lambdas
and function types, including "Escaping and non-escaping") and `docs/handoff/lambdas.md`, which
is the plan for the lambdas milestone. `git log` shows how earlier steps were built and tested.

## Where things stand

Steps 1–4 are done: function types, lambdas, non-escaping functions (templates, or
`vpl::FunctionRef` where C++ can't use one), `?:` and switch expressions over functions
(`vpl::Choice`), and generic inference from lambda bodies, including choices of lambdas passed as
arguments. `Result.map` and `mapErr` are in the standard library. No known bugs are open.

Today only parameters and locals hold functions, and they are all non-escaping. `CheckNotStored`
(`Binder.Lambdas.cs`, also reached through `CheckStorable` in `Binder.Spans.cs`) rejects every
other place: fields, return values, type arguments, array elements, and `F?`. That rule is what
makes capturing by reference safe, so this step replaces it with the real one.

## What to build

The design is decisions 4, 6, 11 and 13 in `lambdas.md` and the "Escaping and non-escaping"
section of §6.7. In short:

1. **`@Escaping`** on function-typed parameters and locals, the second accepted annotation
   (`HasCallerLocation` in `Binder.Declarations.cs` is where annotations are checked today).
   Fields, return values, type arguments, and array elements of function type are always escaping.
2. **`vpl::Function<R(A...)>`** in `runtime/vpl.h`: a reference-counted closure object with the
   object header, so the cycle collector can visit what it captures (decision 11). A lambda whose
   target is escaping becomes one; an escaping value can be passed to a non-escaping parameter or
   bind a non-escaping local, never the other way round, and storing a non-escaping one is an
   error that suggests `@Escaping`.
3. **Captures by copy** for escaping lambdas (decision 6): every captured local or parameter must
   be effectively final (assigned only at its declaration, never passed by `ref`) and is read-only
   inside. They can't capture `ref`s, spans, `ref struct`s, non-escaping functions, or a struct
   method's `this`. A captured class reference, including `this`, keeps its object alive.
4. **The places that hold functions**: fields (assigned by every constructor, since function types
   aren't defaultable), returns, `List<(int) -> int>` and other type arguments, arrays (with
   `((int) -> int)?[]` for `new`), and nullable functions `((int) -> int)?`, which are nullable
   values narrowed by null checks.
5. **Choices of escaping values.** `vpl::Choice` makes every option up front, which is only
   unobservable for lambdas and locals; `CppEmitter.Choice` throws on anything else. Fields and
   calls have effects, so an escaping choice must evaluate only the chosen option.
6. **Cycles**: `CyclicClasses` treats an escaping function type as able to reach every class
   (decision 11); a closure capturing `this` (`this.onHit = () -> react(this);`) must be collected
   by `Memory.collectCycles`. Run every cycle test under ASan.

Out of scope: function references (step 6), the rest of the standard library (step 7), lambdas
held by `ref struct`s, and the performance notes in `lambdas.md` (8 and 9 describe a closure that
captures nothing not allocating, and a null closure pointer standing for null; both are
optimizations for later, so keep the representation simple and leave room for them).

## Decisions to settle before coding

Write each down, with the simplest rule that is sound, and put it in the spec:

- **How a C++ lambda becomes a visitable closure.** The cycle collector needs to visit captured
  class references, which a plain C++ lambda can't expose. Probably the emitter generates a
  closure class per escaping lambda with its captures as fields; check what `CppEmitter.Cycles.cs`
  needs.
- **When a lambda is escaping**: from its target alone (decision 2), including through `?:`,
  switch expressions, generic inference, and pending arguments from step 4.
- **What "effectively final" means precisely** with flow analysis's existing facts
  (`LocalSymbol.IsAssignedByLambda`, definite assignment), and what the error says.
- **Escaping choices** (part 5 above): the C++ form, and that conditions stay lazy.
- **Function types as type arguments**: what `List<(int) -> int>` instantiates in C++, and whether
  generic code (`T` that may be a function) needs anything, such as equality (function values have
  no `==`).

## How to work

- Read the code before designing: `Binder.Lambdas.cs` (`CheckNotStored`, `BindLambda`),
  `Binder.Spans.cs` (`CheckStorable`), `FlowAnalyzer.cs` (captures, `IsAssignedByLambda`),
  `CppEmitter.Lambdas.cs` (lambdas, `FunctionRef`, `Choice`), `CppEmitter.Cycles.cs`,
  `CyclicClasses.cs`, `ElementAliasing.cs` (decision 9's union of what lambdas write), and the
  `FunctionRef` and `Choice` sections of `runtime/vpl.h`.
- Tests: behavior tests in `tests/cases/lambdas/`, cycle tests in `tests/cases/cycles/`, and an
  error test for every new or changed error, listed exactly. Many current errors in
  `errors/lambda_escaping.vpl` ("cannot be ... yet") go away or change; replace them with the real
  rules.
- Run the full suite on MSVC and g++ (commands in `CLAUDE.md`). Check that the generated C++ for
  new tests compiles warning-free under the strict g++ flags, and run new programs under ASan as
  `CLAUDE.md` describes, checking that ASan is live.
- Try to break it: a closure outliving the function that made it, a closure stored in a field of
  the object it captures, closures in lists that are cleared during a call of one, a non-escaping
  value smuggled into a field through `?:` or a generic, captures of a loop variable, and
  reference counts across `FunctionRef` and `Choice`. Stress testing found real bugs in every
  earlier step, including a use-after-free.
- Update spec §6.7 (remove "Implemented so far" limits as they land) and `lambdas.md` (mark step 5
  done, move decisions 4, 6, 11 and 13 to the landed list, update review item 3 on `F?`).
- Commit each coherent part once it passes on both compilers. Don't push.

## Working with the user

- Explain decisions plainly and briefly, with a recommendation rather than a survey of options.
  Show a short example when a rule is abstract.
- When something is deferred, say whether it is a known gap with a design (queue it) or an open
  question that needs real use to answer. Don't park obvious gaps as "wait and see".
- Report honestly: what was built, what was decided and why, any bugs found, and what is left.
