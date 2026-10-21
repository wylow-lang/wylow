# Lambdas and function types

Goal: function types `(A, B) -> R`, Java-style lambdas, non-escaping function parameters that
allocate nothing, and escaping closures for fields, locals, return values, and containers. The
design is in [06-syntax.md](../spec/06-syntax.md#67-lambdas-and-function-types) (§6.7); this file
fills in what the spec leaves open and orders the work. Each decision below goes into the spec
when it lands.

## Decisions the spec doesn't cover yet

1. **Function type syntax always has parentheses**: `(T) -> bool`, `() -> void`, nullable
   `((int) -> int)?`. A bare `T -> bool` would be ambiguous in declarations. Parameter modes are
   part of the type: `(ref Vec3) -> void`. (Done, with step 1.)
2. **A lambda has no type of its own.** It takes one from its target: a declared variable, field,
   parameter, return, or another lambda's body. `var f = x -> x;` is an error. Parameter types
   come from the target, or may be written (`(int a, int b) -> a + b`) to choose between overloads.
3. **How non-escaping parameters compile.** In a non-virtual function, a function-typed parameter
   is a template parameter of the C++ function (one instantiation per lambda, so the optimizer can
   inline it), as generic functions are. A `virtual`, `abstract`, or interface method can't be a
   template (the reason generic methods can't be virtual, 02-types.md §2.10), so its non-escaping
   parameters are `vpl::FunctionRef<R(A...)>`, a non-owning callable pointer like
   `llvm::function_ref`. VPL code can't tell the two apart.
4. **Escaping values are `vpl::Function<R(A...)>`**, a reference-counted closure object.
   - Function types are not defaultable: a field of one must be set by every constructor, or be
     nullable. `new ((int) -> int)[n]` is an error; use `((int) -> int)?[]`.
   - Function values have no `==`.
   - A non-escaping value never converts to an escaping one. An escaping value can be passed to a
     non-escaping parameter or bind a non-escaping local.
5. **Non-escaping locals** (decided: locals follow the parameters' rule, and `@Escaping` opts in;
   now in §6.7). Like a `ref` local, a non-escaping local must be initialized at its declaration
   and can't be reassigned, so it never outlives what its lambda captures by reference.
   Initialized with a lambda, it compiles to the C++ lambda itself (`auto`). Otherwise (from a
   parameter, or `?:` over two lambdas) it is a `vpl::FunctionRef`, with each lambda the `?:`
   chooses from stored in an unnamed local beside it.
6. **Captures.**
   - Non-escaping lambdas capture by reference. They may mutate captured locals and capture `ref`s,
     spans, ref structs, non-escaping parameters and locals, and a struct method's `this`.
   - Escaping lambdas capture by copy. Every local or parameter they capture must be effectively
     final (assigned only at its declaration and never passed by `ref`), and is read-only in the
     lambda. They may not capture any of the things only non-escaping lambdas can capture.
   - A captured class reference, including `this`, keeps its object alive.
   - A lambda can't refer to the variable it initializes, since that variable isn't assigned yet.
7. **Overloads.** Candidates whose function-typed parameter doesn't match a lambda's parameter
   count are discarded. If more than one candidate is left and they differ only in that function
   type's parameter types, the call is ambiguous unless the lambda declares its parameter types.
   Each lambda is bound once, never once per candidate.
8. **Generic inference** has two phases. Type arguments are first inferred from the arguments that
   aren't lambdas. Then each lambda is bound with its parameter types substituted, and its body's
   type infers what's left. `Result.map<U>((T) -> U)` needs this.
9. **Effects.** Evaluation order (`HasEffects`, `Writes`, `ReadsState`) treats a call through a
   function value as able to write anything. For `ElementAliasing`'s whole-program summary, such a
   call writes what any lambda or function reference in the program writes (their union), which is
   sound and simple.
10. **Flow analysis** (done in step 2). A captured local must be definitely assigned where the
   lambda is. No narrowing from outside holds inside a lambda, and a local any lambda assigns is
   never narrowed, since the lambda may run during any call. This also keeps a lambda from
   referring into a nullable value (`BoundTree.NullableValueRoot`). It is stricter than needed:
   a narrowing of an effectively final local could carry into the lambda, if that turns out to
   matter. `return` in a lambda returns from the lambda. `break` and `continue` can't leave it.
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
   flow analysis (decision 10). `Binder.RejectFunctionUses` then rejects the program; step 3
   removes it.
3. **Non-escaping.** Template and `FunctionRef` emission, by-reference captures, escape checks,
   and effects and ordering (decision 9), including `ElementAliasing`.
4. **Generic inference** with lambdas (decision 8). Inferring from the other arguments already
   works; what's left is inferring from a lambda's body, as `Result.map<U>((T) -> U)` needs.
5. **Escaping.** `@Escaping`, `vpl::Function`, by-copy captures and the effectively-final rule,
   cycle collector support. Run the cycles under ASan.
6. **Function references.**
7. **Standard library.** `Result.map` and `mapErr`, `List.removeWhere`, and a `sort` on spans that
   takes a comparison, all written in VPL.

Each step lands with tests (behavior and every error), passes on MSVC and g++, and updates the
spec.

## Not in this milestone

Extension methods and queries (they come next, on top of this), lambdas held by `ref struct`s,
the capture rules for jobs, threads, and isolates, references to instance methods, and converting
lambdas to C callbacks (07-interop.md §7.6 forbids it).

Also not in this milestone: escaping lambdas that capture nothing skipping allocation. This is an
optimization that never changes behavior, so it can come any time. The likely form is to make
`vpl::Function` a code pointer plus a closure pointer that is null when nothing is captured, so
copying and releasing one only test for null.
