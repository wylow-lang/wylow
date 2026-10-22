# Lambdas and function types

Goal: function types `(A, B) -> R`, Java-style lambdas, non-escaping function parameters that
allocate nothing, and escaping closures for fields, locals, return values, and containers. The
design is in [06-syntax.md](../spec/06-syntax.md#67-lambdas-and-function-types) (§6.7); this file
fills in what the spec leaves open and orders the work. Each decision below goes into the spec
when it lands, and is then only listed here.

## Decisions

Landed, and described in §6.7: 1 (function type syntax), 2 (a lambda takes its type from its
target), 3 (non-escaping parameters are templates, or `vpl::FunctionRef` where C++ can't use one),
4 (escaping values are `vpl::Function`), 5 (non-escaping locals), 6 (captures), 7 (overloads), 8
(generic inference, including from lambda bodies), 10 (flow analysis), 11 (cycles), 12 (function
references), and 13 (`@Escaping`).

9. **Effects.** Evaluation order (`HasEffects`, `Writes`, `ReadsState`) treats a call through a
   function value as able to write anything. For `ElementAliasing`'s whole-program summary, such a
   call writes what any lambda or function reference of its type writes (their union).

## Order of work

1. **Parsing** (done). Function types, including as type arguments and nullable. Lambdas: `x -> ...`,
   `() -> ...`, `(a, b) -> ...`, and `(int a) -> ...`, each with an expression or block body. To
   tell a lambda from a parenthesized expression, look ahead for `)` followed by `->`.
2. **Types and binding** (done). Function types in `Types.cs` (interning, `TypeMap` substitution,
   equality, not defaultable), target typing (a lambda passed to overloads is a
   `BoundPendingLambda` until resolution picks its type), lambda bodies with their own return
   context, calling a function value (`BoundInvocation`), bind-once function-typed locals, and
   flow analysis (decision 10).
3. **Non-escaping** (done). Lambdas are C++ `[&]` lambdas (`CppEmitter.Lambdas.cs`).
   Function-typed parameters are template parameters, or `vpl::FunctionRef` in methods that are
   dispatched or implement an interface's (`FunctionSymbol.ImplementsInterface`, including one
   inherited from a base class), in recursive functions that pass themselves new lambdas, in
   constructors, and in function types.
   Functions with function-typed parameters that share their name and parameter count with
   another get a name suffix encoding them, so overloads differing only in function types stay
   distinct in C++.
   - Escape checks: until step 5, only parameters and locals held functions, which is what made
     capturing by reference safe. Step 5 replaced this with the escaping form of function types.
   - `?:` and switch expressions over functions (added after step 3) are a `vpl::Choice`
     (`CppEmitter.Choice`): every lambda or local it could be, and the index the conditions and
     cases pick. A switch's cases run in a C++ lambda, so a chosen lambda can't use a name its
     case declares (`Binder.CheckCaseCaptures`). Only the conditions are evaluated lazily, which is
     unobservable because making a lambda or copying a local has no effects. Escaping options
     (fields, calls) are observable, so since step 5 they are evaluated only when chosen
     (`vpl::choose_held`).
   - A local a lambda assigns is marked `LocalSymbol.IsAssignedByLambda`. It is not "stable", so it is
     pinned while in use as a receiver or argument (`tests/cases/lambdas/safety.vpl` would otherwise
     read freed memory).
   - Effects and ordering (decision 9): calling a function value, or calling anything with a function
     argument, counts as writing locals.
   - `ElementAliasing` counts a lambda's writes in the function that creates it, and a call of a function
     value as writing what any lambda of its type writes. Loops inside lambdas are checked too.
   - Field initializers are now flow-analyzed, since lambdas in them have bodies to check.
4. **Generic inference** with lambdas (done, decision 8). `Binder.Inference.cs`: a type argument
   only a lambda's return type can give is inferred by binding the lambda's body
   (`BindLambdaInferringReturn`) with its return type left open, once its parameter types are
   known; `RetargetLambda` converts its returns once the type is chosen. Bodies are bound only for
   the one candidate left, so each lambda is still bound once; if several candidates are left,
   the call is an error asking for type arguments. A `?:` or switch expression of lambdas passed
   as an argument is a `BoundPendingChoice` until resolution gives it a type (`Binder.Choose`).
   `Result.map` and `mapErr` are written in VPL.
5. **Escaping** (done). Escaping-ness is part of the function type (`VplType.IsEscaping`), so
   storing, returning, or inferring a type argument from a non-escaping value fails at
   conversion, with an error naming the variable to declare `@Escaping` (`Binder.Escaping.cs`).
   An escaping lambda is a `vpl::closure` (`CppEmitter.Closure`): its captures are copied into a
   reference-counted closure object that the cycle collector visits, and `this` is passed as a
   `vpl::Ref`. Effectively final is `LocalSymbol.IsModified`, set at every mutating use, and checked
   once the whole body is bound (`CheckEscapingCaptures`). Calls of a stored function pin the
   closure; a non-escaping choice evaluates an escaping option only when chosen
   (`vpl::choose_held`). Since then: nullable non-escaping parameters (`vpl::optional_ref`, made
   where the call is, so the reference outlives no temporary), and `@Escaping` on a function
   type's own parameters.
6. **Function references** (done). A function's name used as a value is bound as the lambda that
   calls it (`Binder.FunctionReferences.cs`), so it needs nothing of its own past binding.
7. **Standard library.** `List.removeWhere`, and a `sort` on spans that
   takes a comparison, all written in VPL.

Each step lands with tests (behavior and every error), passes on MSVC and g++, and updates the
spec.

## Not in this milestone

Extension methods and queries (they come next, on top of this), lambdas held by `ref struct`s,
the capture rules for jobs, threads, and isolates, references to instance methods, and converting
lambdas to C callbacks (07-interop.md §7.6 forbids it).

## Costs and open decisions

Moved to §6.7: "Costs to remove later" and "Decisions to revisit".
