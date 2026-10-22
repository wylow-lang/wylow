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
   call writes what any lambda or function reference in the program writes (their union), which is
   sound and simple.

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
   inherited from a base class), in recursive functions, in constructors, and in function types.
   Functions with function-typed parameters get a name suffix encoding them, so overloads
   differing only in function types stay distinct in C++.
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
     value as writing what any lambda writes. Loops inside lambdas are checked too.
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
   (`vpl::choose_held`). Queued, with designs in §6.7: non-escaping nullable parameters and
   `@Escaping` on a function type's own parameters.
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

## Performance notes

Costs the current design adds, and what could remove them. None of these changes behavior, so each
can be done when profiling shows it matters (see the analysis-based optimizations note: semantics
stay explicit, optimizations come later).

1. **Indirect calls where templates can't be used.** Virtual and interface methods, class methods
   implementing an interface's, recursive functions, constructors, and function types' own
   function parameters take a `vpl::FunctionRef`: one indirect call per call of the function, and
   no inlining of the lambda. Could use: devirtualizing calls whose receiver type is known; for
   recursion, detecting that the recursive call only passes its own parameter on (the common
   case), which can't instantiate without end; for constructors, mangling a tag argument instead.
2. **Code size.** Each distinct lambda passed to a template instantiates the callee again, as
   generics do per type. Large functions called with many lambdas grow the binary; a performance
   warning could point out large instantiation counts.
3. **Pins for locals a lambda assigns.** Such a local is no longer "stable", so using it as a
   receiver or argument holds an extra reference (refcount inc/dec) for the call, even when the
   lambda that assigns it can't run during that call. Could be precise: only calls that are
   passed (directly or through forwarding) a lambda assigning the local can change it.
4. **Evaluation order is conservative.** Any call given a function value, and any call of one, is
   treated as writing every local, so operands around it get hoisted into an ordering lambda
   (`CppEmitter.Ordered`). Usually inlined away, but it can also force temporaries. Could track
   which locals each lambda writes.
   - The wrapper also nests a C++ block, so each level of nested lambdas can cost two. About 25
     levels exceed MSVC's limit on block nesting ("blocks nested too deeply"). Deeply nested
     statements can hit the same limit; real code doesn't nest that deep, so this is a known
     limit, not a planned fix.
5. **Element aliasing is program-wide for function calls.** A call of a function value merges
   what *every* lambda in the program writes, so one lambda storing into elements of type `X`
   anywhere disables in-place loop elements in every loop over `X` that calls a function value.
   Could narrow to the lambdas that can reach that call.
6. **Narrowing is conservative** (decision 10): a null check outside a lambda doesn't carry in,
   and a local any lambda assigns is never narrowed, which can force `!` (a check) where it isn't
   needed.
7. **`vpl::unreachable` after each non-void lambda body**, as after functions; dead code the C++
   compiler removes.
8. **Escaping lambdas that capture nothing** (step 5) needn't allocate. The likely form is to make
   `vpl::Function` a code pointer plus a closure pointer that is null when nothing is captured, so
   copying and releasing one only test for null.
9. **Nullable functions** (step 5) are nullable values, `vpl::Optional<...>`, with a separate
   presence flag; a null closure pointer could stand for null instead.

## Decisions to review once lambdas are in use

These were reasonable on paper; real programs (the voxel port) will show whether they get in the way.

1. **Bind-once function locals** (decision 5). A function-typed local can't be reassigned; `?:`
   and switch expressions cover choosing one. Since step 4, a choice passed as an argument waits
   for overload resolution and generic inference as a lambda does.
2. **Narrowing rules** (decision 10): stricter than needed; carrying a check on an effectively final
   local into a lambda would be sound.
3. **`F?` is a nullable value** (`vpl::Optional<vpl::Function>`), not reference-like, and always
   escaping. Performance note 9 could make null a null closure pointer; a non-escaping `F?`
   parameter is planned (§6.7).
4. **Untyped lambdas are ambiguous between overloads** that differ only in function parameter
   types (decision 7), rather than trying each overload.
5. **Parameter modes are part of a function type**, and an untyped lambda takes them from its
   target; function-typed parameters can't be `ref` or `in`.
6. **Expression-bodied lambdas returning `void`** must have a call as their body ("only calls can
   be used as statements"), as for statements.
7. **Which functions get `FunctionRef`** (performance note 1): the rules are simple and
   conservative, and could be wrong in the slow direction often enough to matter.
8. **Inferring from a lambda's body needs one overload left** (step 4). Two generic overloads
   whose lambdas take the same number of parameters, or a generic and a non-generic one, make
   `x.map(m -> m.name)` an error asking for type arguments. Binding the body once per distinct
   parameter-type list, and sharing it, would lift that if real overload sets need it.
