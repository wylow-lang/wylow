# Lambdas and function types

Goal: function types `(A, B) -> R`, Java-style lambdas, non-escaping function parameters that
allocate nothing, and escaping closures for fields, locals, return values, and containers. The
design is in [06-syntax.md](../spec/06-syntax.md#67-lambdas-and-function-types) (§6.7); this file
fills in what the spec leaves open and orders the work. Each decision below goes into the spec
when it lands, and is then only listed here.

## Decisions

Landed, and described in §6.7: 1 (function type syntax), 2 (a lambda takes its type from its
target), 3 (non-escaping parameters are templates, or `vpl::FunctionRef` where C++ can't use one),
5 (non-escaping locals), 7 (overloads), 8 (generic inference, including from lambda bodies), and
10 (flow analysis).

4. **Escaping values are `vpl::Function<R(A...)>`**, a reference-counted closure object.
   - Function types are not defaultable: a field of one must be set by every constructor, or be
     nullable. `new ((int) -> int)[n]` is an error; use `((int) -> int)?[]`.
   - Function values have no `==`.
   - A non-escaping value never converts to an escaping one. An escaping value can be passed to a
     non-escaping parameter or bind a non-escaping local.
6. **Captures.**
   - Non-escaping lambdas capture by reference. They may mutate captured locals and capture `ref`s,
     spans, ref structs, non-escaping parameters and locals, and a struct method's `this`.
   - Escaping lambdas capture by copy. Every local or parameter they capture must be effectively
     final (assigned only at its declaration and never passed by `ref`), and is read-only in the
     lambda. They may not capture any of the things only non-escaping lambdas can capture.
   - A captured class reference, including `this`, keeps its object alive.
   - A lambda can't refer to the variable it initializes, since that variable isn't assigned yet.
9. **Effects.** Evaluation order (`HasEffects`, `Writes`, `ReadsState`) treats a call through a
   function value as able to write anything. For `ElementAliasing`'s whole-program summary, such a
   call writes what any lambda or function reference in the program writes (their union), which is
   sound and simple.
11. **Cycles.** A closure that captures class references can form a cycle
    (`this.onHit = () -> react(this);`). Closures carry the object header and are visited by the
    cycle collector. `CyclicClasses` treats an escaping function type as able to reach every class,
    as it already does for generic instantiations that only C++ sees. A precise version can come
    later.
12. **Function references.** A free function or static method's name converts to a function type
    (`list.sort(byDepth)`). If the name is overloaded, the target type picks the overload.
    References to a method on an instance (`enemy::update`) are not in this milestone.
13. **`@Escaping`** is the second accepted annotation, allowed only on function-typed parameters and locals.

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
   - Escape checks: until step 5, only parameters and locals hold functions (`Binder.CheckNotStored`).
     Fields, returns, type arguments, arrays (also inferred by array literals), and `F?` are
     errors. That is what makes capturing by reference safe.
   - `?:` and switch expressions over functions (added after step 3) are a `vpl::Choice`
     (`CppEmitter.Choice`): every lambda or local it could be, and the index the conditions and
     cases pick. A switch's cases run in a C++ lambda, so a chosen lambda can't use a name its
     case declares (`Binder.CheckCaseCaptures`). Only the conditions are evaluated lazily, which is
     unobservable because making a lambda or copying a local has no effects. Escaping values
     (fields, calls) would break that; `Choice` throws on them until step 5 handles them.
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
5. **Escaping.** `@Escaping`, `vpl::Function`, by-copy captures and the effectively-final rule,
   cycle collector support. Run the cycles under ASan.
6. **Function references.**
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
3. **`F?` is a nullable value**, not reference-like. Only matters once functions can be stored.
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
